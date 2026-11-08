using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
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
        private static bool _connected;
        private static readonly SemaphoreSlim _semaphore = new(1, 1);

        public static bool IsRunning => _connected;

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

                // 尝试连接
                ConnectInternalAsync(CancellationToken.None).Wait(10000);
                _connected = IsRunning;
                if (_connected)
                {
                    HdrEngine.WriteFileLog($"[TV] 电视已连接: {tvIp} ({_client.TvInfo?.ModelName ?? ""})");
                }
                else
                {
                    HdrEngine.WriteFileLog($"[TV] 电视连接失败: {tvIp}");
                }
            }
        }

        private static async Task<bool> ConnectInternalAsync(CancellationToken ct)
        {
            if (_client == null || _tvIp == null) return false;
            try
            {
                await _client.ConnectAsync(ct);
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
                await _client.ConnectAsync(ct);
                _connected = true;
            }
            catch { /* 重连失败在日志中记录 */ }
        }

        /// <summary>确保连接，失败则尝试重连。</summary>
        private static async Task<LgeTvClient?> EnsureConnectedAsync(CancellationToken ct)
        {
            if (_client == null || _tvIp == null) return null;

            if (_client.Connected) return _client;

            lock (_lock)
            {
                if (_client.Connected) return _client;
            }

            var ok = await ConnectInternalAsync(ct);
            if (ok)
            {
                _connected = true;
                return _client;
            }
            return null;
        }

        /// <summary>获取当前亮度。</summary>
        public static async Task<int?> GetBrightnessAsync()
        {
            await _semaphore.WaitAsync();
            try
            {
                var client = await EnsureConnectedAsync(CancellationToken.None);
                if (client == null) return null;

                var payload = new Dictionary<string, object>
                {
                    ["category"] = PictureCategory,
                    ["keys"] = new[] { BrightnessKey },
                };

                var resp = await client.RequestAsync("ssap://settings/getSystemSettings", payload, 8000, CancellationToken.None);

                if (resp.TryGetProperty("type", out var t) && t.GetString() == "error")
                {
                    HdrEngine.WriteFileLog($"[TV] 读取亮度失败: {resp}");
                    return null;
                }

                if (!resp.TryGetProperty("payload", out var p) || !p.TryGetProperty("returnValue", out var rv) || rv.GetInt32() != 1)
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
            if (value < 0 || value > 100) return false;

            await _semaphore.WaitAsync();
            try
            {
                var client = await EnsureConnectedAsync(CancellationToken.None);
                if (client == null) return false;

                var v = value.ToString();
                var paramsObj = new Dictionary<string, object>
                {
                    ["category"] = PictureCategory,
                    ["settings"] = new Dictionary<string, object> { [BrightnessKey] = v },
                };

                var alertPayload = new Dictionary<string, object>
                {
                    ["message"] = " ",
                    ["buttons"] = new[] { new { label = "", onClick = LunaSetSystemSettings, parameters = paramsObj } },
                    ["onclose"] = new { uri = LunaSetSystemSettings, parameters = paramsObj },
                    ["onfail"] = new { uri = LunaSetSystemSettings, parameters = paramsObj },
                };

                var createResp = await client.RequestAsync("ssap://system.notifications/createAlert", alertPayload, 8000, CancellationToken.None);

                if (createResp.TryGetProperty("type", out var t) && t.GetString() == "error")
                {
                    HdrEngine.WriteFileLog($"[TV] createAlert 失败: {createResp}");
                    return false;
                }

                if (createResp.TryGetProperty("payload", out var p) && p.TryGetProperty("alertId", out var aid) && aid.ValueKind == JsonValueKind.String)
                {
                    var alertId = aid.GetString();
                    if (!string.IsNullOrEmpty(alertId))
                    {
                        await client.RequestAsync("ssap://system.notifications/closeAlert", new Dictionary<string, object> { ["alertId"] = alertId }, 3000, CancellationToken.None);
                    }
                }

                // 等待写入生效
                await Task.Delay(300, CancellationToken.None);

                // 读回验证
                for (int i = 0; i < 3; i++)
                {
                    var read = await GetBrightnessAsync();
                    if (read == value) return true;
                    await Task.Delay(250, CancellationToken.None);
                }

                HdrEngine.WriteFileLog($"[TV] 亮度写入后读回不符: 期望 {value}");
                return false;
            }
            catch (Exception ex)
            {
                HdrEngine.WriteFileLog($"[TV] 设置亮度异常: {ex.Message}");
                return false;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>步进调整亮度。</summary>
        public static async Task<(int from, int to)> StepBrightnessAsync(int delta)
        {
            var current = await GetBrightnessAsync();
            if (current == null) return (-1, -1);
            var target = Math.Max(0, Math.Min(100, current.Value + delta));
            var ok = await SetBrightnessAsync(target);
            return (current.Value, ok ? target : -1);
        }

        /// <summary>执行配对。</summary>
        public static async Task<bool> PairAsync()
        {
            await _semaphore.WaitAsync();
            try
            {
                if (_client == null || _tvIp == null) return false;
                var client = new LgeTvClient(_tvIp, _localAddr);
                await client.ConnectAsync();
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
            await _semaphore.WaitAsync();
            try
            {
                var client = await EnsureConnectedAsync(CancellationToken.None);
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
