// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NaSeqMen.Contracts;
using NaSeqMen.Desktop.Windows;
using NaSeqMen.Input.Windows;
using NaSeqMen.Obs;

var checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAIL: " + label);
    Console.WriteLine("PASS: " + label);
    checks++;
}

if (args.Length == 2 && args[0] == "--obs-settings")
{
    using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(args[1]));
    await using var live = new ObsModule();
    await live.InitializeAsync(new TestHost());
    await live.ConnectAsync(new ConnectionDefinition { Id = "live-readonly", Settings = new()
    {
        ["url"] = settings.RootElement.GetProperty("address").GetString()!,
        ["password"] = settings.RootElement.GetProperty("password").GetString()!
    }});
    foreach (var type in new[] { "GetVersion", "GetSceneList", "GetRecordStatus" })
    {
        var result = await live.ExecuteAsync(new("live-readonly", "request", new() { ["type"] = type }, new()));
        Check(result.Success, "live OBS read-only " + type);
        if (type == "GetRecordStatus")
        {
            using var status = JsonDocument.Parse(result.Message);
            Check(!status.RootElement.GetProperty("outputActive").GetBoolean(), "live OBS is not recording");
        }
    }
    Console.WriteLine("LIVE OBS READ-ONLY CHECKS PASSED; no recording, scene or audio mutation sent.");
    return;
}

await KeyboardChecks.Run(Check);

var observed = new ConcurrentQueue<JsonElement>();
var expectedAuth = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(
    Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("test-passwordsalt"))) + "challenge")));
var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
await using var server = builder.Build();
server.UseWebSockets();
server.Map("/obs", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    try
    {
        await Send(socket, new { op = 0, d = new { rpcVersion = 1, authentication = new { salt = "salt", challenge = "challenge" } } });
        using var identify = await Receive(socket);
        var auth = identify.RootElement.GetProperty("d").GetProperty("authentication").GetString();
        if (auth != expectedAuth)
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "auth failed", CancellationToken.None);
            return;
        }
        await Send(socket, new { op = 2, d = new { negotiatedRpcVersion = 1 } });
        while (socket.State == WebSocketState.Open)
        {
            using var incoming = await Receive(socket);
            var data = incoming.RootElement.GetProperty("d");
            observed.Enqueue(data.Clone());
            var type = data.GetProperty("requestType").GetString()!;
            var id = data.GetProperty("requestId").GetString()!;
            if (type == "SetInputVolume")
                await Send(socket, new { op = 5, d = new { eventType = "InputVolumeChanged", eventData = new
                { inputName = "Mic", inputVolumeMul = data.GetProperty("requestData").GetProperty("inputVolumeMul").GetDouble() } } });
            await Send(socket, new { op = 7, d = new { requestType = type, requestId = id,
                requestStatus = new { result = type != "Denied", code = type == "Denied" ? 600 : 100,
                    comment = type == "Denied" ? "mock rejection" : "" }, responseData = new { echo = type } } });
        }
    }
    catch (WebSocketException) { }
    catch (IOException) { }
    catch (OperationCanceledException) { }
});
await server.StartAsync();
var url = server.Urls.Single().Replace("http://", "ws://") + "/obs";
var host = new TestHost();
await using (var obs = new ObsModule())
{
    await obs.InitializeAsync(host);
    await obs.ConnectAsync(new ConnectionDefinition { Id = "test-obs", Module = "obs", Settings = new()
        { ["url"] = url, ["password"] = "test-password" } });
    Check(observed.IsEmpty, "connect/auth sends no snapshot requests");
    var context = new Signal { Module = "test", Source = "module-check" };
    async Task<ActionResult> Execute(string operation, Dictionary<string, string>? args = null) =>
        await obs.ExecuteAsync(new("test-obs", operation, args ?? [], context));
    Check((await Execute("startRecord")).Success, "StartRecord request accepted by mock");
    Check((await Execute("stopRecord")).Success, "StopRecord request accepted by mock");
    Check((await Execute("toggleRecord")).Success, "ToggleRecord request accepted by mock");
    Check((await Execute("setMute", new() { ["input"] = "Mic", ["muted"] = "true" })).Success, "SetInputMute bool parameter");
    Check((await Execute("toggleMute", new() { ["input"] = "Mic" })).Success, "ToggleInputMute request");
    Check((await Execute("setScene", new() { ["scene"] = "Scene A" })).Success, "SetCurrentProgramScene request");
    var volume = await Execute("setVolume", new() { ["input"] = "Mic", ["volume"] = "0.42" });
    Check(volume.Success, "SetInputVolume request");
    var changed = await host.Next(s => s.Source == "event" && s.Control == "InputVolumeChanged");
    Check(JsonDocument.Parse(changed.Value).RootElement.GetProperty("inputVolumeMul").GetDouble() == 0.42,
        "OBS event routes value through same connection");
    Check(changed.ConnectionId == "test-obs", "event retains connection identity");
    var requestCount = observed.Count;
    Check(!(await Execute("setVolume", new() { ["input"] = "Mic", ["volume"] = "NaN" })).Success, "nonfinite volume rejected");
    Check(!(await Execute("setVolume", new() { ["input"] = "Mic", ["volume"] = "1.1" })).Success, "volume outside range rejected");
    Check(!(await Execute("request", new() { ["type"] = "GetVersion", ["data"] = "[]" })).Success, "nonobject request data rejected");
    Check(observed.Count == requestCount, "invalid commands never reach socket");
    var rejected = await Execute("request", new() { ["type"] = "Denied", ["data"] = "{}" });
    Check(!rejected.Success && rejected.Message.Contains("600"), "OBS rejection is reported as failure");
    var concurrent = await Task.WhenAll(Execute("request", new() { ["type"] = "GetVersion" }),
        Execute("request", new() { ["type"] = "GetRecordStatus" }));
    Check(concurrent.All(x => x.Success) && concurrent[0].Message.Contains("GetVersion") &&
        concurrent[1].Message.Contains("GetRecordStatus"), "concurrent replies correlate with request IDs");
    var triggered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    host.OnSignal = async signal =>
    {
        if (signal.Source == "event") triggered.TrySetResult((await Execute("request", new() { ["type"] = "GetVersion" })).Success);
    };
    Check((await Execute("setVolume", new() { ["input"] = "Mic", ["volume"] = "0.5" })).Success, "second volume command accepted");
    Check(await triggered.Task.WaitAsync(TimeSpan.FromSeconds(5)), "event-triggered request does not deadlock receiver");
    host.OnSignal = null;
    await obs.DisconnectAsync("test-obs");
    Check(!(await Execute("startRecord")).Success, "disconnected action fails without queued replay");
}
await using (var bad = new ObsModule())
{
    await bad.InitializeAsync(host);
    var rejected = false;
    try { await bad.ConnectAsync(new ConnectionDefinition { Id = "bad", Settings = new() { ["url"] = url, ["password"] = "wrong" } }); }
    catch (IOException) { rejected = true; }
    Check(rejected, "wrong authentication rejected");
}
await using (var input = new InputModule())
{
    await input.InitializeAsync(host);
    var devices = await input.GetDevicesAsync();
    Check(!input.Descriptor.CanSuppress && devices.All(x => !x.CanSuppress), "Raw Input never advertises suppression");
    Check(devices.All(x => !string.IsNullOrWhiteSpace(x.Id)), "device enumeration provides interface identities");
    Console.WriteLine($"Enumerated {devices.Count} interfaces; device identifiers not logged.");
}
await using (var desktop = new DesktopModule())
{
    using var self = Process.GetCurrentProcess();
    Check(await desktop.EvaluateAsync(new("test", "processRunning", new() { ["process"] = self.ProcessName }, new())),
        "process condition checks current process");
    Check(!await desktop.EvaluateAsync(new("test", "processRunning", new() { ["process"] = "NaSeqMen-No-Such-Process-12893" }, new())),
        "process condition false for absent process");
}
await server.StopAsync();
var modifiers = new ModifierTracker();
Check(modifiers.Observe(new("mouse", "mouse", "button1", "down")).SourceKnown == ModifierKeys.None, "unobserved modifier is unknown");
var first = modifiers.Observe(new("keyboard-a", "keyboard", "", "down", ScanCode: 0x1D));
modifiers.Observe(new("keyboard-b", "keyboard", "", "down", ScanCode: 0x1D, Flags: 2));
var released = modifiers.Observe(new("keyboard-a", "keyboard", "", "up", ScanCode: 0x1D));
Check(first.SourceHeld == ModifierKeys.LeftCtrl && released.SourceHeld == ModifierKeys.None &&
      released.SourceKnown == ModifierKeys.LeftCtrl && released.GlobalHeld == ModifierKeys.RightCtrl, "per-device releases and immutable snapshots");
