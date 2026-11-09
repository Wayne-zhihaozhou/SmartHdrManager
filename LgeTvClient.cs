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
    /// LG webOS 电视 SSAP 客户端（纯 C# 实现）。
    /// 握手流程：WebSocket(wss:3001/ws:3000) → hello → getSystemInfo → register。
    /// 所有入站消息由单一接收循环按 type + id 分发：握手等待者拿 hello/registered，
    /// 请求等待者拿 response/error，避免响应被错误路由导致请求必然超时。
    /// 注册结果只认电视真正完成注册的消息（registered 或带 client-key 的响应）：
    /// 部分固件会先回一条 returnValue 确认再回 registered，把确认当成注册完成会拿不到
    /// client-key，导致凭证无法持久化、电视每次启动都重新弹窗要确认。
    /// </summary>
    public sealed class LgeTvClient : IDisposable
    {
        private const int SecurePort = 3001;
        private const int PlainPort = 3000;
        private const int ConnectTimeoutMs = 8000;
        private const int RequestTimeoutMs = 8000;
        private const int HelloTimeoutMs = 6000;
        private const int RegisterTimeoutMs = 30000;
        private const int ShutdownTimeoutMs = 1000;
        private const int ReceiveBufferSize = 8192;
        private const int MaxMessageBytes = 4 * 1024 * 1024;

        private const string HelloType = "hello";
        private const string RegisteredType = "registered";
        private const string RegisterType = "register";
        private const string RequestType = "request";
        private const string ResponseType = "response";
        private const string ErrorType = "error";
        private const string ClientKeyField = "client-key";
        private const string ClientKeyAltField = "clientKey";

        private static readonly string[] RegisterPermissions =
        {
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
        private readonly bool _secureFirst;
        private readonly SemaphoreSlim _connectGate = new(1, 1);
        private readonly object _sync = new();
        private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pending = new();

        private ClientWebSocket? _socket;
        private JsonElement? _bufferedHello;
        private TaskCompletionSource<JsonElement>? _helloWaiter;
        private TaskCompletionSource<JsonElement>? _registeredWaiter;
        private CancellationTokenSource? _receiveCts;
        private int _nextId;
        private bool _disposed;

        /// <param name="host">电视 IP。</param>
        /// <param name="localAddress">绑定的本机源地址（可为空）。</param>
        /// <param name="secureFirst">是否先尝试 wss:3001。</param>
        public LgeTvClient(string host, string? localAddress = null, bool secureFirst = true)
        {
            _host = host;
            _localAddress = localAddress;
            _secureFirst = secureFirst;
        }

        public string Host => _host;

        /// <summary>已配对凭证；首次注册后由电视下发，需要持久化才能免配对弹窗。</summary>
        public string? ClientKey { get; set; }

        public TvDeviceInfo? TvInfo { get; private set; }

        public bool Connected
        {
            get
            {
                lock (_sync) return _socket != null && _socket.State == WebSocketState.Open;
            }
        }

        /// <summary>连接并完成 hello + getSystemInfo + register 握手。</summary>
        public async Task ConnectAsync(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            await _connectGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (Connected) return;
                await CloseSocketAsync().ConfigureAwait(false);

                var socket = await OpenSocketAsync(ct).ConfigureAwait(false);
                StartReceiveLoop(socket);

                var hello = await SendHandshakeAsync(HelloType, new Dictionary<string, object>(), HelloTimeoutMs, ct).ConfigureAwait(false);
                TvInfo = ReadTvInfo(hello, null);

                try
                {
                    var systemInfo = await RequestAsync("ssap://system/getSystemInfo", new Dictionary<string, object>(), HelloTimeoutMs, ct).ConfigureAwait(false);
                    TvInfo = ReadTvInfo(hello, systemInfo);
                }
                catch (Exception ex)
                {
                    HdrEngine.WriteFileLog($"[TV] getSystemInfo 未返回（部分固件允许跳过）: {ex.Message}");
                }

                var registered = await SendHandshakeAsync(RegisterType, BuildRegisterPayload(), RegisterTimeoutMs, ct).ConfigureAwait(false);
                ClientKey = ReadClientKey(registered) ?? ClientKey;

                if (string.IsNullOrEmpty(ClientKey))
                {
                    HdrEngine.WriteFileLog("[TV] 电视未下发 client-key：本次连接仍需在电视上确认，下次启动会再次询问");
                }
            }
            finally
            {
                _connectGate.Release();
            }
        }

        /// <summary>发送 SSAP 请求并等待对应 id 的响应。</summary>
        public async Task<JsonElement> RequestAsync(string uri, Dictionary<string, object>? payload = null, int timeoutMs = RequestTimeoutMs, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var id = Interlocked.Increment(ref _nextId).ToString();
            var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                _pending[id] = waiter;
            }

            try
            {
                var message = new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["type"] = RequestType,
                    ["uri"] = uri,
                    ["payload"] = payload ?? new Dictionary<string, object>(),
                };
                await SendRawAsync(JsonSerializer.Serialize(message), ct).ConfigureAwait(false);
                return await waiter.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"请求超时: {uri}");
            }
            finally
            {
                lock (_sync)
                {
                    _pending.Remove(id);
                }
            }
        }

        private Dictionary<string, object> BuildRegisterPayload()
        {
            var payload = new Dictionary<string, object>
            {
                ["forcePairing"] = false,
                ["pairingType"] = "PROMPT",
                ["manifest"] = new Dictionary<string, object>
                {
                    ["appVersion"] = "1.1",
                    ["manifestVersion"] = 1,
                    ["permissions"] = RegisterPermissions,
                },
            };
            if (!string.IsNullOrEmpty(ClientKey))
                payload[ClientKeyField] = ClientKey!;
            return payload;
        }

        private async Task<ClientWebSocket> OpenSocketAsync(CancellationToken ct)
        {
            var secureUrl = $"wss://{_host}:{SecurePort}";
            var plainUrl = $"ws://{_host}:{PlainPort}";
            var urls = _secureFirst ? new[] { secureUrl, plainUrl } : new[] { plainUrl, secureUrl };

            Exception? lastError = null;
            foreach (var url in urls)
            {
                ct.ThrowIfCancellationRequested();
                var socket = new ClientWebSocket();
                if (url.StartsWith("wss:", StringComparison.Ordinal))
                    socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(ConnectTimeoutMs);
                    await socket.ConnectAsync(new Uri(url), timeout.Token).ConfigureAwait(false);
                    lock (_sync)
                    {
                        _socket = socket;
                    }
                    return socket;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    socket.Dispose();
                    ct.ThrowIfCancellationRequested();
                }
            }

            throw new InvalidOperationException($"无法连接电视 {_host}: {lastError?.Message ?? "未知错误"}");
        }

        private void StartReceiveLoop(ClientWebSocket socket)
        {
            var cts = new CancellationTokenSource();
            lock (_sync)
            {
                _receiveCts = cts;
            }
            _ = Task.Run(() => ReceiveLoopAsync(socket, cts.Token), CancellationToken.None);
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
        {
            var buffer = new byte[ReceiveBufferSize];
            using var message = new MemoryStream();

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    message.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            HandleSocketClosed(socket, new InvalidOperationException("电视连接已关闭"));
                            return;
                        }
                        message.Write(buffer, 0, result.Count);
                        if (message.Length > MaxMessageBytes)
                        {
                            HandleSocketClosed(socket, new InvalidOperationException("SSAP 消息超出长度上限"));
                            return;
                        }
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType != WebSocketMessageType.Text) continue;
                    DispatchMessage(ParseMessage(message));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (WebSocketException ex)
                {
                    HandleSocketClosed(socket, ex);
                    return;
                }
                catch (Exception ex)
                {
                    HdrEngine.WriteFileLog($"[TV] 接收消息失败: {ex.Message}");
                }
            }
        }

        private static JsonElement ParseMessage(MemoryStream message)
        {
            using var document = JsonDocument.Parse(message.ToArray());
            return document.RootElement.Clone();
        }

        private void DispatchMessage(JsonElement message)
        {
            var type = GetString(message, "type");
            var id = GetString(message, "id");

            switch (type)
            {
                case HelloType:
                    BufferOrCompleteHello(message);
                    return;
                case RegisteredType:
                case ResponseType when id == RegisterType:
                case ErrorType when id == RegisterType:
                    DispatchRegister(message, type);
                    return;
                case ResponseType when id == HelloType:
                    BufferOrCompleteHello(message);
                    return;
                case ResponseType:
                case ErrorType:
                    ResolveRequest(id, message);
                    return;
            }
        }

        /// <summary>
        /// 注册结果分派。只有电视明确完成注册才唤醒等待者：
        /// registered、或带 client-key 的响应视为成功；error 视为失败；
        /// 其余 id=register 的响应只是确认消息（部分固件随后才会下发 client-key），
        /// 若此时就当作注册完成，凭证永远拿不到，电视每次启动都会重新弹窗要确认。
        /// </summary>
        private void DispatchRegister(JsonElement message, string? type)
        {
            if (type == ErrorType)
            {
                FailRegister(message);
                return;
            }

            if (type == RegisteredType || ReadClientKey(message) != null)
            {
                CompleteRegistered(message);
                return;
            }

            if (!ReadReturnValue(message))
            {
                FailRegister(message);
                return;
            }

            if (!string.IsNullOrEmpty(ClientKey))
            {
                CompleteRegistered(message);
                return;
            }

            HdrEngine.WriteFileLog("[TV] 已收到注册确认，继续等待电视下发 client-key…");
        }

        /// <summary>部分固件在连接建立后主动推送 hello，早于客户端发送 hello 请求，先缓存再交付。</summary>
        private void BufferOrCompleteHello(JsonElement message)
        {
            TaskCompletionSource<JsonElement>? waiter;
            lock (_sync)
            {
                waiter = _helloWaiter;
                if (waiter == null)
                {
                    _bufferedHello = message;
                    return;
                }
                _helloWaiter = null;
            }
            waiter.TrySetResult(message);
        }

        private void ResolveRequest(string? id, JsonElement message)
        {
            if (string.IsNullOrEmpty(id)) return;

            TaskCompletionSource<JsonElement>? waiter = null;
            lock (_sync)
            {
                if (_pending.TryGetValue(id!, out var found))
                {
                    _pending.Remove(id!);
                    waiter = found;
                }
            }
            waiter?.TrySetResult(message);
        }

        private void CompleteRegistered(JsonElement message)
        {
            TaskCompletionSource<JsonElement>? waiter;
            lock (_sync)
            {
                waiter = _registeredWaiter;
                _registeredWaiter = null;
            }
            waiter?.TrySetResult(message);
        }

        private void FailRegister(JsonElement message)
        {
            TaskCompletionSource<JsonElement>? waiter;
            lock (_sync)
            {
                waiter = _registeredWaiter;
                _registeredWaiter = null;
            }
            waiter?.TrySetException(new InvalidOperationException($"配对被拒绝: {ExtractErrorText(message)}"));
        }

        private async Task<JsonElement> SendHandshakeAsync(string type, Dictionary<string, object> payload, int timeoutMs, CancellationToken ct)
        {
            if (type == HelloType && TryTakeBufferedHello(out var buffered))
                return buffered;

            var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                if (type == HelloType) _helloWaiter = waiter;
                else _registeredWaiter = waiter;
            }

            try
            {
                var message = new Dictionary<string, object>
                {
                    ["id"] = type,
                    ["type"] = type,
                    ["payload"] = payload,
                };
                await SendRawAsync(JsonSerializer.Serialize(message), ct).ConfigureAwait(false);
                return await waiter.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(type == RegisterType
                    ? "配对超时（电视是否弹出确认提示？）"
                    : "hello 握手超时");
            }
            finally
            {
                lock (_sync)
                {
                    if (type == HelloType)
                    {
                        if (_helloWaiter == waiter) _helloWaiter = null;
                    }
                    else if (_registeredWaiter == waiter)
                    {
                        _registeredWaiter = null;
                    }
                }
            }
        }

        private bool TryTakeBufferedHello(out JsonElement hello)
        {
            lock (_sync)
            {
                if (_bufferedHello is JsonElement buffered)
                {
                    _bufferedHello = null;
                    hello = buffered;
                    return true;
                }
            }
            hello = default;
            return false;
        }

        private Task SendRawAsync(string json, CancellationToken ct)
        {
            var socket = RequireOpenSocket();
            var bytes = Encoding.UTF8.GetBytes(json);
            return socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }

        private ClientWebSocket RequireOpenSocket()
        {
            lock (_sync)
            {
                if (_socket == null || _socket.State != WebSocketState.Open)
                    throw new InvalidOperationException("未连接电视");
                return _socket;
            }
        }

        private void HandleSocketClosed(ClientWebSocket socket, Exception error)
        {
            lock (_sync)
            {
                if (!ReferenceEquals(_socket, socket)) return;
                _socket = null;
            }
            FailWaiters(error);
        }

        private void FailAll(Exception error)
        {
            lock (_sync)
            {
                _socket = null;
            }
            FailWaiters(error);
        }

        private void FailWaiters(Exception error)
        {
            List<TaskCompletionSource<JsonElement>> pending;
            TaskCompletionSource<JsonElement>? hello;
            TaskCompletionSource<JsonElement>? registered;
            lock (_sync)
            {
                pending = new List<TaskCompletionSource<JsonElement>>(_pending.Values);
                _pending.Clear();
                hello = _helloWaiter;
                _helloWaiter = null;
                registered = _registeredWaiter;
                _registeredWaiter = null;
                _bufferedHello = null;
            }

            foreach (var waiter in pending) waiter.TrySetException(error);
            hello?.TrySetException(error);
            registered?.TrySetException(error);
        }

        private async Task CloseSocketAsync()
        {
            ClientWebSocket? socket;
            CancellationTokenSource? cts;
            lock (_sync)
            {
                socket = _socket;
                _socket = null;
                cts = _receiveCts;
                _receiveCts = null;
            }

            try { cts?.Cancel(); } catch { }
            cts?.Dispose();

            if (socket != null)
            {
                try
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        using var timeout = new CancellationTokenSource(ShutdownTimeoutMs);
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token).ConfigureAwait(false);
                    }
                }
                catch { }
                socket.Dispose();
            }

            FailAll(new InvalidOperationException("电视连接已重置"));
        }

        /// <summary>
        /// 型号只在 getSystemInfo 的响应里（hello 只带 deviceOSVersion），因此两者都读：
        /// 版本取 hello，型号取 getSystemInfo，都缺失时返回 null。
        /// </summary>
        private static TvDeviceInfo? ReadTvInfo(JsonElement hello, JsonElement? systemInfo)
        {
            var helloPayload = ReadPayload(hello);
            var infoPayload = systemInfo is JsonElement info ? ReadPayload(info) : null;
            var modelName = GetString(infoPayload, "modelName") ?? GetString(helloPayload, "modelName");
            var version = GetString(helloPayload, "deviceOSVersion") ?? GetString(infoPayload, "deviceOSVersion");
            return modelName == null && version == null ? null : new TvDeviceInfo(modelName, version);
        }

        private static JsonElement? ReadPayload(JsonElement message)
        {
            if (message.ValueKind != JsonValueKind.Object) return null;
            if (!message.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return null;
            return payload;
        }

        private static bool ReadReturnValue(JsonElement message)
        {
            var payload = ReadPayload(message);
            if (payload is not JsonElement body || !body.TryGetProperty("returnValue", out var value)) return true;

            return value.ValueKind switch
            {
                JsonValueKind.False => false,
                JsonValueKind.Number => value.GetInt32() != 0,
                JsonValueKind.String => value.GetString() is "true" or "1",
                _ => true,
            };
        }

        private static string? ReadClientKey(JsonElement registered)
        {
            var payload = ReadPayload(registered);
            if (payload is not JsonElement body) return null;
            return GetString(body, ClientKeyField) ?? GetString(body, ClientKeyAltField);
        }

        private static string? GetString(JsonElement? element, string property)
        {
            return element is JsonElement value ? GetString(value, property) : null;
        }

        private static string? GetString(JsonElement element, string property)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) return null;
            var text = value.GetString();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        internal static string ExtractErrorText(JsonElement message)
        {
            if (message.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
            {
                if (GetString(payload, "errorText") is string errorText) return errorText;
                if (GetString(payload, "message") is string messageText) return messageText;
            }
            return GetString(message, "error") ?? "电视返回错误";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            ClientWebSocket? socket;
            CancellationTokenSource? cts;
            lock (_sync)
            {
                socket = _socket;
                _socket = null;
                cts = _receiveCts;
                _receiveCts = null;
            }

            try { cts?.Cancel(); } catch { }
            cts?.Dispose();
            try { socket?.Dispose(); } catch { }

            FailAll(new ObjectDisposedException(nameof(LgeTvClient)));
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
}
