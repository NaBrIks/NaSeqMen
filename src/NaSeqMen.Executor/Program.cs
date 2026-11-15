// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NaSeqMen.Contracts;
using static NaSeqMen.Contracts.MessageCatalog;
using NaSeqMen.Runtime;

var root = Path.GetFullPath(Argument("--root") ?? Environment.CurrentDirectory);
var port = int.TryParse(Argument("--port") ?? "17963", out var selectedPort) ? selectedPort : 0;
if (port is < 1024 or > 65535 || port == 443) throw Error<ArgumentException>(M("common.invalidPort"));
var local = Path.Combine(root, "local");
Directory.CreateDirectory(local);
var tokenPath = Path.Combine(local, "api-token.txt");
if (!File.Exists(tokenPath)) await File.WriteAllTextAsync(tokenPath, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
var token = (await File.ReadAllTextAsync(tokenPath)).Trim();
if (token.Length < 32) throw Error<InvalidDataException>(M("common.invalidToken"));
var tokenBytes = Encoding.UTF8.GetBytes(token);
var moduleDirectory = Path.Combine(AppContext.BaseDirectory, "modules");
if (!Directory.Exists(moduleDirectory)) moduleDirectory = Path.Combine(root, "artifacts", "alpha", "modules");
var pluginListPath = Path.Combine(local, "plugins.json");
var pluginList = File.Exists(pluginListPath)
    ? JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(pluginListPath))
        ?? throw Error<InvalidDataException>(M("executor.pluginList"))
    : null;
var modules = ModuleLoader.Load(moduleDirectory, pluginList);
var configPath = Path.Combine(local, "config.json");
var initialPath = File.Exists(configPath) ? configPath : Path.Combine(root, "examples", "default.json");
var config = File.Exists(initialPath)
    ? JsonSerializer.Deserialize<Configuration>(await File.ReadAllTextAsync(initialPath), GraphRuntime.Json)
        ?? throw Error<InvalidDataException>(M("executor.configEmpty"))
    : new Configuration();
await using var runtime = new GraphRuntime(modules, configPath);
await runtime.StartAsync(config);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = root });
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 2 * 1024 * 1024);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
var app = builder.Build();
app.UseWebSockets();
app.Use(async (context, next) =>
{
    if (context.Request.Headers.TryGetValue("Origin", out var origins))
    {
        if (origins.Count != 1 || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin) ||
            origin.Scheme != "http" || origin.Host != "127.0.0.1" || origin.Port != port)
        { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
    }
    if (context.Request.Path != "/api/health")
    {
        var supplied = context.Request.Headers["X-NaSeqMen-Token"].ToString();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), tokenBytes))
        { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
    }
    await next(context);
});

app.MapGet("/api/health", () => new { name = "NaSeqMen.Executor", version = "0.1.0-alpha", dryRun = runtime.DryRun });
app.MapGet("/api/config", runtime.GetConfiguration);
app.MapGet("/api/localization", () => MessageCatalog.Catalog);
app.MapGet("/api/modules", () => runtime.Modules);
app.MapGet("/api/devices", async (CancellationToken cancellationToken) => await runtime.GetDevicesAsync(cancellationToken));
app.MapPost("/api/devices/capture", async (DiscoveryCaptureRequest request, CancellationToken cancellationToken) =>
{
    if (request.Owner == Guid.Empty || request.Interfaces is null || request.Interfaces.Length > 10 ||
        request.Interfaces.Any(path => string.IsNullOrWhiteSpace(path) || path.Length > 1024))
        return Results.BadRequest(ApiError(M("input.invalidCapture")));
    try { return Results.Ok(await runtime.SetDiscoveryAsync(request, cancellationToken)); }
    catch (Exception error) when (error is not OperationCanceledException)
    { return Results.BadRequest(ApiError(FromException(error))); }
});
app.MapPost("/api/config/validate", (Configuration configuration) => runtime.Validate(configuration));
app.MapPut("/api/config", async (Configuration configuration, CancellationToken cancellationToken) =>
{
    var result = await runtime.ApplyAsync(configuration, cancellationToken: cancellationToken);
    return result.Valid ? Results.Ok(result) : Results.BadRequest(result);
});
app.MapPost("/api/inject", (Signal signal) =>
{
    if (signal.Control is null || signal.Phase is null || signal.Value is null || signal.ValueType is null ||
        signal.Value.Length > 64 * 1024 || signal.Control.Length > 1024 || signal.Phase.Length > 32 ||
        signal.ValueType is not ("text" or "number" or "boolean"))
        return Results.BadRequest(ApiError(M("executor.invalidSignal")));
    // An API caller cannot masquerade as a physical device or reuse a trace identity.
    var accepted = runtime.TryPublish(signal with
    {
        Id = Guid.NewGuid().ToString("N"), Time = DateTimeOffset.UtcNow,
        Module = "test", ConnectionId = "", Source = "synthetic",
        Modifiers = null, IsRepeat = false, InputGeneration = 0, OriginalControl = null
    });
    return accepted ? Results.Ok(new { accepted = true }) : Results.Json(new { accepted = false }, statusCode: StatusCodes.Status429TooManyRequests);
});
app.Map("/api/diagnostics", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    using var subscription = runtime.Diagnostics.Subscribe();
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    var reader = Task.Run(async () =>
    {
        var buffer = new byte[256];
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var received = await socket.ReceiveAsync(buffer.AsMemory(), linked.Token);
                if (received.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        finally { linked.Cancel(); }
    });
    try
    {
        await foreach (var diagnostic in subscription.Reader.ReadAllAsync(linked.Token))
        {
            var data = JsonSerializer.SerializeToUtf8Bytes(diagnostic, GraphRuntime.Json);
            await socket.SendAsync(data.AsMemory(), WebSocketMessageType.Text, true, linked.Token);
        }
    }
    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
    finally
    {
        linked.Cancel();
        try { await reader; } catch (OperationCanceledException) { }
    }
});

using var tray = args.Contains("--no-tray", StringComparer.Ordinal) ? null : new TrayLifetime(root, app.Lifetime);
await app.RunAsync();

string? Argument(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

internal sealed class TrayLifetime : IDisposable
{
    private readonly Thread _thread;
    private System.Windows.Forms.ApplicationContext? _context;
    private readonly ManualResetEventSlim _ready = new();

    public TrayLifetime(string root, IHostApplicationLifetime lifetime)
    {
        _thread = new Thread(() =>
        {
            _context = new System.Windows.Forms.ApplicationContext();
            using var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add(M("executor.openEditor").Message, null, (_, _) =>
            {
                var path = Path.Combine(root, "artifacts", "alpha", "editor", "NaSeqMen.Editor.exe");
                if (File.Exists(path))
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, ArgumentList = { "--root", root } });
            });
            menu.Items.Add(M("executor.exit").Message, null, (_, _) => lifetime.StopApplication());
            using var icon = new System.Windows.Forms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application, Text = M("executor.tray").Message,
                Visible = true, ContextMenuStrip = menu
            };
            _ready.Set();
            System.Windows.Forms.Application.Run(_context);
        }) { IsBackground = true, Name = "NaSeqMen.Tray" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Dispose()
    {
        if (_ready.Wait(TimeSpan.FromSeconds(1))) _context?.ExitThread();
        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }
}
