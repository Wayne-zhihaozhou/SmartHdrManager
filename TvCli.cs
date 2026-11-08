using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SmartHdrManager
{
    /// <summary>
    /// LG webOS 电视亮度控制（纯 C#，无 Node.js 依赖）。
    /// 写入方式与 pc-control-tv 一致：直接调 setSystemSettings 会被 ACG 拒绝，
    /// 因此先 createAlert 创建提示框，把 luna 写入挂到它的按钮/关闭回调上，
    /// 再立即 closeAlert 关闭提示框 —— 关闭动作由电视系统以特权身份执行写入，
    /// 不需要用户在电视上做任何确认，也不会留下弹窗。
    /// </summary>
    public static class TvCli
    {
        private const string LunaSetSystemSettings = "luna://com.webos.settingsservice/setSystemSettings";
        private const string CreateAlertUri = "ssap://system.notifications/createAlert";
        private const string CloseAlertUri = "ssap://system.notifications/closeAlert";
        private const string GetSystemSettingsUri = "ssap://settings/getSystemSettings";
        private const string PictureCategory = "picture";
        private const string BrightnessKey = "backlight";

        private const int MinBrightness = 0;
        private const int MaxBrightness = 100;
        private const int SettingRequestTimeoutMs = 8000;
        private const int ReadRequestTimeoutMs = 6000;
        private const int AlertRequestTimeoutMs = 5000;
        private const int WriteSettleDelayMs = 250;
        private const int ReadBackAttempts = 3;

        private static readonly object Sync = new();
        private static readonly SemaphoreSlim Gate = new(1, 1);

        private static LgeTvClient? _client;
        private static string? _tvIp;
        private static string? _localAddr;

        /// <summary>初始化电视连接：有已保存的 client-key 时静默连接，否则首次需要配对。</summary>
        public static void Init(string tvIp, string localAddr)
        {
            if (string.IsNullOrWhiteSpace(tvIp)) return;

            lock (Sync)
            {
                _tvIp = tvIp;
                _localAddr = localAddr;
                _client?.Dispose();
                _client = new LgeTvClient(tvIp, localAddr) { ClientKey = TvClientKeyStore.Load(tvIp) };
            }

            _ = Task.Run(async () =>
            {
                var client = await EnsureConnectedAsync(CancellationToken.None).ConfigureAwait(false);
                if (client != null)
                {
                    HdrEngine.WriteFileLog($"[TV] 电视已连接: {tvIp} ({client.TvInfo?.ModelName ?? "未知型号"})");
                }
            });
        }

        /// <summary>读取当前亮度，失败返回 null。</summary>
        public static async Task<int?> GetBrightnessAsync()
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var client = await EnsureConnectedAsync(CancellationToken.None).ConfigureAwait(false);
                return client == null ? null : await ReadBrightnessAsync(client, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                HdrEngine.WriteFileLog($"[TV] 读取亮度失败: {ex.Message}");
                return null;
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>设置亮度（0-100），写入后读回验证。</summary>
        public static async Task<bool> SetBrightnessAsync(int value)
        {
            if (value < MinBrightness || value > MaxBrightness)
            {
                HdrEngine.WriteFileLog($"[TV] 亮度值超出范围: {value}（有效范围 {MinBrightness}-{MaxBrightness}）");
                return false;
            }

            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var client = await EnsureConnectedAsync(CancellationToken.None).ConfigureAwait(false);
                if (client == null) return false;

                var alertId = await CreateBrightnessAlertAsync(client, value, CancellationToken.None).ConfigureAwait(false);
                await CloseAlertAsync(client, alertId, CancellationToken.None).ConfigureAwait(false);
                await Task.Delay(WriteSettleDelayMs).ConfigureAwait(false);

                var applied = await VerifyBrightnessAsync(client, value, CancellationToken.None).ConfigureAwait(false);
                if (applied) HdrEngine.WriteFileLog($"[TV] 亮度已设置为 {value}");
                return applied;
            }
            catch (Exception ex)
            {
                HdrEngine.WriteFileLog($"[TV] 设置亮度 {value} 失败: {ex.Message}");
                return false;
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>以当前亮度为基准步进调整。</summary>
        public static async Task<(int from, int to)> StepBrightnessAsync(int delta)
        {
            var current = await GetBrightnessAsync().ConfigureAwait(false);
            if (current == null) return (-1, -1);

            var target = Math.Clamp(current.Value + delta, MinBrightness, MaxBrightness);
            var applied = await SetBrightnessAsync(target).ConfigureAwait(false);
            return (current.Value, applied ? target : -1);
        }

        /// <summary>测试连接并汇报当前亮度（会顺带保存配对凭证）。</summary>
        public static async Task<(bool success, string message)> TestConnectionAsync(string tvIp, string localAddr)
        {
            if (string.IsNullOrWhiteSpace(tvIp))
                return (false, "电视 IP 地址不能为空");

            var client = new LgeTvClient(tvIp, localAddr) { ClientKey = TvClientKeyStore.Load(tvIp) };
            try
            {
                await client.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                PersistClientKey(client);

                var brightness = await ReadBrightnessAsync(client, CancellationToken.None).ConfigureAwait(false);
                if (brightness == null)
                    return (false, "已连接，但读取亮度失败");
                return (true, $"已连接！当前亮度: {brightness.Value}");
            }
            catch (TimeoutException ex)
            {
                return (false, $"{ex.Message}——请检查电视 IP 是否正确、电视是否与电脑在同一网络");
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

        /// <summary>强制重新配对：忽略已保存的凭证，让电视重新弹出确认框。</summary>
        public static async Task<bool> PairAsync()
        {
            string? tvIp;
            string? localAddr;
            lock (Sync)
            {
                tvIp = _tvIp;
                localAddr = _localAddr;
            }
            if (string.IsNullOrWhiteSpace(tvIp)) return false;

            var client = new LgeTvClient(tvIp, localAddr);
            try
            {
                await client.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                if (string.IsNullOrEmpty(client.ClientKey))
                {
                    HdrEngine.WriteFileLog("[TV] 配对失败: 电视未返回 client-key");
                    return false;
                }

                TvClientKeyStore.Save(client.Host, client.ClientKey);
                HdrEngine.WriteFileLog("[TV] 配对成功，已保存 client-key");

                lock (Sync)
                {
                    _client?.Dispose();
                    _client = new LgeTvClient(tvIp, localAddr) { ClientKey = client.ClientKey };
                }
                return true;
            }
            catch (Exception ex)
            {
                HdrEngine.WriteFileLog($"[TV] 配对失败: {ex.Message}");
                return false;
            }
            finally
            {
                client.Dispose();
            }
        }

        /// <summary>获取电视型号 / webOS 版本。</summary>
        public static async Task<string?> GetInfoAsync()
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var client = await EnsureConnectedAsync(CancellationToken.None).ConfigureAwait(false);
                return client?.TvInfo?.ToString();
            }
            finally
            {
                Gate.Release();
            }
        }

        public static void Shutdown()
        {
            lock (Sync)
            {
                _client?.Dispose();
                _client = null;
                _tvIp = null;
                _localAddr = null;
            }
        }

        private static async Task<LgeTvClient?> EnsureConnectedAsync(CancellationToken ct)
        {
            LgeTvClient? client;
            lock (Sync)
            {
                client = _client;
            }
            if (client == null) return null;
            if (client.Connected) return client;

            try
            {
                await client.ConnectAsync(ct).ConfigureAwait(false);
                PersistClientKey(client);
                return client;
            }
            catch (Exception ex)
            {
                HdrEngine.WriteFileLog($"[TV] 连接电视失败: {ex.Message}");
                return null;
            }
        }

        private static void PersistClientKey(LgeTvClient client)
        {
            var key = client.ClientKey;
            if (string.IsNullOrEmpty(key)) return;
            if (TvClientKeyStore.Load(client.Host) == key) return;

            TvClientKeyStore.Save(client.Host, key);
            HdrEngine.WriteFileLog("[TV] 已保存配对凭证，之后启动不再弹出配对确认");
        }

        private static async Task<string> CreateBrightnessAlertAsync(LgeTvClient client, int value, CancellationToken ct)
        {
            var lunaParams = new Dictionary<string, object>
            {
                ["category"] = PictureCategory,
                ["settings"] = new Dictionary<string, object> { [BrightnessKey] = value.ToString() },
            };
            var callback = new Dictionary<string, object> { ["uri"] = LunaSetSystemSettings, ["params"] = lunaParams };

            var alert = new Dictionary<string, object>
            {
                ["message"] = " ",
                ["buttons"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["label"] = " ",
                        ["onClick"] = LunaSetSystemSettings,
                        ["params"] = lunaParams,
                    },
                },
                ["onclose"] = callback,
                ["onfail"] = callback,
            };

            var response = await client.RequestAsync(CreateAlertUri, alert, SettingRequestTimeoutMs, ct).ConfigureAwait(false);
            if (!IsSuccessfulResponse(response))
                throw new InvalidOperationException($"createAlert 失败: {DescribeFailure(response)}");

            var alertId = ReadAlertId(response);
            if (string.IsNullOrEmpty(alertId))
                throw new InvalidOperationException("createAlert 未返回 alertId，无法关闭提示框");

            return alertId;
        }

        /// <summary>
        /// closeAlert 必须是完整的 SSAP 请求（带 id/uri/payload）电视才会执行；
        /// 关闭提示框时系统触发 onclose -> setSystemSettings，弹窗随之消失。
        /// </summary>
        private static async Task CloseAlertAsync(LgeTvClient client, string alertId, CancellationToken ct)
        {
            var payload = new Dictionary<string, object> { ["alertId"] = alertId };
            try
            {
                var response = await client.RequestAsync(CloseAlertUri, payload, AlertRequestTimeoutMs, ct).ConfigureAwait(false);
                if (!IsSuccessfulResponse(response))
                    HdrEngine.WriteFileLog($"[TV] closeAlert 返回异常: {DescribeFailure(response)}");
            }
            catch (Exception ex)
            {
                HdrEngine.WriteFileLog($"[TV] closeAlert 未确认（{ex.Message}），亮度仍由关闭回调写入");
            }
        }

        private static async Task<int?> ReadBrightnessAsync(LgeTvClient client, CancellationToken ct)
        {
            var payload = new Dictionary<string, object>
            {
                ["category"] = PictureCategory,
                ["keys"] = new[] { BrightnessKey },
            };

            var response = await client.RequestAsync(GetSystemSettingsUri, payload, ReadRequestTimeoutMs, ct).ConfigureAwait(false);
            if (!IsSuccessfulResponse(response)) return null;
            if (!response.TryGetProperty("payload", out var body) || body.ValueKind != JsonValueKind.Object) return null;
            if (!body.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object) return null;
            if (!settings.TryGetProperty(BrightnessKey, out var value)) return null;

            return value.ValueKind switch
            {
                JsonValueKind.Number => value.GetInt32(),
                JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
                _ => null,
            };
        }

        private static async Task<bool> VerifyBrightnessAsync(LgeTvClient client, int expected, CancellationToken ct)
        {
            for (var attempt = 1; attempt <= ReadBackAttempts; attempt++)
            {
                var current = await ReadBrightnessAsync(client, ct).ConfigureAwait(false);
                if (current == expected) return true;

                HdrEngine.WriteFileLog($"[TV] 读回验证 {attempt}/{ReadBackAttempts}: 期望 {expected}，实际 {(current.HasValue ? current.Value.ToString() : "读取失败")}");
                if (attempt < ReadBackAttempts)
                    await Task.Delay(WriteSettleDelayMs, ct).ConfigureAwait(false);
            }
            return false;
        }

        private static string? ReadAlertId(JsonElement response)
        {
            if (!response.TryGetProperty("payload", out var body) || body.ValueKind != JsonValueKind.Object) return null;
            if (!body.TryGetProperty("alertId", out var alertId)) return null;

            return alertId.ValueKind switch
            {
                JsonValueKind.String => alertId.GetString(),
                JsonValueKind.Number => alertId.GetInt32().ToString(),
                _ => null,
            };
        }

        private static bool IsSuccessfulResponse(JsonElement response)
        {
            if (GetString(response, "type") == "error") return false;
            if (!response.TryGetProperty("payload", out var body) || body.ValueKind != JsonValueKind.Object) return false;
            if (!body.TryGetProperty("returnValue", out var returnValue)) return true;

            return returnValue.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.Number => returnValue.GetInt32() != 0,
                JsonValueKind.String => returnValue.GetString() is "true" or "1",
                _ => false,
            };
        }

        private static string DescribeFailure(JsonElement response)
        {
            var type = GetString(response, "type");
            var errorText = LgeTvClient.ExtractErrorText(response);
            return string.IsNullOrEmpty(type) ? response.ToString() : $"[{type}] {errorText}";
        }

        private static string? GetString(JsonElement element, string property)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) return null;
            return value.GetString();
        }
    }
}
