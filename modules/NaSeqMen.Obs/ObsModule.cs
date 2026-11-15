// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NaSeqMen.Contracts;
using static NaSeqMen.Contracts.MessageCatalog;

namespace NaSeqMen.Obs;

public sealed class ObsModule : NaModule
{
    private readonly ConcurrentDictionary<string, ObsSession> _connections = new();
    public override ModuleDescriptor Descriptor => new("obs", M("module.obs"), "0.1.0", true, false,
        [new("startRecord", M("operation.obs.startRecord"), []), new("stopRecord", M("operation.obs.stopRecord"), []),
         new("toggleRecord", M("operation.obs.toggleRecord"), []), new("setMute", M("operation.obs.setMute"), ["input", "muted"]),
         new("toggleMute", M("operation.obs.toggleMute"), ["input"]),
         new("setVolume", M("operation.obs.setVolume"), ["input", "volume"]),
         new("setScene", M("operation.obs.setScene"), ["scene"]), new("request", M("operation.obs.request"), ["type", "data"])], []);

    public override async ValueTask ConnectAsync(ConnectionDefinition connection, CancellationToken cancellationToken = default)
    {
        await DisconnectAsync(connection.Id, cancellationToken);
        var address = connection.Settings.GetValueOrDefault("url", "ws://127.0.0.1:4455");
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss"))
            throw Error<ArgumentException>(M("obs.url"));
        var password = connection.Settings.GetValueOrDefault("password", "");
        if (connection.Settings.TryGetValue("passwordEnvironment", out var variable) && variable.Length != 0)
            password = Environment.GetEnvironmentVariable(variable) ?? throw Error<ArgumentException>(M("obs.passwordVariable"));
        var session = new ObsSession(connection.Id, Host);
        try
        {
            await session.ConnectAsync(uri, password, cancellationToken);
            if (!_connections.TryAdd(connection.Id, session)) throw Error<InvalidOperationException>(M("obs.connectionExists"));
        }
        catch { await session.DisposeAsync(); throw; }
    }

