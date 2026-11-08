// 亮度控制:读/写/步进,基于 SSAP + createAlert 技巧
'use strict';

const LUNA_SET_SYSTEM_SETTINGS = 'luna://com.webos.settingsservice/setSystemSettings';
const PICTURE_CATEGORY = 'picture';
const BRIGHTNESS_KEY = 'backlight'; // 本机 G6 实测:OLED 亮度键名(字符串值)

/**
 * 读取当前亮度(backlight)。
 * @param {WebOSTVClient} client
 * @returns {Promise<string|null>} 字符串值,如 "60"
 */
async function readBacklight(client) {
    const resp = await client.request('ssap://settings/getSystemSettings', {
        category: PICTURE_CATEGORY,
        keys: [BRIGHTNESS_KEY],
    });
    if (resp.type === 'error' || !resp.payload || resp.payload.returnValue !== true) {
        throw new Error(`读取亮度失败: ${resp.payload ? resp.payload.errorText : resp.error || '未知错误'}`);
    }
    const val = resp.payload.settings && resp.payload.settings[BRIGHTNESS_KEY];
    return val !== undefined && val !== null ? String(val) : null;
}

/**
 * 写入亮度(backlight)。createAlert 技巧:创建弹窗并把 luna 调用挂到关闭回调,
 * 关闭时由系统以特权身份执行,绕开 ACG。
 * 写后读回验证:createAlert 的 luna 调用无返回值,必须 getSystemSettings 读回确认
 * (最多重试 3 次,仍不符则抛错,避免「设置看似成功实则未生效」的静默失败)。
 * @param {WebOSTVClient} client
 * @param {string|number} value 0-100
 * @returns {Promise<void>}
 */
async function setBacklight(client, value) {
    const v = String(value);
    const params = { category: PICTURE_CATEGORY, settings: { [BRIGHTNESS_KEY]: v } };
    const alertPayload = {
        message: ' ',
        buttons: [{ label: '', onClick: LUNA_SET_SYSTEM_SETTINGS, params }],
        onclose: { uri: LUNA_SET_SYSTEM_SETTINGS, params },
        onfail: { uri: LUNA_SET_SYSTEM_SETTINGS, params },
    };
    const createResp = await client.request('ssap://system.notifications/createAlert', alertPayload);
    if (createResp.type === 'error' || !createResp.payload || createResp.payload.returnValue !== true) {
        throw new Error(`createAlert 失败: ${createResp.payload ? JSON.stringify(createResp.payload) : createResp.error}`);
    }
    const alertId = createResp.payload.alertId;
    if (alertId) {
        await client.request('ssap://system.notifications/closeAlert', { alertId });
    }
    // 等写入生效(closeAlert 触发 onclose 回调, 系统异步执行 luna 调用; 局域网内 250ms 足够)
    await sleep(250);

    // 写后读回验证
    let lastRead = null;
    for (let i = 0; i < 3; i++) {
        try {
            lastRead = await readBacklight(client);
        } catch (e) {
            lastRead = null;
        }
        if (lastRead !== null && lastRead === v) return;
        await sleep(250);
    }
    throw new Error(`写入后读回不符: 期望 ${v}, 读到 ${lastRead === null ? '(读取失败)' : lastRead}`);
}

function sleep(ms) {
    return new Promise((r) => setTimeout(r, ms));
}

/**
 * 步进调整:读当前值 + delta,写入目标值。
 * @param {WebOSTVClient} client
 * @param {number} delta 正数调亮,负数调暗
 * @returns {Promise<{from: string, to: string}>}
 */
async function stepBacklight(client, delta) {
    const cur = await readBacklight(client);
    if (cur === null) {
        throw new Error('无法读取当前亮度(可能该键不可用)');
    }
    const curNum = parseInt(cur, 10);
    if (Number.isNaN(curNum)) {
        throw new Error(`当前亮度值不是数字: ${cur}`);
    }
    const target = Math.max(0, Math.min(100, curNum + delta));
    await setBacklight(client, target);
    return { from: cur, to: String(target) };
}

/**
 * 读取亮度,并返回所有 picture 类别下可用的键(诊断用)。
 * @param {WebOSTVClient} client
 * @param {string[]} [keys] 要读取的键名列表
 */
async function readPictureSettings(client, keys = ['backlight', 'brightness', 'energySaving', 'pictureMode']) {
    const out = {};
    for (const k of keys) {
        try {
            const resp = await client.request('ssap://settings/getSystemSettings', {
                category: PICTURE_CATEGORY,
                keys: [k],
            });
            if (resp.type === 'response' && resp.payload && resp.payload.returnValue === true) {
                out[k] = resp.payload.settings ? resp.payload.settings[k] : null;
            } else {
                out[k] = `ERR: ${resp.payload ? resp.payload.errorText : resp.error}`;
            }
        } catch (e) {
            out[k] = `ERR: ${e.message}`;
        }
    }
    return out;
}

module.exports = {
    readBacklight,
    setBacklight,
    stepBacklight,
    readPictureSettings,
    BRIGHTNESS_KEY,
};