Check(modifiers.Observe(new("keyboard-b", "device", "connection", "removed")).GlobalHeld == ModifierKeys.None, "removal clears only that device");
Console.WriteLine($"MODULE CHECKS PASSED: {checks}. No SendInput, launch, real OBS or input capture was used.");

static async Task Send(WebSocket socket, object data) =>
    await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(data).AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);

static async Task<JsonDocument> Receive(WebSocket socket)
{
    var buffer = new byte[4096];
    using var stream = new MemoryStream();
    ValueWebSocketReceiveResult result;
    do
    {
        result = await socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
        if (result.MessageType == WebSocketMessageType.Close) throw new IOException("closed");
        stream.Write(buffer, 0, result.Count);
    } while (!result.EndOfMessage);
    return JsonDocument.Parse(stream.ToArray());
}

sealed class TestHost : IModuleHost
{
    private readonly Channel<Signal> _signals = Channel.CreateUnbounded<Signal>();
    public Func<Signal, Task>? OnSignal { get; set; }
    public async ValueTask PublishAsync(Signal signal, CancellationToken cancellationToken = default)
    {
        _signals.Writer.TryWrite(signal);
        if (OnSignal is { } callback) await callback(signal);
    }
    public void Trace(Diagnostic diagnostic) { }
    public async Task<Signal> Next(Func<Signal, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await _signals.Reader.WaitToReadAsync(timeout.Token))
            while (_signals.Reader.TryRead(out var signal)) if (predicate(signal)) return signal;
        throw new IOException("Signal missing");
    }
}
