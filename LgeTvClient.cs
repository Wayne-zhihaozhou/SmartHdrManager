using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SmartHdrManager
{
    /// <summary>
    /// LG webOS TV SSAP 客户端（纯 C# 实现，替代 Node.js 依赖）。
    /// 原理: WebSocket (wss:3001 / ws:3000) → hello → getSystemInfo → register → request/response
    /// 使用独立消息接收循环处理 incoming 消息。
    /// </summary>
    public sealed class LgeTvClient : IDisposable
    {
        private const int DefaultPortWss = 3001;
        private const int DefaultPortWs = 3000;
        private const int RequestTimeoutMs = 8000;
        private const int HelloTimeoutMs = 6000;
        private const int RegisterTimeoutMs = 30000;

        private static readonly string[] RegisterPermissions = {
            "APP_TO_APP", "CLOSE", "CONTROL_AUDIO", "CONTROL_DISPLAY",
            "CONTROL_INPUT_JOYSTICK", "CONTROL_INPUT_MEDIA_PLAYBACK",
            "CONTROL_INPUT_MEDIA_RECORDING", "CONTROL_INPUT_TEXT",
            "CONTROL_INPUT_TV", "CONTROL_MOUSE_AND_KEYBOARD", "CONTROL_POWER",
            "CONTROL_TV_SCREEN", "LAUNCH", "LAUNCH_WEBAPP", "READ_APP_STATUS",
            "READ_COUNTRY_INFO", "READ_CURRENT_CHANNEL", "READ_INPUT_DEVICE_LIST",
            "READ_INSTALLED_APPS", "READ_LGE_SDX", "READ_LGE_TV_INPUT_EVENTS",
            "READ_NETWORK_STATE", "READ_NOTIFICATIONS", "READ_POWER_STATE",
            "READ_RUNNING_APPS", "READ_SETTINGS", "READ_TV_CHANNEL_LIST",
            "READ_TV_CURRENT_TIME", "READ_UPDATE_INFO", "SEARCH", "TEST_OPEN",
            "TEST_PROTECTED", "TEST_SECURE", "UPDATE_FROM_REMOTE_APP",
            "WRITE_NOTIFICATION_ALERT", "WRITE_NOTIFICATION_TOAST", "WRITE_SETTINGS",
        };

        private readonly string _host;
        private readonly string? _localAddress;
        private readonly string? _initialClientKey;
        private readonly bool _secureFirst;

        private ClientWebSocket? _ws;
        private readonly object _lock = new();
        private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly Queue<JsonElement> _incomingMessages = new();
        private readonly SemaphoreSlim _messageAvailable = new(0, 1);
        private Task? _receiveLoop;
        private CancellationTokenSource? _receiveCts;
        private int _msgId;
        private bool _disposed;
        private bool _closed;

        public bool Connected
        {
            get
            {
                lock (_lock)
                {
                    return _ws != null && _ws.State == WebSocketState.Open;
                }
            }
        }

        public string? ClientKey { get; private set; }
        public TvDeviceInfo? TvInfo { get; private set; }

        public LgeTvClient(string host, string? localAddress = null, string? clientKey = null, bool secureFirst = true)
        {
            _host = host;
            _localAddress = localAddress;
            _initialClientKey = clientKey;
            _secureFirst = secureFirst;
            ClientKey = clientKey;
        }

        /// <summary>连接并完成握手（hello + getSystemInfo + register）。</summary>
        public async Task ConnectAsync(CancellationToken ct = default)
        {
            if (Connected) return;

            await CloseInternalAsync().ConfigureAwait(false);
            _closed = false;

            // 1. 建立 WebSocket 连接
            await _connectAsync(ct).ConfigureAwait(false);
            _startReceiveLoop();

            // 2. hello
            var helloMsg = await _waitForMsgAsync("hello", new Dictionary<string, object>(), HelloTimeoutMs, ct).ConfigureAwait(false);
            TvInfo = null;
            if (helloMsg.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
            {
                TvInfo = new TvDeviceInfo(
                    payload.TryGetProperty("modelName", out var m) ? m.GetString() : null,
                    payload.TryGetProperty("deviceOSVersion", out var v) ? v.GetString() : null);
            }

            // 3. getSystemInfo（新固件要求）
            try { await RequestAsync("ssap://system/getSystemInfo", new Dictionary<string, object>(), 6000, ct).ConfigureAwait(false); } catch { /* 某些固件允许跳过 */ }

            // 4. register
            var regPayload = new Dictionary<string, object>
            {
                ["forcePairing"] = false,
                // 已有 client-key 时用 NONE 静默配对；没有时才弹 PROMPT 请求用户确认
                ["pairingType"] = ClientKey != null ? "NONE" : "PROMPT",
                ["manifest"] = new Dictionary<string, object>
                {
                    ["appVersion"] = "1.1",
                    ["manifestVersion"] = 1,
                    ["permissions"] = RegisterPermissions,
                },
            };
            if (ClientKey != null)
                regPayload["client-key"] = ClientKey;

            var regMsg = await _waitForMsgAsync("register", regPayload, RegisterTimeoutMs, ct).ConfigureAwait(false);

            if (regMsg.GetProperty("type").GetString() == "error")
            {
                var errMsg = regMsg.TryGetProperty("payload", out var rp) && rp.TryGetProperty("errorText", out var et)
                    ? et.GetString() ?? regMsg.GetProperty("payload").ToString()
                    : regMsg.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
                throw new InvalidOperationException($"配对被拒绝: {errMsg}");
            }

            var newKey = regMsg.TryGetProperty("payload", out var rpp)
                ? (rpp.TryGetProperty("client-key", out var ck) ? ck.GetString()
                : (rpp.TryGetProperty("clientKey", out var ck2) ? ck2.GetString() : null))
                : null;
            ClientKey = newKey ?? ClientKey;
        }

        private async Task _connectAsync(CancellationToken ct)
        {
            var urls = _secureFirst
                ? new[] { $"wss://{_host}:{DefaultPortWss}", $"ws://{_host}:{DefaultPortWs}" }
                : new[] { $"ws://{_host}:{DefaultPortWs}", $"wss://{_host}:{DefaultPortWss}" };

            Exception? lastErr = null;
            foreach (var url in urls)
            {
                if (ct.IsCancellationRequested) break;
                var ws = new ClientWebSocket();
                try
                {
                    if (url.StartsWith("wss:"))
                        ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

                    await ws.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);
                    _ws = ws;
                    return;
                }
                catch (Exception ex)
                {
                    lastErr = ex;
                    try { ws.Dispose(); } catch { }
                    _ws = null;
                }
            }
            throw new InvalidOperationException($"无法连接电视 {_host}: {lastErr?.Message ?? "未知错误"}");
        }

        private void _startReceiveLoop()
        {
            _receiveCts = new CancellationTokenSource();
            _receiveLoop = Task.Run(() => _receiveLoopAsync(_receiveCts.Token));
        }

        private async Task _receiveLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && !_closed)
            {
                try
                {
                    if (_ws == null || _ws.State != WebSocketState.Open)
                    {
                        await Task.Delay(500, ct).ConfigureAwait(false);
                        continue;
                    }

                    var buffer = new byte[65536];
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", ct).ConfigureAwait(false);
                        _failAll(new InvalidOperationException("连接已关闭"));
                        _closed = true;
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        JsonElement parsed;
                        try { parsed = JsonDocument.Parse(json).RootElement; } catch { continue; }

                        lock (_lock)
                        {
                            var type = parsed.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                            if (type == "hello" || type == "registered" || type == "response" || type == "error" || type == "register")
                            {
                                _incomingMessages.Enqueue(parsed);
                                _messageAvailable.Release();
                            }
                            else
                            {
                                // 非关键消息直接处理（hello/register/response/error 通过 _messageAvailable 分发）
                                if (parsed.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                                {
                                    var id = idProp.GetString();
                                    if (_pending.TryGetValue(id, out var tcs))
                                    {
                                        _pending.Remove(id);
                                        tcs.TrySetResult(parsed);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (InvalidOperationException) { break; }
                catch (Exception ex)
                {
                    HdrEngine.WriteFileLog($"[TV] 接收异常: {ex.Message}");
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
            }
        }

        private async Task<JsonElement> _waitForMsgAsync(string expectedType, Dictionary<string, object> payload, int timeoutMs, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var timeoutTimer = new Timer(_ => tcs.TrySetException(new TimeoutException($"{expectedType} 握手超时")), null, timeoutMs, Timeout.Infinite);

            // 发送消息
            var msg = new Dictionary<string, object> { ["type"] = expectedType, ["payload"] = payload };
            await SendRawAsync(JsonSerializer.Serialize(msg), ct).ConfigureAwait(false);

            // 等待响应（轮询队列）
            while (!ct.IsCancellationRequested)
            {
                if (_messageAvailable.Wait(100, ct))
                {
                    lock (_lock)
                    {
                        while (_incomingMessages.Count > 0)
                        {
                            var incoming = _incomingMessages.Dequeue();
                            if (incoming.TryGetProperty("type", out var tp) && tp.GetString() == expectedType)
                            {
                                timeoutTimer.Dispose();
                                tcs.TrySetResult(incoming);
                                return incoming;
                            }
                        }
                    }
                }
            }

            timeoutTimer.Dispose();
            throw new TimeoutException($"{expectedType} 握手超时");
        }

        /// <summary>发送 SSAP 请求并等待响应。</summary>
        public async Task<JsonElement> RequestAsync(string uri, Dictionary<string, object> payload, int timeoutMs, CancellationToken ct)
        {
            if (_ws == null || _ws.State != WebSocketState.Open)
                throw new InvalidOperationException("未连接");

            var id = Interlocked.Increment(ref _msgId).ToString();
            var msg = new Dictionary<string, object>
            {
                ["id"] = id,
                ["type"] = "request",
                ["uri"] = uri,
                ["payload"] = payload,
            };

            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var timer = new Timer(_ =>
            {
                tcs.TrySetException(new TimeoutException($"请求超时: {uri}"));
            }, null, timeoutMs, Timeout.Infinite);

            lock (_lock)
            {
                _pending[id] = tcs;
            }

            await SendRawAsync(JsonSerializer.Serialize(msg), ct).ConfigureAwait(false);

            try
            {
                return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                lock (_lock) { _pending.Remove(id); }
                timer?.Dispose();
            }
        }

        private async Task SendRawAsync(string json, CancellationToken ct)
        {
            if (_ws == null || _ws.State != WebSocketState.Open)
                throw new InvalidOperationException("未连接");
            var bytes = Encoding.UTF8.GetBytes(json);
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }

        /// <summary>发送自定义 JSON 消息（用于非标准 SSAP 消息如 closeAlert）。</summary>
        public async Task SendCustomJsonAsync(string json, CancellationToken ct)
        {
            if (_ws == null || _ws.State != WebSocketState.Open)
                throw new InvalidOperationException("未连接");
            var bytes = Encoding.UTF8.GetBytes(json);
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }

        private void _failAll(Exception ex)
        {
            lock (_lock)
            {
                foreach (var kv in _pending)
                {
                    kv.Value.TrySetException(ex);
                }
                _pending.Clear();
            }
        }

        private async Task CloseInternalAsync()
        {
            _closed = true;
            try { _receiveCts?.Cancel(); } catch { }
            _receiveCts?.Dispose();
            _receiveCts = null;
            _receiveLoop = null;
            _messageAvailable.Release(); // unblock any wait

            lock (_lock)
            {
                if (_ws != null)
                {
                    try { _ws.Dispose(); } catch { }
                    _ws = null;
                }
                foreach (var kv in _pending)
                {
                    kv.Value.TrySetException(new InvalidOperationException("连接已关闭"));
                }
                _pending.Clear();
                _incomingMessages.Clear();
            }
        }

        /// <summary>启动自动重连（每 5 秒检测一次）。</summary>
        public void StartAutoReconnect(Func<CancellationToken, Task> onReconnect)
        {
            Task.Run(async () =>
            {
                while (!_disposed)
                {
                    await Task.Delay(5000);
                    if (!_disposed && !Connected)
                    {
                        try
                        {
                            HdrEngine.WriteFileLog("[TV] 连接断开，正在重连...");
                            await onReconnect(CancellationToken.None);
                            HdrEngine.WriteFileLog("[TV] 已重连");
                        }
                        catch (Exception ex)
                        {
                            HdrEngine.WriteFileLog($"[TV] 重连失败: {ex.Message}");
                        }
                    }
                }
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _closed = true;
            try { _receiveCts?.Cancel(); } catch { }
            try { _receiveCts?.Dispose(); } catch { }
            _messageAvailable.Release();
            lock (_lock)
            {
                _ws?.Dispose();
                _ws = null;
                foreach (var kv in _pending) kv.Value.TrySetException(new ObjectDisposedException(nameof(LgeTvClient)));
                _pending.Clear();
                _incomingMessages.Clear();
            }
        }
    }

    /// <summary>电视设备信息。</summary>
    public readonly struct TvDeviceInfo
    {
        public string? ModelName { get; }
        public string? WebOSVersion { get; }
        public TvDeviceInfo(string? modelName, string? webOSVersion)
        {
            ModelName = modelName;
            WebOSVersion = webOSVersion;
        }
        public override string ToString() => $"{ModelName ?? "Unknown"} / webOS {WebOSVersion ?? "Unknown"}";
    }

    /// <summary>client-key 持久化存储。</summary>
    public static class ClientKeyStore
    {
        private static readonly string KeyDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "key");

        public static string? Load(string host)
        {
            try
            {
                var file = Path.Combine(KeyDir, "client-key.json");
                if (!File.Exists(file)) return null;
                var store = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
                return store?.TryGetValue(host, out var key) == true ? key : null;
            }
            catch { return null; }
        }

        public static void Save(string host, string clientKey)
        {
            Directory.CreateDirectory(KeyDir);
            var file = Path.Combine(KeyDir, "client-key.json");
            Dictionary<string, string> store;
            try { store = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? new(); } catch { store = new(); }
            store[host] = clientKey;
            File.WriteAllText(file, JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
