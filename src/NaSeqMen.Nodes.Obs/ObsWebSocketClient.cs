using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NaSeqMen.Nodes.Obs;

/// <summary>
/// Spec-compliant OBS Studio WebSocket v5 client with auto-reconnect, SHA256 challenge auth,
/// fragmented message reassembly, and thread-safe request/response correlation.
/// </summary>
public sealed class ObsWebSocketClient : IAsyncDisposable
{
    public const int OpHello = 0;
    public const int OpIdentify = 1;
    public const int OpIdentified = 2;
    public const int OpEvent = 5;
    public const int OpRequest = 6;
    public const int OpRequestResponse = 7;

    public const int CloseCodeAuthenticationFailed = 4009;

    public const int EventSubscriptionGeneral = 1 << 0;
    public const int EventSubscriptionScenes = 1 << 2;
    public const int EventSubscriptionInputs = 1 << 3;
    public const int EventSubscriptionTransitions = 1 << 4;
    public const int EventSubscriptionOutputs = 1 << 6;
    public const int EventSubscriptionInputVolumeMeters = 1 << 16;

    public const int DefaultStandardEventSubscriptions =
        EventSubscriptionGeneral |
        EventSubscriptionScenes |
        EventSubscriptionInputs |
        EventSubscriptionTransitions |
        EventSubscriptionOutputs;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ObsRequestResponse>> _pendingRequests =
        new(StringComparer.Ordinal);

    private volatile ClientWebSocket? _socket;
    private volatile bool _isIdentified;
    private long _requestSequence;

    public event Action<ObsConnectionState, string?>? ConnectionStateChanged;
    public event Action<ObsEvent>? EventReceived;

    public bool IsConnected => _isIdentified && _socket is { State: WebSocketState.Open };

    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(3);

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public static Uri BuildUri(string host, int port)
    {
        var trimmed = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        if (trimmed.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(trimmed);
        }

        var safePort = Math.Clamp(port <= 0 ? 4455 : port, 1, 65535);
        var formattedHost = trimmed.Contains(':') && !trimmed.StartsWith('[') ? $"[{trimmed}]" : trimmed;
        return new Uri($"ws://{formattedHost}:{safePort}");
    }

