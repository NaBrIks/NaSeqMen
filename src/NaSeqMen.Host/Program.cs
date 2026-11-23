using System.Text.Json;
using System.Text.Json.Serialization;
using NaSeqMen.Core.Graph;
using NaSeqMen.Engine;
using NaSeqMen.Host;
using NaSeqMen.Nodes.Basic;
using NaSeqMen.Nodes.Controls;
using NaSeqMen.Nodes.Interception;
using NaSeqMen.Nodes.Obs;
using NaSeqMen.Nodes.Scripting;
using NaSeqMen.Nodes.Windows;

var builder = WebApplication.CreateBuilder(args);

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
};

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

var registry = new NodeRegistry();
registry.RegisterPlugin(new BasicNodesPlugin());
registry.RegisterPlugin(new WindowsNodesPlugin());
registry.RegisterPlugin(new InterceptionNodesPlugin());
registry.RegisterPlugin(new ObsNodesPlugin());
registry.RegisterPlugin(new ControlsNodesPlugin());
registry.RegisterPlugin(new ScriptingNodesPlugin());

var workspacePath = Path.Combine(AppContext.BaseDirectory, "workspace.json");
var repoRootLocal = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "local"));
if (Directory.Exists(repoRootLocal))
{
    workspacePath = Path.Combine(repoRootLocal, "workspace-v2.json");
}

var store = new WorkspaceStore(workspacePath);
var engine = new FlowEngine(registry);

builder.Services.AddSingleton(registry);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(engine);

var port = Environment.GetEnvironmentVariable("NASEQMEN_PORT") ?? "17980";
var editorUrl = $"http://127.0.0.1:{port}";
builder.WebHost.UseUrls(editorUrl);

var app = builder.Build();

var initialWorkspace = await store.LoadOrCreateDefaultAsync();
await engine.DeployAsync(initialWorkspace);

var noTray = args.Any(a => string.Equals(a, "--no-tray", StringComparison.OrdinalIgnoreCase));
TrayHost? tray = null;
if (!noTray)
{
    tray = new TrayHost(editorUrl, engine, () => app.Lifetime.StopApplication());
    tray.Start();
}

app.UseDefaultFiles();
app.UseStaticFiles();

// --- REST API ---

app.MapGet("/api/catalog", (NodeRegistry reg) => Results.Ok(reg.GetDescriptors()));

app.MapGet("/api/workspace", (FlowEngine eng) => Results.Ok(eng.CurrentWorkspace));

app.MapGet("/api/state", (FlowEngine eng) => Results.Ok(new
{
    paused = eng.IsPaused,
    widgets = eng.GetWidgetStates(),
    statuses = eng.GetNodeStatuses(),
}));

app.MapGet("/api/engine/status", (FlowEngine eng) => Results.Ok(new { paused = eng.IsPaused }));

app.MapPost("/api/engine/pause", async (FlowEngine eng, CancellationToken ct) =>
{
    await eng.PauseAsync(ct);
    return Results.Ok(new { paused = true });
});

app.MapPost("/api/engine/resume", async (FlowEngine eng, CancellationToken ct) =>
{
    await eng.ResumeAsync(ct);
    return Results.Ok(new { paused = false });
});

app.MapPost("/api/workspace/deploy", async (WorkspaceDocument doc, WorkspaceStore st, FlowEngine eng, CancellationToken ct) =>
{
    await st.SaveAsync(doc, ct);
    await eng.DeployAsync(doc, ct);
    return Results.Ok(new { ok = true, timestamp = DateTimeOffset.UtcNow });
});

app.MapPost("/api/workspace/reset", async (WorkspaceStore st, FlowEngine eng, CancellationToken ct) =>
{
    var starter = WorkspaceStore.CreateStarterWorkspace();
    await st.SaveAsync(starter, ct);
    await eng.DeployAsync(starter, ct);
    return Results.Ok(starter);
});

app.MapPost("/api/nodes/{nodeId}/trigger", async (string nodeId, TriggerRequest? body, FlowEngine eng, CancellationToken ct) =>
{
    var ok = await eng.TriggerNodeAsync(nodeId, body?.Payload, ct);
    return ok ? Results.Ok(new { ok = true }) : Results.NotFound(new { ok = false, error = "Node not running" });
});

app.MapPost("/api/nodes/{nodeId}/interact", async (string nodeId, InteractRequest body, FlowEngine eng, CancellationToken ct) =>
{
    var ok = await eng.InteractNodeAsync(nodeId, body.Action, body.Data, ct);
    return ok ? Results.Ok(new { ok = true }) : Results.NotFound(new { ok = false, error = "Node not running" });
});

app.MapGet("/api/interception/devices", (string? dllPath) =>
{
    var snapshot = InterceptionDriverService.Instance.GetSnapshot(
        string.IsNullOrWhiteSpace(dllPath) ? null : dllPath,
        refreshDevices: true);
    return Results.Ok(snapshot);
});

app.MapPost("/api/interception/panic-release", () =>
{
    InterceptionDriverService.Instance.EmergencyReleaseAllFilters();
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/debug", (FlowEngine eng) => Results.Ok(eng.GetDebugHistory()));

app.MapDelete("/api/debug", (FlowEngine eng) =>
{
    eng.ClearDebugHistory();
    return Results.Ok(new { ok = true });
});

// --- Server-Sent Events stream for live wire activity, widgets, debug entries & status ---

app.MapGet("/api/events", async (HttpContext ctx, FlowEngine eng, CancellationToken ct) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Connection = "keep-alive";

    var (subId, reader) = eng.Subscribe();
    try
    {
        var initJson = JsonSerializer.Serialize(new { kind = "engine.state", data = new { paused = eng.IsPaused } }, jsonOptions);
        await ctx.Response.WriteAsync($"data: {initJson}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);

        await foreach (var evt in reader.ReadAllAsync(ct))
        {
            var json = JsonSerializer.Serialize(evt, jsonOptions);
            await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException)
    {
        // Client disconnected
    }
    finally
    {
        eng.Unsubscribe(subId);
    }
});

app.Lifetime.ApplicationStopping.Register(() =>
{
    tray?.Dispose();
    engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
});

await app.RunAsync();

public sealed record TriggerRequest(object? Payload);
public sealed record InteractRequest(string Action, JsonElement? Data = null);
