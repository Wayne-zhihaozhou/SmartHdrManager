// 命令行入口:get / set <value> / step <+n|-n> / pair / info
// 用法:
//   node src/cli.js get
//   node src/cli.js set 60
//   node src/cli.js step +1
//   node src/cli.js step -1
//   node src/cli.js pair
//   node src/cli.js info
//   node src/cli.js -serve         常驻模式(读 stdin 逐行命令, 复用电视连接, 零冷启动)
'use strict';

const fs = require('fs');
const path = require('path');
const readline = require('readline');
const { WebOSTVClient, loadClientKeyFor, saveClientKey } = require('./ssap-client');
const { readBacklight, setBacklight, stepBacklight, readPictureSettings } = require('./brightness');

const PROJECT_ROOT = path.resolve(__dirname, '..');

// 配置(环境变量优先, 其次 config.json, 最后内置默认值)
function loadConfigFallback() {
    try {
        const raw = fs.readFileSync(path.join(PROJECT_ROOT, 'config.json'), 'utf8');
        const obj = JSON.parse(raw);
        return {
            tvIp: typeof obj.tvIp === 'string' ? obj.tvIp : null,
            localAddr: typeof obj.localAddr === 'string' ? obj.localAddr : null,
        };
    } catch (e) {
        return { tvIp: null, localAddr: null };
    }
}

const fallbackCfg = loadConfigFallback();
const TV_IP = process.env.TV_IP || fallbackCfg.tvIp || '192.168.1.4';
const LOCAL_ADDR = process.env.LOCAL_ADDR || fallbackCfg.localAddr || '192.168.1.5';

function parseArgs(argv) {
    const cmd = argv[0];
    const arg = argv[1];
    if (cmd === 'get') return { cmd };
    if (cmd === 'pair') return { cmd };
    if (cmd === 'info') return { cmd };
    if (cmd === 'set') {
        // 严格校验: 必须是纯数字 0-100, 拒绝 "50abc" 这类脏输入
        const s = String(arg || '').trim();
        if (!/^\d+$/.test(s)) {
            throw new Error(`set 需要一个 0-100 的数值,收到: ${arg}`);
        }
        const v = parseInt(s, 10);
        if (v < 0 || v > 100) {
            throw new Error(`set 需要一个 0-100 的数值,收到: ${arg}`);
        }
        return { cmd, value: v };
    }
    if (cmd === 'step') {
        const m = /^([+-])(\d+)$/.exec(String(arg || '').trim());
        if (!m) {
            throw new Error(`step 需要形如 +1 / -1 的参数,收到: ${arg}`);
        }
        const delta = m[1] === '+' ? parseInt(m[2], 10) : -parseInt(m[2], 10);
        return { cmd, delta };
    }
    throw new Error(`未知命令: ${cmd || '(空)'}\n用法: get | set <0-100> | step <+n|-n> | pair | info`);
}

async function createClient() {
    // 已有 client-key 则复用,否则需要配对(首次会弹电视确认)
    let clientKey = loadClientKeyFor(PROJECT_ROOT, TV_IP);
    const client = new WebOSTVClient({
        host: TV_IP,
        localAddress: LOCAL_ADDR,
        clientKey: clientKey || undefined,
    });
    console.log(`[电视] ${TV_IP} (源地址 ${LOCAL_ADDR})${clientKey ? ' 已有 client-key' : ' 未配对'}`);
    const { clientKey: newKey, tvInfo } = await client.connect();
    if (newKey && newKey !== clientKey) {
        saveClientKey(PROJECT_ROOT, TV_IP, newKey);
        console.log(`[配对] 已保存 client-key: ${newKey}`);
    }
    if (tvInfo && tvInfo.modelName) {
        console.log(`[电视] ${tvInfo.modelName} / webOS ${tvInfo.deviceOSVersion || ''}`);
    }
    return client;
}

async function runCommand(client, args, quiet) {
    let result;
    const log = (msg) => { if (!quiet) console.log(msg); };
    switch (args.cmd) {
        case 'get': {
            const v = await readBacklight(client);
            log(`[亮度] backlight = ${v}`);
            result = v;
            break;
        }
        case 'set': {
            await setBacklight(client, args.value);
            log(`[亮度] set ${args.value} ✅`);
            result = args.value;
            break;
        }
        case 'step': {
            const { from, to } = await stepBacklight(client, args.delta);
            log(`[亮度] step ${args.delta > 0 ? '+' : ''}${args.delta} -> ${to} ✅`);
            result = to;
            break;
        }
        case 'pair': {
            log(`[配对] 成功`);
            result = 'ok';
            break;
        }
        case 'info': {
            const all = await readPictureSettings(client);
            for (const [k, v] of Object.entries(all)) {
                log(`[picture] ${k} = ${v}`);
            }
            result = all;
            break;
        }
        default:
            throw new Error(`未实现命令: ${args.cmd}`);
    }
    return result;
}

// 常驻模式: 复用电视连接, 从 stdin 逐行读命令, 响应为单行协议:
//   OK:set <value> / OK:step <value> / OK:get <value> / OK / ERR:<消息>
// 避免多行输出导致父进程解析歧义。
async function serve() {
    const client = await createClient();
    const rl = readline.createInterface({ input: process.stdin, terminal: false });
    console.log('[serve] ready');
    rl.on('line', async (line) => {
        const trimmed = line.trim();
        if (!trimmed || trimmed === 'quit' || trimmed === 'exit') {
            client.close();
            process.exit(0);
            return;
        }
        try {
            const args = parseArgs(trimmed.split(/\s+/));
            const result = await runCommand(client, args, true);
            // 单行协议输出: 结果可能是对象(info), 统一序列化避免 "[object Object]"
            const out = (result !== null && typeof result === 'object')
                ? JSON.stringify(result)
                : String(result);
            process.stdout.write(`OK:${args.cmd} ${out}\n`);
        } catch (e) {
            process.stdout.write('ERR: ' + e.message + '\n');
        }
    });
    rl.on('close', () => { client.close(); process.exit(0); });
    // 断线自动重连: 周期性检测连接, 断开则重连(SSAP 连接可能因电视休眠/网络波动断开)
    // 注意: connect() 内部会重新走完整握手; 重连期间在途命令会因连接关闭立即被拒绝
    let reconnecting = false;
    setInterval(async () => {
        if (reconnecting) return;
        try {
            if (!client.connected) {
                reconnecting = true;
                console.log('[serve] 连接断开, 重连...');
                await client.connect();
                console.log('[serve] 已重连');
            }
        } catch (e) {
            console.log('[serve] 重连失败: ' + e.message);
        } finally {
            reconnecting = false;
        }
    }, 5000);
}

async function main() {
    const args = process.argv.slice(2);
    if (args.length > 0 && args[0] === '-serve') {
        await serve();
        return;
    }
    const parsed = parseArgs(args);
    const client = await createClient();
    await runCommand(client, parsed, false);
    client.close();
    process.exit(0);
}

main().catch((e) => {
    console.error(`[错误] ${e.message}`);
    process.exit(1);
});
