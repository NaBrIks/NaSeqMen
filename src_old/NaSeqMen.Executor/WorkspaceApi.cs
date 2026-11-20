// SPDX-License-Identifier: MIT
using System.Text.Json;
using NaSeqMen.Core;
using NaSeqMen.Runtime.ProjectGraph;

internal static class WorkspaceApi
{
    internal static void MapWorkspace(this WebApplication app, ProjectRuntime runtime)
    {
        var changes = new SemaphoreSlim(1);
        app.MapGet("/api/workspace", runtime.GetWorkspace);
        app.MapGet("/api/workspace/state", runtime.GetState);
        app.MapGet("/api/workspace/catalog", () => runtime.Types);
        app.MapGet("/api/workspace/adapters", () => runtime.AdapterTypes);
        app.MapGet("/api/workspace/trace", runtime.GetTrace);
        app.MapGet("/api/workspace/activity", runtime.GetActivity);
        app.MapDelete("/api/workspace/trace", () => { runtime.ClearTrace(); return Results.Ok(new { }); });
        app.MapPost("/api/workspace/watch", (WatchRequest watch) =>
        {
            runtime.Watch(watch.Project, watch.Page, watch.Node, watch.Port ?? "", watch.Enabled);
            return Results.Ok(new { });
        });
        app.MapPost("/api/workspace/inject", (InjectRequest input) =>
            runtime.Inject(input.Project, input.Page, input.Node, input.Data)
                ? Results.Ok(new { }) : Results.BadRequest(new { message = "Input is unavailable or the queue is full." }));
        app.MapPut("/api/workspace", async (Workspace workspace, CancellationToken ct) =>
            await ChangeAsync(() => runtime.ApplyAsync(workspace, ct: ct), ct));
        app.MapPost("/api/workspace/play", async (PlayRequest request, CancellationToken ct) =>
        {
            await changes.WaitAsync(ct);
            try
            {
                var project = request.Project with { DryRun = false };
                var workspace = runtime.GetWorkspace();
                var projects = workspace.Projects.Any(p => p.Id == project.Id)
                    ? workspace.Projects.Select(p => p.Id == project.Id ? project : p).ToArray()
                    : [.. workspace.Projects, project];
                await runtime.ApplyAsync(workspace with { Projects = projects }, ct: ct);
                return Results.Ok(new { project, state = runtime.GetState() });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { return Results.BadRequest(new { code = "workspace.playFailed", message = ex.Message, state = runtime.GetState() }); }
            finally { changes.Release(); }
        });
        app.MapPost("/api/workspace/mode", async (ModeRequest mode, CancellationToken ct) =>
            await ChangeAsync(async () =>
            {
                var workspace = runtime.GetWorkspace();
                if (!workspace.Projects.Any(p => p.Id == mode.Project)) throw new InvalidDataException("Project not found.");
                await runtime.ApplyAsync(workspace with
                {
                    Projects = workspace.Projects.Select(p => p.Id == mode.Project ? p with { DryRun = mode.DryRun } : p).ToArray()
                }, ct: ct);
            }, ct));
        app.MapPost("/api/workspace/validate", async (Workspace workspace) =>
            await RunAsync(() => { runtime.Validate(workspace); return Task.CompletedTask; }));

        async Task<IResult> ChangeAsync(Func<Task> action, CancellationToken ct)
        {
            await changes.WaitAsync(ct);
            try { return await RunAsync(action); }
            finally { changes.Release(); }
        }
    }

    private static async Task<IResult> RunAsync(Func<Task> operation)
    {
        try { await operation(); return Results.Ok(new { }); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return Results.BadRequest(new { message = ex.Message }); }
    }

    private sealed record WatchRequest(string Project, string Page, string Node, string? Port, bool Enabled);
    private sealed record InjectRequest(string Project, string Page, string Node, JsonElement Data);
    private sealed record PlayRequest(WorkflowProject Project);
    private sealed record ModeRequest(string Project, bool DryRun);
}
