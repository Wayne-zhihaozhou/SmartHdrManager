// SSAP 客户端核心:连接/配对/请求 webOS TV
// 原理:https://github.com/home-assistant-libs/aiowebostv 的无签名注册握手 + createAlert 技巧
'use strict';

const fs = require('fs');
const path = require('path');
const WebSocket = require('ws');

const DEFAULT_PORT_WSS = 3001;
const DEFAULT_PORT_WS = 3000;

// 配对 manifest(无签名,aiowebostv 同款;带 WRITE_SETTINGS / WRITE_NOTIFICATION_ALERT)
const REGISTER_PERMISSIONS = [
    'APP_TO_APP', 'CLOSE', 'CONTROL_AUDIO', 'CONTROL_DISPLAY',
    'CONTROL_INPUT_JOYSTICK', 'CONTROL_INPUT_MEDIA_PLAYBACK',
    'CONTROL_INPUT_MEDIA_RECORDING', 'CONTROL_INPUT_TEXT',
    'CONTROL_INPUT_TV', 'CONTROL_MOUSE_AND_KEYBOARD', 'CONTROL_POWER',
    'CONTROL_TV_SCREEN', 'LAUNCH', 'LAUNCH_WEBAPP', 'READ_APP_STATUS',
    'READ_COUNTRY_INFO', 'READ_CURRENT_CHANNEL', 'READ_INPUT_DEVICE_LIST',
    'READ_INSTALLED_APPS', 'READ_LGE_SDX', 'READ_LGE_TV_INPUT_EVENTS',
    'READ_NETWORK_STATE', 'READ_NOTIFICATIONS', 'READ_POWER_STATE',
    'READ_RUNNING_APPS', 'READ_SETTINGS', 'READ_TV_CHANNEL_LIST',
    'READ_TV_CURRENT_TIME', 'READ_UPDATE_INFO', 'SEARCH', 'TEST_OPEN',
    'TEST_PROTECTED', 'TEST_SECURE', 'UPDATE_FROM_REMOTE_APP',
    'WRITE_NOTIFICATION_ALERT', 'WRITE_NOTIFICATION_TOAST', 'WRITE_SETTINGS',
];

class WebOSTVClient {
    /**
     * @param {object} opts
     * @param {string} opts.host 电视 IP
     * @param {string} [opts.localAddress] 绑定源地址(绕过 VPN 劫持)
     * @param {string} [opts.clientKey] 已配对的 client-key(免确认重连)
     * @param {boolean} [opts.secureFirst] 先试 wss:3001 还是 ws:3000
     */
    constructor(opts) {
        this.host = opts.host;
        this.localAddress = opts.localAddress || null;
        this.clientKey = opts.clientKey || null;
        this.secureFirst = opts.secureFirst !== false;
        this.ws = null;
        this.pending = new Map(); // id -> { resolve, reject, timer }
        this.helloWaiters = []; // { resolve, reject, timer }
        this.regWaiters = [];
        this.msgId = 0;
    }

    get connected() {
        return this.ws !== null && this.ws.readyState === WebSocket.OPEN;
    }

    _nextId() {
        return String(++this.msgId);
    }

    _urls() {
        const a = this.secureFirst
            ? [`wss://${this.host}:${DEFAULT_PORT_WSS}`, `ws://${this.host}:${DEFAULT_PORT_WS}`]
            : [`ws://${this.host}:${DEFAULT_PORT_WS}`, `wss://${this.host}:${DEFAULT_PORT_WSS}`];
        return a;
    }

    async _connect() {
        let lastErr = null;
        for (const url of this._urls()) {
            let ws = null;
            try {
                const opts = { handshakeTimeout: 8000 };
                if (this.localAddress) opts.localAddress = this.localAddress;
                if (url.startsWith('wss:')) opts.rejectUnauthorized = false;
                ws = new WebSocket(url, opts);
                await new Promise((resolve, reject) => {
                    ws.once('open', resolve);
                    ws.once('error', reject);
                });
                this.ws = ws;
                this._wire();
                return url;
            } catch (e) {
                lastErr = e;
                // 打开失败的 ws 不会进入 this.ws, 需要显式关闭, 避免句柄泄漏
                if (ws) { try { ws.close(); } catch (_) {} }
            }
        }
        throw new Error(`无法连接电视 ${this.host}(${this._urls().join(', ')}): ${lastErr ? lastErr.message : '未知错误'}`);
    }

    // 连接关闭/失败时, 清空所有在途请求与握手等待者, 避免悬挂超时
    _failAll(err) {
        const pend = Array.from(this.pending.values());
        this.pending.clear();
        pend.forEach((c) => { clearTimeout(c.timer); c.reject(err); });
        const fail = (list) => {
            const l = list.slice();
            list.length = 0;
            l.forEach((e) => { clearTimeout(e.timer); e.reject(err); });
        };
        fail(this.helloWaiters);
        fail(this.regWaiters);
    }

