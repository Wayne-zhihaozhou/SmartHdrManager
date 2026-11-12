using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HdrBrightControl
{
    /// <summary>
    /// LG webOS TV SSAP client (pure C#). Handshake: WebSocket (wss:3001 / ws:3000) → hello → getSystemInfo → register.
    /// All inbound messages are dispatched by one receive loop per type + id, so a handshake waiter and a request waiter
    /// can never receive each other's response.
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

        private const string HelloRequestId = "hello";
        private const string RegisteredType = "registered";
        private const string RegisterRequestId = "register";
        private const string RequestType = "request";
        private const string ResponseType = "response";
        private const string ErrorType = "error";
        private const string CloseReason = "bye";
        private const string ConnectionResetMessage = "电视连接已重置";
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
        private readonly bool _secureFirst;
        private readonly SemaphoreSlim _connectGate = new(1, 1);
        private readonly object _sync = new();
        private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pending = new();

        private ClientWebSocket? _socket;
        private HttpMessageInvoker? _invoker;
        private JsonElement? _bufferedHello;
        private TaskCompletionSource<JsonElement>? _helloWaiter;
        private TaskCompletionSource<JsonElement>? _registeredWaiter;
        private CancellationTokenSource? _receiveCts;
        private int _nextId;
        private bool _disposed;

        public LgeTvClient(string host, bool secureFirst = true)
        {
            _host = host;
            _secureFirst = secureFirst;
        }

        public string Host => _host;

        /// <summary>Pairing key the TV handed out; it has to be persisted or the TV asks again on every start.</summary>
        public string? ClientKey { get; set; }

        public TvDeviceInfo? TvInfo { get; private set; }

        public bool Connected
        {
            get
            {
                lock (_sync) return _socket != null && _socket.State == WebSocketState.Open;
            }
        }

        /// <summary>Connects and runs the hello + getSystemInfo + register handshake.</summary>
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

                var hello = await SendHandshakeAsync(HelloRequestId, new Dictionary<string, object>(), HelloTimeoutMs, ct)
                    .ConfigureAwait(false);
                TvInfo = ReadTvInfo(hello, null);

                try
                {
                    var systemInfo = await RequestAsync("ssap://system/getSystemInfo", new Dictionary<string, object>(),
                        HelloTimeoutMs, ct).ConfigureAwait(false);
                    TvInfo = ReadTvInfo(hello, systemInfo);
                }
                catch (Exception ex)
                {
                    HdrEngine.WriteFileLog($"[TV] getSystemInfo 未返回（部分固件允许跳过）: {ex.Message}");
                }

                var registered = await SendHandshakeAsync(RegisterRequestId, BuildRegisterPayload(), RegisterTimeoutMs, ct)
                    .ConfigureAwait(false);
                ClientKey = ReadClientKey(registered) ?? ClientKey;

                if (string.IsNullOrEmpty(ClientKey))
                    HdrEngine.WriteFileLog("[TV] 电视未下发 client-key：本次连接仍需在电视上确认，下次启动会再次询问");
            }
            finally
            {
                _connectGate.Release();
            }
        }

        /// <summary>Sends an SSAP request and waits for the response with the same id.</summary>
        public async Task<JsonElement> RequestAsync(string uri, Dictionary<string, object>? payload = null,
                                                   int timeoutMs = RequestTimeoutMs, CancellationToken ct = default)
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

            var source = TvNetworkRoute.FindSourceAddress(_host);
            HdrEngine.WriteFileLog(source == null
                ? $"[TV] 未找到与 {_host} 同网段的网卡，使用默认路由连接"
                : $"[TV] 绑定本机网卡 {source} 直连 {_host}");

            Exception? lastError = null;
            foreach (string url in urls)
            {
                ct.ThrowIfCancellationRequested();

                var invoker = CreateInvoker(source);
                var socket = new ClientWebSocket();
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(ConnectTimeoutMs);
                    await socket.ConnectAsync(new Uri(url), invoker, timeout.Token).ConfigureAwait(false);

                    lock (_sync)
                    {
                        _socket = socket;
                        _invoker = invoker;
                    }
                    return socket;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    invoker.Dispose();
                    socket.Dispose();
                    ct.ThrowIfCancellationRequested();
                }
            }

            throw new InvalidOperationException($"无法连接电视 {_host}: {lastError?.Message ?? "未知错误"}");
        }

        /// <summary>
        /// The connection is made by this handler instead of the shared HTTP stack: it never uses the system proxy and
        /// binds the outgoing socket to <paramref name="source"/>, which keeps the TV reachable while a VPN owns the
        /// default route. The TV's certificate is self-signed, so validation is bypassed for this host only.
        /// </summary>
        private static HttpMessageInvoker CreateInvoker(IPAddress? source)
        {
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectCallback = (context, token) => ConnectBoundAsync(context, source, token),
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                },
            };
            return new HttpMessageInvoker(handler);
        }

        private static async ValueTask<Stream> ConnectBoundAsync(SocketsHttpConnectionContext context, IPAddress? source,
                                                                 CancellationToken ct)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                if (source != null) socket.Bind(new IPEndPoint(source, 0));
                await socket.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
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
            string json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        private void DispatchMessage(JsonElement message)
        {
            var type = GetString(message, "type");
            var id = GetString(message, "id");

            switch (type)
            {
                case HelloRequestId:
                case ResponseType when id == HelloRequestId:
                    BufferOrCompleteHello(message);
                    return;
                case RegisteredType:
                case ResponseType when id == RegisterRequestId:
                case ErrorType when id == RegisterRequestId:
                    DispatchRegister(message, type);
                    return;
                case ResponseType:
                case ErrorType:
                    ResolveRequest(id, message);
                    return;
            }
        }

        /// <summary>
        /// Only a message that really finished the registration wakes the waiter: "registered", or a response carrying
        /// a client-key. Other id=register responses are just an acknowledgement some firmwares send before the key, and
        /// treating those as done would leave the client-key unread, so the TV would ask again on every start.
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

        /// <summary>Some firmwares push hello right after connecting, before the client asks; it is buffered until then.</summary>
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

        private async Task<JsonElement> SendHandshakeAsync(string type, Dictionary<string, object> payload, int timeoutMs,
                                                           CancellationToken ct)
        {
            if (type == HelloRequestId && TryTakeBufferedHello(out var buffered))
                return buffered;

            var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                if (type == HelloRequestId) _helloWaiter = waiter;
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
                throw new TimeoutException(type == RegisterRequestId
                    ? "配对超时（电视是否弹出确认提示？）"
                    : "hello 握手超时");
            }
            finally
            {
                lock (_sync)
                {
                    if (type == HelloRequestId)
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

        private async Task TeardownAsync(bool graceful)
        {
            ClientWebSocket? socket;
            HttpMessageInvoker? invoker;
            CancellationTokenSource? cts;
            lock (_sync)
            {
                socket = _socket; _socket = null;
                invoker = _invoker; _invoker = null;
                cts = _receiveCts; _receiveCts = null;
            }

            try { cts?.Cancel(); } catch { }
            cts?.Dispose();

            if (socket != null)
            {
                if (graceful && socket.State == WebSocketState.Open)
                {
                    try
                    {
                        using var timeout = new CancellationTokenSource(ShutdownTimeoutMs);
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, CloseReason, timeout.Token)
                            .ConfigureAwait(false);
                    }
                    catch { }
                }
                socket.Dispose();
            }

            invoker?.Dispose();
            FailWaiters(new InvalidOperationException(ConnectionResetMessage));
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
            await TeardownAsync(graceful: true).ConfigureAwait(false);
        }

        /// <summary>Model name comes from getSystemInfo, the webOS version from hello; both may be missing.</summary>
        private static TvDeviceInfo? ReadTvInfo(JsonElement hello, JsonElement? systemInfo)
        {
            var helloPayload = ReadPayload(hello);
            var infoPayload = systemInfo is JsonElement info ? ReadPayload(info) : null;

            string? modelName = GetString(infoPayload, "modelName") ?? GetString(helloPayload, "modelName");
            string? version = GetString(helloPayload, "deviceOSVersion") ?? GetString(infoPayload, "deviceOSVersion");
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

        private static string? GetString(JsonElement? element, string property) =>
            element is JsonElement value ? GetString(value, property) : null;

        private static string? GetString(JsonElement element, string property)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) return null;

            string? text = value.GetString();
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

            TeardownAsync(graceful: false).GetAwaiter().GetResult();
        }
    }

    /// <summary>Model name and webOS version of the connected TV.</summary>
    public readonly struct TvDeviceInfo
    {
        public string? ModelName { get; }
        public string? WebOSVersion { get; }

        public TvDeviceInfo(string? modelName, string? webOSVersion)
        {
            ModelName = modelName;
            WebOSVersion = webOSVersion;
        }
    }
}
