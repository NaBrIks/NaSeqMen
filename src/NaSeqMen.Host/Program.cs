using System.Text.Json;
using System.Text.Json.Serialization;
using NaSeqMen.Core.Graph;
using NaSeqMen.Engine;
using NaSeqMen.Host;
using NaSeqMen.Nodes.Basic;
using NaSeqMen.Nodes.Controls;
using NaSeqMen.Nodes.Scripting;

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

// Built-in base modules
registry.RegisterPlugin(new BasicNodesPlugin(), source: "built-in");
registry.RegisterPlugin(new ControlsNodesPlugin(), source: "built-in");
registry.RegisterPlugin(new ScriptingNodesPlugin(), source: "built-in");

// Dynamically discovered external plugins from <AppDir>\plugins\
var pluginsDir = Environment.GetEnvironmentVariable("NASEQMEN_PLUGINS_DIR")
    ?? Path.Combine(AppContext.BaseDirectory, "plugins");
PluginLoader.LoadFromDirectory(pluginsDir, registry);

var workspacePath = Path.Combine(AppContext.BaseDirectory, "workspace.json");
var repoRootLocal = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "local"));
if (Directory.Exists(repoRootLocal))
{
    workspacePath = Path.Combine(repoRootLocal, "workspace-v2.json");
}

var store = new WorkspaceStore(workspacePath);
var engine = new FlowEngine(registry);
var devProjectsRoot = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, ".."));
var localization = new NodeLocalizationService(
    registry,
    AppContext.BaseDirectory,
    Directory.Exists(devProjectsRoot) ? devProjectsRoot : null);

builder.Services.AddSingleton(registry);
builder.Services.AddSingleton(localization);
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

app.MapGet("/api/catalog", (string? lang, NodeLocalizationService loc) => Results.Ok(loc.GetLocalizedCatalog(lang)));

app.MapGet("/api/languages", (NodeLocalizationService loc) => Results.Ok(loc.GetAvailableLanguages()));

app.MapGet("/api/docs/{typeId}", (string typeId, string? lang, NodeLocalizationService loc) =>
{
    var doc = loc.GetNodeDocumentation(typeId, lang);
    return doc is null ? Results.NotFound(new { error = $"Unknown node type '{typeId}'." }) : Results.Ok(doc);
});

app.MapGet("/api/plugins", (NodeRegistry reg) => Results.Ok(new
{
    pluginsDirectory = pluginsDir,
    plugins = reg.GetLoadedPlugins(),
}));

app.MapPost("/api/plugins/{pluginId}/{action}", async (string pluginId, string action, HttpRequest req, NodeRegistry reg, CancellationToken ct) =>
{
    JsonElement? body = null;
    if (req.ContentLength is > 0)
    {
        try
        {
            body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body, cancellationToken: ct);
        }
        catch
        {
            // Ignore empty/malformed optional body
        }
    }

    var (found, result) = await reg.InvokePluginActionAsync(pluginId, action, body, ct);
    if (!found)
        return Results.NotFound(new { ok = false, error = $"Plugin '{pluginId}' not loaded" });

    return Results.Ok(result ?? new { ok = true });
});

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
