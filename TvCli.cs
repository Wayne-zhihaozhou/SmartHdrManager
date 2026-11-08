using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SmartHdrManager
{
    /// <summary>
    /// LG webOS 电视亮度控制（纯 C# 实现，无 Node.js 依赖）。
    /// 使用 SSAP 协议通过 WebSocket 与电视通信。
    /// </summary>
    public static class TvCli
    {
        private const string LunaSetSystemSettings = "luna://com.webos.settingsservice/setSystemSettings";
        private const string PictureCategory = "picture";
        private const string BrightnessKey = "backlight";

        private static LgeTvClient? _client;
        private static readonly object _lock = new();
        private static string? _tvIp;
        private static string? _localAddr;
        private static volatile bool _connected;
        private static readonly SemaphoreSlim _semaphore = new(1, 1);

        public static bool IsRunning
        {
            get
            {
                lock (_lock) { return _connected; }
            }
        }

        /// <summary>初始化电视连接。</summary>
        public static void Init(string tvIp, string localAddr)
        {
            lock (_lock)
            {
                if (_connected) return;
                _tvIp = tvIp;
                _localAddr = localAddr;

                var clientKey = ClientKeyStore.Load(tvIp);
                _client = new LgeTvClient(tvIp, localAddr, clientKey);

                // 自动重连
                _client.StartAutoReconnect(async ct =>
                {
                    await ReconnectInternalAsync(ct);
                });

                // 后台异步连接，不阻塞程序启动
                Task.Run(async () =>
                {
                    try
                    {
                        var ok = await ConnectInternalAsync(CancellationToken.None);
                        lock (_lock)
                        {
                            _connected = ok;
                        }
                        if (ok)
                        {
                            HdrEngine.WriteFileLog($"[TV] 电视已连接: {tvIp} ({_client?.TvInfo?.ModelName ?? ""})");
                        }
                        else
                        {
                            HdrEngine.WriteFileLog($"[TV] 电视连接失败: {tvIp}（将在后台自动重连）");
                        }
                    }
                    catch (Exception ex)
                    {
                        HdrEngine.WriteFileLog($"[TV] 电视连接异常: {ex.Message}");
                        lock (_lock) { _connected = false; }
                    }
                });
            }
        }

        private static async Task<bool> ConnectInternalAsync(CancellationToken ct)
        {
            if (_client == null || _tvIp == null) return false;
            try
            {
                await _client.ConnectAsync(ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                HdrEngine.WriteFileLog($"[TV] 连接失败: {ex.Message}");
                return false;
            }
        }

        private static async Task ReconnectInternalAsync(CancellationToken ct)
        {
            if (_client == null || _tvIp == null) return;
            try
            {
                await _client.ConnectAsync(ct).ConfigureAwait(false);
                lock (_lock)
                {
                    _connected = true;
                }
            }
            catch { /* 重连失败在日志中记录 */ }
        }

        /// <summary>确保连接，失败则尝试重连。</summary>
        private static async Task<LgeTvClient?> EnsureConnectedAsync(CancellationToken ct)
        {
            if (_client == null || _tvIp == null)
            {
                HdrEngine.WriteFileLog($"[TV-CONN] 无法确保连接: _client={_client != null}, _tvIp={_tvIp ?? "null"}");
                return null;
            }

            if (_client.Connected)
            {
                HdrEngine.WriteFileLog("[TV-CONN] 电视已连接");
                return _client;
            }

            HdrEngine.WriteFileLog($"[TV-CONN] 电视未连接，尝试重连: _tvIp={_tvIp}");
            
            lock (_lock)
            {
                if (_client.Connected)
                {
                    HdrEngine.WriteFileLog("[TV-CONN] 在锁内检测到已连接");
                    return _client;
                }
            }

            var ok = await ConnectInternalAsync(ct).ConfigureAwait(false);
            if (ok)
            {
                lock (_lock) { _connected = true; }
                HdrEngine.WriteFileLog("[TV-CONN] 重连成功");
                return _client;
            }
            
            HdrEngine.WriteFileLog("[TV-CONN] 重连失败");
            return null;
        }

        /// <summary>测试与电视的连接，返回详细结果。</summary>
        public static async Task<(bool success, string message)> TestConnectionAsync(string tvIp, string localAddr)
        {
            if (string.IsNullOrWhiteSpace(tvIp))
                return (false, "电视 IP 地址不能为空");

            // 加载已保存的 client-key，避免重复配对
            var savedKey = ClientKeyStore.Load(tvIp);
            var client = new LgeTvClient(tvIp, localAddr, savedKey);
            try
            {
                await client.ConnectAsync(CancellationToken.None).ConfigureAwait(false);

                var sysInfoPayload = new Dictionary<string, object>();
                var sysResp = await client.RequestAsync("ssap://system/getSystemInfo", sysInfoPayload, 6000, CancellationToken.None).ConfigureAwait(false);

                if (sysResp.TryGetProperty("type", out var t) && t.GetString() == "error")
                {
                    var errMsg = sysResp.TryGetProperty("payload", out var p) && p.TryGetProperty("errorText", out var et)
                        ? et.GetString() ?? "未知错误"
                        : "未知错误";
                    return (false, $"电视响应错误: {errMsg}");
                }

                var brightnessPayload = new Dictionary<string, object>
                {
                    ["category"] = PictureCategory,
                    ["keys"] = new[] { BrightnessKey },
                };
                var brightnessResp = await client.RequestAsync("ssap://settings/getSystemSettings", brightnessPayload, 6000, CancellationToken.None).ConfigureAwait(false);

                if (brightnessResp.TryGetProperty("type", out var bt) && bt.GetString() == "error")
                {
                    var errMsg = brightnessResp.TryGetProperty("payload", out var bp) && bp.TryGetProperty("errorText", out var bet)
                        ? bet.GetString() ?? "未知错误"
                        : "未知错误";
                    return (false, $"读取亮度失败: {errMsg}");
                }

                if (!brightnessResp.TryGetProperty("payload", out var bp2) ||
                    !bp2.TryGetProperty("returnValue", out var rv) || !IsReturnValueTrue(rv))
                {
                    return (false, "读取亮度返回异常");
                }

                if (!bp2.TryGetProperty("settings", out var s) || !s.TryGetProperty(BrightnessKey, out var val))
                    return (false, "电视未返回亮度信息");

                int brightness;
                if (val.ValueKind == JsonValueKind.Number)
                    brightness = val.GetInt32();
                else if (val.ValueKind == JsonValueKind.String && int.TryParse(val.GetString(), out var n))
                    brightness = n;
                else
                    return (false, "亮度值格式异常");

                return (true, $"已连接！当前亮度: {brightness}");
            }
            catch (TimeoutException)
            {
                return (false, "连接超时——请检查电视 IP 是否正确，电视是否与同一网络");
            }
            catch (InvalidOperationException ex)
            {
                return (false, $"连接失败: {ex.Message}");
            }
            catch (Exception ex)
            {
                return (false, $"连接失败: {ex.Message}");
            }
            finally
            {
                client.Dispose();
            }
        }

        /// <summary>获取当前亮度。</summary>
        public static async Task<int?> GetBrightnessAsync()
        {
            await _semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var client = await EnsureConnectedAsync(CancellationToken.None).ConfigureAwait(false);
                if (client == null) return null;

                var payload = new Dictionary<string, object>
                {
                    ["category"] = PictureCategory,
                    ["keys"] = new[] { BrightnessKey },
                };

                var resp = await client.RequestAsync("ssap://settings/getSystemSettings", payload, 8000, CancellationToken.None).ConfigureAwait(false);

                if (resp.TryGetProperty("type", out var t) && t.GetString() == "error")
                {
                    HdrEngine.WriteFileLog($"[TV] 读取亮度失败: {resp}");
                    return null;
                }

                if (!resp.TryGetProperty("payload", out var p) || !p.TryGetProperty("returnValue", out var rv) || !IsReturnValueTrue(rv))
                {
                    HdrEngine.WriteFileLog($"[TV] 读取亮度返回异常: {resp}");
                    return null;
                }

                if (!p.TryGetProperty("settings", out var s) || !s.TryGetProperty(BrightnessKey, out var val))
                    return null;

                if (val.ValueKind == JsonValueKind.Number) return val.GetInt32();
                if (val.ValueKind == JsonValueKind.String && int.TryParse(val.GetString(), out var n)) return n;
                return null;
            }
            catch (Exception ex)
            {
                HdrEngine.WriteFileLog($"[TV] 读取亮度异常: {ex.Message}");
                return null;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>设置亮度 0-100。</summary>
        public static async Task<bool> SetBrightnessAsync(int value)
        {
            HdrEngine.WriteFileLog($"[TV-BRIGHTNESS] 请求设置亮度: {value}");
            
            if (value < 0 || value > 100)
            {
                HdrEngine.WriteFileLog($"[TV-BRIGHTNESS] 亮度值超出范围: {value} (有效范围 0-100)");
                return false;
            }

            await _semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                HdrEngine.WriteFileLog("[TV-BRIGHTNESS] 获取信号量成功");
                
                var client = await EnsureConnectedAsync(CancellationToken.None).ConfigureAwait(false);
                if (client == null)
                {
                    HdrEngine.WriteFileLog("[TV-BRIGHTNESS] 错误: 无法获取电视客户端连接");
                    return false;
                }
                
                HdrEngine.WriteFileLog($"[TV-BRIGHTNESS] 电视客户端已连接: {client.Connected}");

                var v = value.ToString();
                var paramsObj = new Dictionary<string, object>
                {
                    ["category"] = PictureCategory,
                    ["settings"] = new Dictionary<string, object> { [BrightnessKey] = v },
                };

                var btnObj = new Dictionary<string, object> { ["label"] = " ", ["onClick"] = LunaSetSystemSettings, ["params"] = paramsObj };
                var onCloseObj = new Dictionary<string, object> { ["uri"] = LunaSetSystemSettings, ["params"] = paramsObj };
                var onFailObj = new Dictionary<string, object> { ["uri"] = LunaSetSystemSettings, ["params"] = paramsObj };

                var alertPayload = new Dictionary<string, object>
                {
                    ["message"] = " ",
                    ["buttons"] = new[] { btnObj },
                    ["onclose"] = onCloseObj,
                    ["onfail"] = onFailObj,
                };

                HdrEngine.WriteFileLog($"[TV-BRIGHTNESS] 发送亮度设置请求，值={v}");
                var createAlertJson = System.Text.Json.JsonSerializer.Serialize(alertPayload);
                HdrEngine.WriteFileLog($"[TV-BRIGHTNESS] createAlert JSON: {createAlertJson}");
                
                var createResp = await client.RequestAsync("ssap://system.notifications/createAlert", alertPayload, 8000, CancellationToken.None).ConfigureAwait(false);

                // 匹配 pc-control-tv: 同时检查 type=error 和 returnValue !== true
                var createType = createResp.TryGetProperty("type", out var ct) ? ct.GetString() : null;
                if (createType == "error")
                {
                    var errMsg = createResp.TryGetProperty("payload", out var ep) && ep.TryGetProperty("errorText", out var et)
                        ? et.GetString() ?? "未知错误"
                        : (createResp.TryGetProperty("error", out var er) ? er.GetString() ?? "未知错误" : createResp.ToString());
                    HdrEngine.WriteFileLog($"[TV-BRIGHTNESS] createAlert 失败: {errMsg}");
                    return false;
                }
                if (createResp.TryGetProperty("payload", out var cp) && cp.TryGetProperty("returnValue", out var rv2) && !IsReturnValueTrue(rv2))
                {
                    var errMsg = cp.TryGetProperty("errorText", out var et2) ? et2.GetString() ?? "未知错误" : "returnValue = false";
                    HdrEngine.WriteFileLog($"[TV-BRIGHTNESS] createAlert returnValue=false: {errMsg}");
                    return false;
                }

                string? alertId = null;
                if (createResp.TryGetProperty("payload", out var p) && p.TryGetProperty("alertId", out var aid))
                {
                    if (aid.ValueKind == JsonValueKind.String)
                        alertId = aid.GetString();
                    else if (aid.ValueKind == JsonValueKind.Number)
                        alertId = aid.GetInt32().ToString();
                }

                // closeAlert 使用单向发送（不等待响应），匹配 pc-control-tv 行为
                // closeAlert 是清理操作，亮度已由 createAlert 的 onclose/onfail 回调设置
                if (!string.IsNullOrEmpty(alertId))
                {
                    HdrEngine.WriteFileLog($"[TV-BRIGHTNESS] 关闭提示框 alertId={alertId}");
                    var closeAlertJson = $"{{\"alertId\":\"{alertId}\"}}";
                    await client.SendCustomJsonAsync(closeAlertJson, CancellationToken.None).ConfigureAwait(false);
                    HdrEngine.WriteFileLog("[TV-BRIGHTNESS] closeAlert 已发送（单向，不等待响应）");
                }

                // 等待写入生效（匹配 pc-control-tv 的 250ms）
                HdrEngine.WriteFileLog("[TV-BRIGHTNESS] 等待 250ms 让亮度设置生效");
                await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);

                // 读回验证（内联读取逻辑，避免在已持有信号量时再次获取导致死锁）
                HdrEngine.WriteFileLog("[TV-BRIGHTNESS] 开始读回验证");
                for (int i = 0; i < 3; i++)
                {
                    var readResp = await client.RequestAsync("ssap://settings/getSystemSettings", new Dictionary<string, object> { ["category"] = PictureCategory, ["keys"] = new[] { BrightnessKey } }, 8000, CancellationToken.None).ConfigureAwait(false);
                    int? readVal = null;
                    if (readResp.TryGetProperty("payload", out var rp) && rp.TryGetProperty("returnValue", out var rv) && IsReturnValueTrue(rv))
                    {
                        if (rp.TryGetProperty("settings", out var s) && s.TryGetProperty(BrightnessKey, out var val))
                        {
                            if (val.ValueKind == JsonValueKind.Number) readVal = val.GetInt32();
                            else if (val.ValueKind == JsonValueKind.String && int.TryParse(val.GetString(), out var n)) readVal = n;
                        }
                    }
                    HdrEngine.WriteFileLog($"[TV-BRIGHTNESS] 读回验证第{i+1}次: 期望={value}, 实际={readVal ?? -1}");
                    if (readVal == value)
                    {
                        HdrEngine.WriteFileLog($"[TV-BRIGHTNESS] 亮度设置成功: {value}");
                        return true;
                    }
                    await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
                }

                HdrEngine.WriteFileLog($"[TV-BRIGHTNESS] 亮度写入后读回不符: 期望 {value}");
                return false;
            }
            catch (Exception ex)
            {
                HdrEngine.WriteFileLog($"[TV-BRIGHTNESS] 设置亮度异常: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
            finally
            {
                HdrEngine.WriteFileLog("[TV-BRIGHTNESS] 释放信号量");
                _semaphore.Release();
            }
        }

        /// <summary>步进调整亮度。</summary>
        public static async Task<(int from, int to)> StepBrightnessAsync(int delta)
        {
            HdrEngine.WriteFileLog($"[TV-BRIGHTNESS] 步进调整亮度: delta={delta}");
            
            var current = await GetBrightnessAsync().ConfigureAwait(false);
            if (current == null)
            {
                HdrEngine.WriteFileLog("[TV-BRIGHTNESS] 获取当前亮度失败，无法步进调整");
                return (-1, -1);
            }
            
            HdrEngine.WriteFileLog($"[TV-BRIGHTNESS] 当前亮度: {current.Value}");
            
            var target = Math.Max(0, Math.Min(100, current.Value + delta));
            HdrEngine.WriteFileLog($"[TV-BRIGHTNESS] 目标亮度: {target}");
            
            var ok = await SetBrightnessAsync(target).ConfigureAwait(false);
            HdrEngine.WriteFileLog($"[TV-BRIGHTNESS] 步进调整结果: {(ok ? "成功" : "失败")}");
            
            return (current.Value, ok ? target : -1);
        }

        /// <summary>执行配对（会在电视上弹出确认请求，用户需在电视上确认）。</summary>
        public static async Task<bool> PairAsync()
        {
            await _semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_tvIp == null) return false;
                // 重新配对：不使用已保存的 key，强制电视弹出确认请求
                var client = new LgeTvClient(_tvIp, _localAddr);
                await client.ConnectAsync().ConfigureAwait(false);
                // 配对信息已保存在 client-key 中
                ClientKeyStore.Save(_tvIp, client.ClientKey!);
                HdrEngine.WriteFileLog($"[TV] 配对成功，已保存 client-key");
                client.Dispose();
                return true;
            }
            catch (Exception ex)
            {
                HdrEngine.WriteFileLog($"[TV] 配对失败: {ex.Message}");
                return false;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>获取电视信息。</summary>
        public static async Task<string?> GetInfoAsync()
        {
            await _semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var client = await EnsureConnectedAsync(CancellationToken.None).ConfigureAwait(false);
                if (client == null) return null;
                return client.TvInfo?.ToString();
            }
            catch (Exception ex)
            {
                HdrEngine.WriteFileLog($"[TV] 获取信息异常: {ex.Message}");
                return null;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>判断 returnValue 是否为真（支持 JSON 布尔 true 或数字 1）。</summary>
        private static bool IsReturnValueTrue(JsonElement elem)
        {
            if (elem.ValueKind == JsonValueKind.True) return true;
            if (elem.ValueKind == JsonValueKind.Number && elem.GetInt32() == 1) return true;
            if (elem.ValueKind == JsonValueKind.String && int.TryParse(elem.GetString(), out var n) && n == 1) return true;
            return false;
        }

        /// <summary>停止连接。</summary>
        public static void Shutdown()
        {
            lock (_lock)
            {
                _connected = false;
                if (_client != null)
                {
                    _client.Dispose();
                    _client = null;
                }
            }
        }
    }
}