    public override async ValueTask DisconnectAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        if (_connections.TryRemove(connectionId, out var session)) await session.DisposeAsync();
    }

    public override async ValueTask<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(request.ConnectionId, out var session)) return new(false, M("obs.notConnected"));
        try
        {
            var (type, data) = BuildRequest(request.Operation, request.Arguments);
            var response = await session.RequestAsync(type, data, cancellationToken);
            return response.ValueKind == JsonValueKind.Undefined ? new(true, M("obs.completed")) : new(true, response.GetRawText());
        }
        catch (Exception error) when (error is not OperationCanceledException) { return new(false, FromException(error)); }
    }

    private static (string Type, object Data) BuildRequest(string operation, Dictionary<string, string> args)
    {
        static string Required(Dictionary<string, string> values, string key) =>
            values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value :
                throw Error<ArgumentException>(M("obs.parameterMissing", ("parameter", key)));
        return operation switch
        {
            "startRecord" => ("StartRecord", new { }),
            "stopRecord" => ("StopRecord", new { }),
            "toggleRecord" => ("ToggleRecord", new { }),
            "setMute" => ("SetInputMute", new { inputName = Required(args, "input"), inputMuted = bool.Parse(Required(args, "muted")) }),
            "toggleMute" => ("ToggleInputMute", new { inputName = Required(args, "input") }),
            "setVolume" => ("SetInputVolume", new { inputName = Required(args, "input"), inputVolumeMul = Volume(Required(args, "volume")) }),
            "setScene" => ("SetCurrentProgramScene", new { sceneName = Required(args, "scene") }),
            "request" => (Required(args, "type"), RequestData(args.GetValueOrDefault("data", "{}"))),
            _ => throw Error<ArgumentException>(M("obs.actionUnknown"))
        };
    }

    private static double Volume(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value < 0 || value > 1)
            throw Error<ArgumentException>(M("obs.volume"));
        return value;
    }

    private static JsonElement RequestData(string text)
    {
        using var json = JsonDocument.Parse(text);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw Error<ArgumentException>(M("obs.data"));
        return json.RootElement.Clone();
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var id in _connections.Keys) await DisconnectAsync(id);
    }

    private sealed class ObsSession(string connectionId, IModuleHost host) : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private readonly SemaphoreSlim _sendGate = new(1);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly Channel<Signal> _events = Channel.CreateBounded<Signal>(new BoundedChannelOptions(256)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
        private Task? _receiver;
        private Task? _publisher;

        public async Task ConnectAsync(Uri address, string password, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            await _socket.ConnectAsync(address, timeout.Token);
            using var hello = await ReceiveMessageAsync(timeout.Token);
            if (hello.RootElement.GetProperty("op").GetInt32() != 0) throw Error<IOException>(M("obs.expectedHello"));
            var identify = new Dictionary<string, object> { ["rpcVersion"] = 1, ["eventSubscriptions"] = 2047 };
            if (hello.RootElement.GetProperty("d").TryGetProperty("authentication", out var auth))
            {
                if (password.Length == 0) throw Error<IOException>(M("obs.passwordRequired"));
                var salt = auth.GetProperty("salt").GetString()!;
                var challenge = auth.GetProperty("challenge").GetString()!;
                var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
                identify["authentication"] = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
            }
            await SendAsync(new { op = 1, d = identify }, timeout.Token);
            using var ready = await ReceiveMessageAsync(timeout.Token);
            if (ready.RootElement.GetProperty("op").GetInt32() != 2) throw Error<IOException>(M("obs.notIdentified"));
            _receiver = ReceiveLoopAsync();
            _publisher = PublishLoopAsync();
        }

        public async Task<JsonElement> RequestAsync(string type, object data, CancellationToken cancellationToken)
        {
            if (_socket.State != WebSocketState.Open || _stop.IsCancellationRequested) throw Error<IOException>(M("obs.connectionClosed"));
            var id = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = completion;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await SendAsync(new { op = 6, d = new { requestType = type, requestId = id, requestData = data } }, timeout.Token);
                var response = await completion.Task.WaitAsync(timeout.Token);
                var status = response.GetProperty("requestStatus");
                if (!status.GetProperty("result").GetBoolean())
                    throw Error<IOException>(M("obs.requestFailed", ("request", type), ("status", status.GetProperty("code").GetInt32()),
                        ("detail", status.TryGetProperty("comment", out var comment) ? comment.GetString() : (object)M("obs.requestRejected"))));
                var result = response.TryGetProperty("responseData", out var resultData) ? resultData.Clone() : default;
                Queue(new Signal { Module = "obs", ConnectionId = connectionId, Source = "response", Control = type,
                    Phase = "response", ValueType = "text", Value = result.ValueKind == JsonValueKind.Undefined ? "{}" : result.GetRawText() });
                return result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_stop.IsCancellationRequested)
            { throw Error<TimeoutException>(M("obs.timeout", ("request", type))); }
            finally { _pending.TryRemove(id, out _); }
        }

        private async Task SendAsync(object message, CancellationToken cancellationToken)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            await _sendGate.WaitAsync(cancellationToken);
            try { await _socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, cancellationToken); }
            finally { _sendGate.Release(); }
        }

        private async Task<JsonDocument> ReceiveMessageAsync(CancellationToken cancellationToken)
        {
            using var message = new MemoryStream();
            var buffer = new byte[8192];
            ValueWebSocketReceiveResult read;
            do
            {
                read = await _socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
                if (read.MessageType == WebSocketMessageType.Close) throw Error<IOException>(M("obs.peerClosed"));
                if (read.MessageType != WebSocketMessageType.Text) throw Error<IOException>(M("obs.expectedText"));
                if (message.Length + read.Count > 1_048_576) throw Error<IOException>(M("obs.messageTooLarge"));
                message.Write(buffer, 0, read.Count);
            } while (!read.EndOfMessage);
            return JsonDocument.Parse(message.ToArray());
        }

        private async Task ReceiveLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var message = await ReceiveMessageAsync(_stop.Token);
                    var op = message.RootElement.GetProperty("op").GetInt32();
                    var data = message.RootElement.GetProperty("d");
                    if (op == 7 && _pending.TryGetValue(data.GetProperty("requestId").GetString()!, out var completion))
                        completion.TrySetResult(data.Clone());
                    else if (op == 5)
                        Queue(new Signal { Module = "obs", ConnectionId = connectionId, Source = "event",
                            Control = data.GetProperty("eventType").GetString()!, Phase = "change", ValueType = "text",
                            Value = data.TryGetProperty("eventData", out var payload) ? payload.GetRawText() : "{}" });
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception error)
            {
                host.Trace(new(DateTimeOffset.UtcNow, "", "", "error", M("obs.error", ("detail", FromException(error)))));
                foreach (var request in _pending.Values) request.TrySetException(error);
            }
            finally
            {
                _stop.Cancel();
                foreach (var request in _pending.Values) request.TrySetCanceled();
                _events.Writer.TryComplete();
            }
        }

        private void Queue(Signal signal)
        {
            if (!_events.Writer.TryWrite(signal))
                host.Trace(new(DateTimeOffset.UtcNow, signal.Id, "", "error", M("obs.routingOverload")));
        }

        private async Task PublishLoopAsync()
        {
            try
            {
                await foreach (var signal in _events.Reader.ReadAllAsync(_stop.Token))
                    await host.PublishAsync(signal, _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception error) { host.Trace(new(DateTimeOffset.UtcNow, "", "", "error", M("obs.error", ("detail", FromException(error))))); }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _socket.Abort();
            _events.Writer.TryComplete();
            foreach (var request in _pending.Values) request.TrySetCanceled();
            if (_receiver is not null) await _receiver;
            if (_publisher is not null) await _publisher;
            _socket.Dispose();
            _stop.Dispose();
            _sendGate.Dispose();
        }
    }
}