    _wire() {
        this.ws.on('message', (data) => {
            let msg;
            try { msg = JSON.parse(data.toString()); } catch (e) { return; }
            if (msg.type === 'hello') {
                const list = this.helloWaiters;
                this.helloWaiters = [];
                list.forEach((e) => { clearTimeout(e.timer); e.resolve(msg); });
                return;
            }
            if (msg.type === 'registered') {
                const list = this.regWaiters;
                this.regWaiters = [];
                list.forEach((e) => { clearTimeout(e.timer); e.resolve(msg); });
                return;
            }
            if (msg.type === 'response' || msg.type === 'error') {
                const cont = this.pending.get(msg.id);
                if (cont) { this.pending.delete(msg.id); clearTimeout(cont.timer); cont.resolve(msg); }
            }
        });
        this.ws.on('close', () => {
            this.ws = null;
            this._failAll(new Error('连接已关闭'));
        });
    }

    request(uri, payload = {}, timeoutMs = 8000) {
        return new Promise((resolve, reject) => {
            if (!this.connected) { reject(new Error('未连接')); return; }
            const id = this._nextId();
            const timer = setTimeout(() => {
                this.pending.delete(id);
                reject(new Error(`请求超时: ${uri}`));
            }, timeoutMs);
            this.pending.set(id, { resolve, reject, timer });
            try {
                this.ws.send(JSON.stringify({ id, type: 'request', uri, payload }));
            } catch (e) {
                clearTimeout(timer);
                this.pending.delete(id);
                reject(e);
            }
        });
    }

    // 发送一个非 request 类型的握手消息(hello / register), 带超时并登记到指定等待队列
    _waitFor(kind, payload, waiters, timeoutMs, timeoutMsg) {
        return new Promise((resolve, reject) => {
            const entry = { resolve, reject, timer: null };
            entry.timer = setTimeout(() => {
                const i = waiters.indexOf(entry);
                if (i >= 0) waiters.splice(i, 1);
                reject(new Error(timeoutMsg));
            }, timeoutMs);
            waiters.push(entry);
            this.ws.send(JSON.stringify({ id: kind, type: kind, payload }));
        });
    }

    /**
     * 连接并完成握手(hello -> getSystemInfo -> register)。
     * 首次配对会弹电视屏幕确认;已有 client-key 则静默重连。
     * @returns {Promise<{clientKey: string, tvInfo: object}>}
     */
    async connect() {
        await this._connect();

        // 1. hello
        const helloMsg = await this._waitFor('hello', {}, this.helloWaiters, 6000, 'hello 超时');
        const tvInfo = (helloMsg && helloMsg.payload) || {};

        // 2. 注册前 getSystemInfo(新固件要求)
        try {
            await this.request('ssap://system/getSystemInfo', {}, 6000);
        } catch (e) {
            // 某些固件允许跳过
        }

        // 3. register
        const registerPayload = {
            forcePairing: false,
            pairingType: 'PROMPT',
            manifest: {
                appVersion: '1.1',
                manifestVersion: 1,
                permissions: REGISTER_PERMISSIONS,
            },
        };
        if (this.clientKey) registerPayload['client-key'] = this.clientKey;

        const regMsg = await this._waitFor('register', registerPayload, this.regWaiters, 30000, '配对超时(电视上是否弹窗?)');

        if (regMsg.type === 'error') {
            throw new Error(`配对被拒绝: ${regMsg.error || regMsg.payload ? JSON.stringify(regMsg.payload) : ''}`);
        }

        const newKey = (regMsg.payload && (regMsg.payload['client-key'] || regMsg.payload.clientKey)) || this.clientKey;
        this.clientKey = newKey;
        return { clientKey: newKey, tvInfo };
    }

    close() {
        if (this.ws) { try { this.ws.close(); } catch (e) {} this.ws = null; }
        this._failAll(new Error('连接已关闭'));
    }
}

// ---- client-key 持久化 ----
function keyFile(projectRoot) {
    return path.join(projectRoot, 'key', 'client-key.json');
}

// 按主机读取已保存的 client-key(单机多电视按 IP 区分)
function loadClientKeyFor(projectRoot, host) {
    try {
        const store = JSON.parse(fs.readFileSync(keyFile(projectRoot), 'utf8'));
        return store[host] || null;
    } catch (e) {
        return null;
    }
}

function saveClientKey(projectRoot, host, clientKey) {
    const file = keyFile(projectRoot);
    let store = {};
    try { store = JSON.parse(fs.readFileSync(file, 'utf8')); } catch (e) { store = {}; }
    store[host] = clientKey;
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, JSON.stringify(store, null, 2), 'utf8');
}

module.exports = {
    WebOSTVClient,
    saveClientKey,
    loadClientKeyFor,
};
