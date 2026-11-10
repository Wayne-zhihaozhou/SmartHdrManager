using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SmartHdrManager
{
    /// <summary>
    /// LG webOS TV brightness control (pure C#, no Node.js).
    /// A direct setSystemSettings call is refused by the ACG, so an alert is created and the luna write is attached to
    /// its button / close callback; closing the alert makes the TV apply the write itself with its own privileges.
    /// Nothing has to be confirmed on the TV and no dialog is left behind.
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

        /// <summary>
        /// Remembers where the TV is. Nothing is connected here: the connection is made the first time brightness is
        /// actually used, so the app does not touch the network for users who never adjust the TV.
        /// </summary>
        public static void Configure(string tvIp)
        {
            lock (Sync)
            {
                _client?.Dispose();
                _client = null;
                _tvIp = tvIp;
            }
        }

        /// <summary>Sets the brightness (0-100) and reads it back to verify.</summary>
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

                bool applied = await VerifyBrightnessAsync(client, value, CancellationToken.None).ConfigureAwait(false);
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

        /// <summary>Tests the connection and reports the current brightness (also saves the pairing key).</summary>
        public static async Task<(bool success, string message)> TestConnectionAsync()
        {
            string tvIp;
            lock (Sync)
            {
                tvIp = _tvIp ?? BrightnessConfig.DefaultTvIp;
            }
            return await TestConnectionInternalAsync(tvIp);
        }

        private static async Task<(bool success, string message)> TestConnectionInternalAsync(string tvIp)
        {
            if (string.IsNullOrWhiteSpace(tvIp))
                return (false, "电视 IP 地址不能为空");

            var client = new LgeTvClient(tvIp) { ClientKey = TvClientKeyStore.Load(tvIp) };
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

        /// <summary>Forces a new pairing: saved credentials are ignored, so the TV asks again.</summary>
        public static async Task<bool> PairAsync()
        {
            string? tvIp;
            lock (Sync)
            {
                tvIp = _tvIp;
            }
            if (string.IsNullOrWhiteSpace(tvIp)) return false;

            var client = new LgeTvClient(tvIp);
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

                _client?.Dispose();
                _client = null;

                lock (Sync)
                {
                    _client = new LgeTvClient(tvIp) { ClientKey = client.ClientKey };
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

        public static void Shutdown()
        {
            lock (Sync)
            {
                _client?.Dispose();
                _client = null;
                _tvIp = null;
            }
        }

        private static async Task<LgeTvClient?> EnsureConnectedAsync(CancellationToken ct)
        {
            LgeTvClient? client;
            string? tvIp;
            lock (Sync)
            {
                tvIp = _tvIp ?? BrightnessConfig.DefaultTvIp;
            }

            lock (Sync)
            {
                if (_client == null && !string.IsNullOrWhiteSpace(tvIp))
                {
                    string? savedKey = TvClientKeyStore.Load(tvIp);
                    _client = new LgeTvClient(tvIp) { ClientKey = savedKey };
                    HdrEngine.WriteFileLog(savedKey == null
                        ? $"[TV] {tvIp} 尚无配对凭证，本次连接需要在电视上确认一次"
                        : $"[TV] {tvIp} 复用已保存的配对凭证");
                }
                client = _client;
            }

            if (client == null) return null;
            if (client.Connected) return client;

            try
            {
                await client.ConnectAsync(ct).ConfigureAwait(false);
                PersistClientKey(client);
                HdrEngine.WriteFileLog($"[TV] 电视已连接: {client.Host} ({client.TvInfo?.ModelName ?? "未知型号"})");
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
            if (string.Equals(TvClientKeyStore.Load(client.Host), key, StringComparison.Ordinal)) return;

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
        /// closeAlert has to be a complete SSAP request (id/uri/payload). Closing the alert makes the TV run the
        /// onclose callback, which applies the brightness.
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
            for (int attempt = 1; attempt <= ReadBackAttempts; attempt++)
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