    public static string ComputeAuthentication(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    public static int BuildEventSubscriptions(bool subscribeMeters)
        => subscribeMeters
            ? DefaultStandardEventSubscriptions | EventSubscriptionInputVolumeMeters
            : DefaultStandardEventSubscriptions;

    public Task ConnectLoopAsync(string host, int port, string? password, bool subscribeMeters, CancellationToken cancellationToken)
        => ConnectLoopAsync(BuildUri(host, port), password, subscribeMeters, cancellationToken);

    public async Task ConnectLoopAsync(Uri uri, string? password, bool subscribeMeters, CancellationToken cancellationToken)
    {
        var hasAttempted = false;
        var lastWasAuthFailed = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!hasAttempted)
            {
                NotifyState(ObsConnectionState.Connecting, null);
                hasAttempted = true;
            }
            else if (!lastWasAuthFailed)
            {
                NotifyState(ObsConnectionState.Reconnecting, null);
            }

            try
            {
                var outcome = await RunSingleSessionAsync(uri, password ?? string.Empty, subscribeMeters, cancellationToken)
                    .ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                    break;

                lastWasAuthFailed = outcome.AuthFailed;
                NotifyState(
                    outcome.AuthFailed ? ObsConnectionState.AuthFailed : ObsConnectionState.Reconnecting,
                    outcome.Error);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                lastWasAuthFailed = false;
                NotifyState(ObsConnectionState.Reconnecting, ex.Message);
            }

            try
            {
                await Task.Delay(ReconnectDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        NotifyState(ObsConnectionState.Disconnected, null);
    }

    public async Task<ObsRequestResponse> SendRequestAsync(
        string requestType,
        object? requestData = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestType);
        if (!IsConnected)
            throw new InvalidOperationException("Not connected to OBS WebSocket.");

        var requestId = $"req-{Interlocked.Increment(ref _requestSequence)}";
        var tcs = new TaskCompletionSource<ObsRequestResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[requestId] = tcs;

        try
        {
            var dataPayload = new Dictionary<string, object?>
            {
                ["requestType"] = requestType,
                ["requestId"] = requestId,
            };

            if (requestData is not null &&
                !(requestData is JsonElement je && (je.ValueKind == JsonValueKind.Null || je.ValueKind == JsonValueKind.Undefined)))
            {
                dataPayload["requestData"] = requestData;
            }

            await SendJsonAsync(new Dictionary<string, object?>
            {
                ["op"] = OpRequest,
                ["d"] = dataPayload,
            }, cancellationToken).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(RequestTimeout);
            return await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _isIdentified = false;
        var socket = Interlocked.Exchange(ref _socket, null);
        FailAllPendingRequests(new ObjectDisposedException(nameof(ObsWebSocketClient)));

        if (socket is not null)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disposed", closeCts.Token)
                        .ConfigureAwait(false);
                }
            }
            catch { }
            finally { socket.Dispose(); }
        }
    }

    private async Task<SessionOutcome> RunSingleSessionAsync(
        Uri uri,
        string password,
        bool subscribeMeters,
        CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        _socket = socket;

        var buffer = new byte[16384];
        try
        {
            // Step 1: Receive Hello (op 0)
            var helloMsg = await ReceiveFullMessageAsync(socket, buffer, cancellationToken).ConfigureAwait(false);
            if (helloMsg.IsClose)
                return BuildCloseOutcome(helloMsg.CloseCode ?? (int?)socket.CloseStatus, helloMsg.CloseDescription);

            var authToken = ParseHelloAndComputeAuth(helloMsg.Payload!, password, out var helloError);
            if (helloError is not null)
                return new SessionOutcome(false, helloError);

            // Step 2: Send Identify (op 1)
            var identifyData = new Dictionary<string, object?>
            {
                ["rpcVersion"] = 1,
                ["eventSubscriptions"] = BuildEventSubscriptions(subscribeMeters),
            };
            if (authToken is not null)
                identifyData["authentication"] = authToken;

            await SendJsonAsync(new Dictionary<string, object?> { ["op"] = OpIdentify, ["d"] = identifyData }, cancellationToken)
                .ConfigureAwait(false);

            // Step 3: Receive Identified (op 2)
            var identifiedMsg = await ReceiveFullMessageAsync(socket, buffer, cancellationToken).ConfigureAwait(false);
            if (identifiedMsg.IsClose)
                return BuildCloseOutcome(identifiedMsg.CloseCode ?? (int?)socket.CloseStatus, identifiedMsg.CloseDescription);

            using (var idDoc = JsonDocument.Parse(identifiedMsg.Payload!))
            {
                if (!ObsJsonHelpers.TryGetInt32(idDoc.RootElement, "op", out var op) || op != OpIdentified)
                    return new SessionOutcome(false, $"Expected OBS Identified (op 2), got op {op}.");
            }

            _isIdentified = true;
            NotifyState(ObsConnectionState.Connected, null);

            // Step 4: Main receive loop
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var msg = await ReceiveFullMessageAsync(socket, buffer, cancellationToken).ConfigureAwait(false);
                if (msg.IsClose)
                {
                    await TryAcknowledgeCloseAsync(socket, cancellationToken).ConfigureAwait(false);
                    return BuildCloseOutcome(msg.CloseCode ?? (int?)socket.CloseStatus, msg.CloseDescription);
                }

                if (msg.Payload is { Length: > 0 })
                    HandleIncomingJson(msg.Payload);
            }

            return new SessionOutcome(false, null);
        }
        finally
        {
            _isIdentified = false;
            Interlocked.CompareExchange(ref _socket, null, socket);
            FailAllPendingRequests(cancellationToken.IsCancellationRequested
                ? new OperationCanceledException(cancellationToken)
                : new InvalidOperationException("OBS WebSocket disconnected."));
        }
    }

    private static string? ParseHelloAndComputeAuth(byte[] payload, string password, out string? error)
    {
        error = null;
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        if (!ObsJsonHelpers.TryGetInt32(root, "op", out var op) || op != OpHello)
        {
            error = $"Expected OBS Hello (op 0), got op {op}.";
            return null;
        }

        if (root.TryGetProperty("d", out var helloData) &&
            helloData.TryGetProperty("authentication", out var authObj) &&
            authObj.ValueKind == JsonValueKind.Object)
        {
            var challenge = ObsJsonHelpers.GetStringOrNull(authObj, "challenge");
            var salt = ObsJsonHelpers.GetStringOrNull(authObj, "salt");
            if (!string.IsNullOrEmpty(challenge) && !string.IsNullOrEmpty(salt))
                return ComputeAuthentication(password, salt, challenge);
        }

        return null;
    }

    private void HandleIncomingJson(byte[] payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        if (!ObsJsonHelpers.TryGetInt32(root, "op", out var op) || !root.TryGetProperty("d", out var data))
            return;

        if (op == OpEvent)
        {
            var eventType = ObsJsonHelpers.GetStringOrNull(data, "eventType");
            if (string.IsNullOrEmpty(eventType))
                return;

            ObsJsonHelpers.TryGetInt32(data, "eventIntent", out var eventIntent);
            JsonElement? eventData = data.TryGetProperty("eventData", out var ed) ? ed.Clone() : null;
            try { EventReceived?.Invoke(new ObsEvent(eventType, eventIntent, eventData)); }
            catch { }
        }
        else if (op == OpRequestResponse)
        {
            var requestId = ObsJsonHelpers.GetStringOrNull(data, "requestId");
            if (string.IsNullOrEmpty(requestId))
                return;

            var requestType = ObsJsonHelpers.GetStringOrNull(data, "requestType") ?? string.Empty;
            var result = false;
            var code = 0;
            string? comment = null;

            if (data.TryGetProperty("requestStatus", out var status) && status.ValueKind == JsonValueKind.Object)
            {
                ObsJsonHelpers.TryGetBool(status, "result", out result);
                ObsJsonHelpers.TryGetInt32(status, "code", out code);
                comment = ObsJsonHelpers.GetStringOrNull(status, "comment");
            }

            JsonElement? responseData = data.TryGetProperty("responseData", out var rd) ? rd.Clone() : null;
            if (_pendingRequests.TryRemove(requestId, out var tcs))
                tcs.TrySetResult(new ObsRequestResponse(requestType, requestId, result, code, comment, responseData));
        }
    }

    private async Task SendJsonAsync(object payload, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var socket = _socket;
            if (socket is not { State: WebSocketState.Open })
                throw new InvalidOperationException("OBS WebSocket is not open.");

            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static async Task<ReceivedMessage> ReceiveFullMessageAsync(ClientWebSocket socket, byte[] buffer, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return new ReceivedMessage(true, null, (int?)result.CloseStatus, result.CloseStatusDescription);

            if (result.Count > 0)
                ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return new ReceivedMessage(false, ms.ToArray(), null, null);
    }

    private static async Task TryAcknowledgeCloseAsync(ClientWebSocket socket, CancellationToken ct)
    {
        if (socket.State != WebSocketState.CloseReceived)
            return;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(1));
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cts.Token).ConfigureAwait(false);
        }
        catch { }
    }

    private void FailAllPendingRequests(Exception ex)
    {
        foreach (var kvp in _pendingRequests)
        {
            if (_pendingRequests.TryRemove(kvp.Key, out var tcs))
                tcs.TrySetException(ex);
        }
    }

    private void NotifyState(ObsConnectionState state, string? error)
    {
        try { ConnectionStateChanged?.Invoke(state, error); }
        catch { }
    }

    private static SessionOutcome BuildCloseOutcome(int? closeCode, string? description)
    {
        if (closeCode == CloseCodeAuthenticationFailed)
        {
            var reason = string.IsNullOrWhiteSpace(description)
                ? "Authentication failed (4009)"
                : $"Authentication failed: {description}";
            return new SessionOutcome(true, reason);
        }

        var error = closeCode.HasValue
            ? $"Closed ({closeCode.Value}){(string.IsNullOrWhiteSpace(description) ? "" : $": {description}")}"
            : description;
        return new SessionOutcome(false, error);
    }

    private readonly record struct ReceivedMessage(bool IsClose, byte[]? Payload, int? CloseCode, string? CloseDescription);
    private readonly record struct SessionOutcome(bool AuthFailed, string? Error);
}
