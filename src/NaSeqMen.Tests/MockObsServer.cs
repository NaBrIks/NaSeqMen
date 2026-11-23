using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NaSeqMen.Tests;

internal sealed record MockObsRequest(string RequestType, string RequestId, JsonElement? RequestData);

internal sealed class MockObsServer : IAsyncDisposable
{
    public const string Challenge = "test-challenge";
    public const string Salt = "test-salt";
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly string _password;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentQueue<MockObsRequest> _requests = new();
    private readonly Task _acceptTask;

    private volatile WebSocket? _activeSocket;
    private int _identifiedCount;
    private int _authFailureCount;
    private int _lastEventSubscriptions;
    private string _currentScene = "Main";
    private bool _micMuted;

    public MockObsServer(string password = "secret123")
    {
        _password = password;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public int Port { get; }
    public int IdentifiedCount => Volatile.Read(ref _identifiedCount);
    public int AuthFailureCount => Volatile.Read(ref _authFailureCount);
    public int LastEventSubscriptions => Volatile.Read(ref _lastEventSubscriptions);
    public bool IsClientConnected => _activeSocket is { State: WebSocketState.Open };

    public IReadOnlyList<MockObsRequest> GetRequests() => _requests.ToArray();

    public Task SendVolumeMetersAsync(string inputName, double[][] inputLevelsMul)
        => SendEventAsync("InputVolumeMeters", 1 << 16, new
        {
            inputs = new[]
            {
                new { inputName, inputLevelsMul },
            },
        });

    public Task SendVolumeChangedAsync(string inputName, double inputVolumeMul, double inputVolumeDb)
        => SendEventAsync("InputVolumeChanged", 1 << 3, new
        {
            inputName,
            inputVolumeMul,
            inputVolumeDb,
        });

    public Task SendEventAsync(string eventType, int eventIntent, object eventData)
        => SendJsonAsync(new
        {
            op = 5,
            d = new
            {
                eventType,
                eventIntent,
                eventData,
            },
        }, _cts.Token);

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                client?.Dispose();
                break;
            }
            catch (ObjectDisposedException)
            {
                client?.Dispose();
                break;
            }
            catch
            {
                client?.Dispose();
            }
        }
    }

    private async Task HandleClientAsync(TcpClient tcp, CancellationToken ct)
    {
        using (tcp)
        await using (var stream = tcp.GetStream())
        {
            WebSocket? ws = null;
            try
            {
                if (!await PerformWebSocketHandshakeAsync(stream, ct).ConfigureAwait(false))
                    return;

                ws = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));

                // 1. Op 0 Hello
                await SendJsonToSocketAsync(ws, new
                {
                    op = 0,
                    d = new
                    {
                        obsWebSocketVersion = "5.5.0",
                        rpcVersion = 1,
                        authentication = new
                        {
                            challenge = Challenge,
                            salt = Salt,
                        },
                    },
                }, ct).ConfigureAwait(false);

                // 2. Op 1 Identify
                var identifyPayload = await ReceiveTextAsync(ws, ct).ConfigureAwait(false);
                if (identifyPayload is null)
                    return;

                using var idDoc = JsonDocument.Parse(identifyPayload);
                var root = idDoc.RootElement;
                if (!root.TryGetProperty("op", out var opEl) || opEl.GetInt32() != 1 ||
                    !root.TryGetProperty("d", out var idData))
                {
                    await ws.CloseAsync(WebSocketCloseStatus.ProtocolError, "Expected Identify", ct).ConfigureAwait(false);
                    return;
                }

                var providedAuth = idData.TryGetProperty("authentication", out var authEl) ? authEl.GetString() : null;
                var expectedAuth = ComputeExpectedAuth(_password, Salt, Challenge);
                if (!string.Equals(providedAuth, expectedAuth, StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _authFailureCount);
                    await ws.CloseAsync((WebSocketCloseStatus)4009, "Authentication failed", ct).ConfigureAwait(false);
                    return;
                }

                if (idData.TryGetProperty("eventSubscriptions", out var subsEl) && subsEl.TryGetInt32(out var subs))
                    Volatile.Write(ref _lastEventSubscriptions, subs);

                // 3. Op 2 Identified
                await SendJsonToSocketAsync(ws, new
                {
                    op = 2,
                    d = new { negotiatedRpcVersion = 1 },
                }, ct).ConfigureAwait(false);

                _activeSocket = ws;
                Interlocked.Increment(ref _identifiedCount);

                // 4. Request loop (Op 6 -> Op 7)
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var text = await ReceiveTextAsync(ws, ct).ConfigureAwait(false);
                    if (text is null)
                        break;

                    await HandleMessageAsync(ws, text, ct).ConfigureAwait(false);
                }
            }
            catch
            {
                // Normal shutdown or client disconnect
            }
            finally
            {
                if (ws is not null)
                {
                    Interlocked.CompareExchange(ref _activeSocket, null, ws);
                    ws.Dispose();
                }
            }
        }
    }

    private async Task HandleMessageAsync(WebSocket ws, string json, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("op", out var opEl) || opEl.GetInt32() != 6 ||
            !root.TryGetProperty("d", out var d))
        {
            return;
        }

        var requestType = d.TryGetProperty("requestType", out var rt) ? rt.GetString() ?? string.Empty : string.Empty;
        var requestId = d.TryGetProperty("requestId", out var ri) ? ri.GetString() ?? string.Empty : string.Empty;
        JsonElement? requestData = d.TryGetProperty("requestData", out var rd) ? rd.Clone() : null;

        _requests.Enqueue(new MockObsRequest(requestType, requestId, requestData));

        object responseData = requestType switch
        {
            "GetVersion" => new { obsVersion = "30.2.0", obsWebSocketVersion = "5.5.0", rpcVersion = 1 },
            "GetSceneList" => new
            {
                currentProgramSceneName = _currentScene,
                scenes = new object[]
                {
                    new { sceneName = "BRB", sceneIndex = 0 },
                    new { sceneName = "Main", sceneIndex = 1 },
                },
            },
            "GetInputList" => new
            {
                inputs = new object[]
                {
                    new { inputName = "Mic/Aux", inputKind = "wasapi_input_capture" },
                    new { inputName = "Desktop Audio", inputKind = "wasapi_output_capture" },
                },
            },
            "ToggleInputMute" => ToggleMuteResponse(),
            "SetInputMute" => new { },
            "SetInputVolume" => new { },
            "SetCurrentProgramScene" => UpdateSceneResponse(requestData),
            "ToggleRecord" => new { outputActive = true },
            "ToggleStream" => new { outputActive = true },
            _ => new { },
        };

        await SendJsonToSocketAsync(ws, new
        {
            op = 7,
            d = new
            {
                requestType,
                requestId,
                requestStatus = new { result = true, code = 100 },
                responseData,
            },
        }, ct).ConfigureAwait(false);
    }

    private object ToggleMuteResponse()
    {
        _micMuted = !_micMuted;
        return new { inputMuted = _micMuted };
    }

    private object UpdateSceneResponse(JsonElement? data)
    {
        if (data is { ValueKind: JsonValueKind.Object } obj &&
            obj.TryGetProperty("sceneName", out var sn) &&
            sn.ValueKind == JsonValueKind.String)
        {
            _currentScene = sn.GetString() ?? _currentScene;
        }
        return new { };
    }

    private async Task SendJsonAsync(object payload, CancellationToken ct)
    {
        var ws = _activeSocket ?? throw new InvalidOperationException("No OBS client connected to MockObsServer.");
        await SendJsonToSocketAsync(ws, payload, ct).ConfigureAwait(false);
    }

    private async Task SendJsonToSocketAsync(WebSocket ws, object payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[8192];
        using var ms = new MemoryStream();
        WebSocketReceiveResult res;
        do
        {
            res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct).ConfigureAwait(false);
            if (res.MessageType == WebSocketMessageType.Close)
            {
                if (ws.State == WebSocketState.CloseReceived)
                {
                    try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", ct).ConfigureAwait(false); }
                    catch { }
                }
                return null;
            }
            if (res.Count > 0)
                ms.Write(buf, 0, res.Count);
        }
        while (!res.EndOfMessage);

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task<bool> PerformWebSocketHandshakeAsync(NetworkStream stream, CancellationToken ct)
    {
        var headerBuf = new byte[4096];
        var total = 0;
        while (total < headerBuf.Length)
        {
            var read = await stream.ReadAsync(headerBuf.AsMemory(total, headerBuf.Length - total), ct).ConfigureAwait(false);
            if (read == 0)
                return false;
            total += read;
            if (Encoding.ASCII.GetString(headerBuf, 0, total).Contains("\r\n\r\n", StringComparison.Ordinal))
                break;
        }

        var requestText = Encoding.ASCII.GetString(headerBuf, 0, total);
        string? wsKey = null;
        foreach (var line in requestText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            {
                wsKey = line["Sec-WebSocket-Key:".Length..].Trim();
                break;
            }
        }

        if (string.IsNullOrEmpty(wsKey))
            return false;

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(wsKey + WebSocketGuid)));
        var response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n";

        var respBytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(respBytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
        return true;
    }

    private static string ComputeExpectedAuth(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try { _listener.Stop(); } catch { }
        try { await _acceptTask.ConfigureAwait(false); } catch { }
        _sendLock.Dispose();
        _cts.Dispose();
    }
}
