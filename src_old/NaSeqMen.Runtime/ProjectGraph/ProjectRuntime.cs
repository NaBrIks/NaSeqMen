// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Threading.Channels;
using NaSeqMen.Core;

namespace NaSeqMen.Runtime.ProjectGraph;

public sealed partial class ProjectRuntime : IAsyncDisposable
{
    private sealed class Revision(Workspace workspace, PreparedProject[] projects)
    {
        internal Workspace Workspace { get; } = workspace;
        internal PreparedProject[] Projects { get; } = projects;
        internal System.Collections.Concurrent.ConcurrentDictionary<(string, string), IAdapterSession> Sessions { get; } = new();
        internal bool Active;
        internal long Number;
        internal System.Collections.Concurrent.ConcurrentDictionary<string, string> Errors { get; } = new();
    }

    private readonly string _path;
    private readonly Dictionary<string, IGraphAdapter> _adapters;
    private readonly Dictionary<string, NodeType> _types;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Channel<Func<ValueTask>> _queue = Channel.CreateBounded<Func<ValueTask>>(128);
    private readonly Queue<TraceEntry> _trace = new();
    private readonly Queue<Activity> _activity = new();
    private long _activityId;
    public sealed record Activity(long Id, string Project, string Page, string Node, Wire? Wire);
    private readonly Task _worker;
    private volatile Revision _current = new(new([]), []);
    private readonly CancellationTokenSource _stop = new();
    private volatile Dictionary<string, Revision> _projectRevisions = new();
    private long _revisionNumber;

    public ProjectRuntime(string path, IEnumerable<IGraphAdapter> adapters)
    {
        _path = path;
        _adapters = adapters.ToDictionary(a => a.Id);
        _types = GraphNodes.Types.Concat(_adapters.Values.SelectMany(a => a.Nodes)).ToDictionary(n => n.Id);
        _worker = Task.Run(ProcessAsync);
    }

    public IReadOnlyCollection<NodeType> Types => _types.Values;
    public AdapterType[] AdapterTypes => _adapters.Values.Select(adapter => adapter.Describe()).ToArray();
    public Workspace GetWorkspace() => _current.Workspace;
    public TraceEntry[] GetTrace() { lock (_trace) return [.. _trace]; }
    public void ClearTrace() { lock (_trace) _trace.Clear(); }
    public Activity[] GetActivity() { lock (_activity) return [.. _activity]; }
    public void Validate(Workspace workspace) => GraphPreparation.Prepare(workspace, _types, _adapters);

    public object GetState() => new
    {
        revision = Interlocked.Read(ref _revisionNumber),
        projects = _projectRevisions.Select(pair => new
        {
            id = pair.Key, revision = pair.Value.Number,
            dryRun = pair.Value.Workspace.Projects[0].DryRun, active = pair.Value.Active,
            connections = pair.Value.Workspace.Projects[0].Connections.Select(connection => new
            {
                id = connection.Id, enabled = connection.Enabled,
                state = !connection.Enabled ? "disabled" : pair.Value.Errors.ContainsKey(connection.Id) ? "error" :
                    pair.Value.Sessions.ContainsKey((pair.Key, connection.Id)) ?
                        pair.Value.Workspace.Projects[0].DryRun ? "observing" : "ready" : "unavailable",
                error = pair.Value.Errors.GetValueOrDefault(connection.Id)
            })
        })
    };

    public async Task ApplyAsync(Workspace workspace, bool persist = true, CancellationToken ct = default)
    {
        var prepared = GraphPreparation.Prepare(workspace, _types, _adapters);
        await _gate.WaitAsync(ct);
        try
        {
            var previous = _projectRevisions;
            var next = new Dictionary<string, Revision>();
            var changed = new List<Revision>();
            foreach (var project in prepared)
            {
                if (previous.TryGetValue(project.Definition.Id, out var old) && old.Active && old.Errors.IsEmpty &&
                    JsonElement.DeepEquals(GraphJson.Value(old.Workspace.Projects[0]), GraphJson.Value(project.Definition)))
                    next.Add(project.Definition.Id, old);
                else
                {
                    var revision = new Revision(new([project.Definition], workspace.Version), [project]);
                    next.Add(project.Definition.Id, revision);
                    changed.Add(revision);
                }
            }
            var retired = previous.Where(pair => !next.TryGetValue(pair.Key, out var replacement) ||
                !ReferenceEquals(pair.Value, replacement)).Select(pair => pair.Value).ToArray();
            foreach (var old in retired) await CloseAsync(old);
            try
            {
                foreach (var revision in changed) await OpenAsync(revision, ct);
                if (persist)
                {
                    var temp = _path + ".new";
                    try
                    {
                        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(workspace, GraphJson.Options), ct);
                        File.Move(temp, _path, true);
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
                var number = Interlocked.Increment(ref _revisionNumber);
                foreach (var revision in changed) { revision.Number = number; Volatile.Write(ref revision.Active, true); }
                _projectRevisions = next;
                _current = new Revision(workspace, prepared);
            }
            catch
            {
                foreach (var revision in changed) await CloseAsync(revision);
                foreach (var old in retired)
                    try { await OpenAsync(old, CancellationToken.None); Volatile.Write(ref old.Active, true); }
                    catch (Exception error) { Report(old.Workspace.Projects[0].Id, "", error); }
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    private async Task OpenAsync(Revision revision, CancellationToken ct)
    {
        foreach (var project in revision.Projects)
        foreach (var connection in project.Definition.Connections.Where(c => c.Enabled))
        {
            var host = new AdapterHost(
                input => PublishAsync(revision, project, connection.Id, input),
                input => Decide(revision, project, connection.Id, input),
                error => { revision.Errors[connection.Id] = error.Message; Report(project.Definition.Id, connection.Id, error); });
            try
            {
                revision.Errors.TryRemove(connection.Id, out _);
                var adapter = _adapters[connection.Adapter];
                var session = project.Definition.DryRun
                    ? await adapter.OpenTestAsync(connection, host, ct)
                    : await adapter.OpenAsync(connection, host, ct);
                if (session is not null) revision.Sessions[(project.Definition.Id, connection.Id)] = session;
            }
            catch (Exception error) when (!ct.IsCancellationRequested &&
                project.Definition.DryRun)
            {
                host.Error(error);
            }
        }
    }

    private async Task CloseAsync(Revision revision)
    {
        Volatile.Write(ref revision.Active, false);
        foreach (var session in revision.Sessions.Values)
            try { await session.DisposeAsync(); }
            catch (Exception error) { Report("", "", error); }
        revision.Sessions.Clear();
    }

    public bool Inject(string projectId, string pageId, string nodeId, JsonElement data)
    {
        if (!_projectRevisions.TryGetValue(projectId, out var revision)) return false;
        var project = revision.Projects.SingleOrDefault(p => p.Definition.Id == projectId);
        if (project is null || !project.Pages.TryGetValue(pageId, out var page) ||
            !page.Nodes.TryGetValue(nodeId, out var node)) return false;
        var packet = Packet.Create(data.OrEmpty());
        if (node.Type == "inject")
            return _queue.Writer.TryWrite(() => DispatchAsync(revision, project, page, node, "out", packet));
        return _types[node.Type].ManualInput is { } port &&
            _queue.Writer.TryWrite(() => RunAsync(revision, project, page, node, port, packet, null));
    }

    private async Task ProcessAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync())
            try { await item(); }
            catch (Exception error) { Report("", "", error); }
    }

    private void Report(string project, string node, Exception error) =>
        Trace(new(DateTimeOffset.UtcNow, project, "", node, "", "", "error", GraphJson.Empty, error.Message));

    private void Trace(TraceEntry entry)
    {
        lock (_trace)
        {
            if (_trace.Count == 400) _trace.Dequeue();
            _trace.Enqueue(entry);
        }
    }

    private void Pulse(PreparedProject project, PreparedPage page, string node, Wire? wire = null)
    {
        lock (_activity)
        {
            if (_activity.Count == 96) _activity.Dequeue();
            _activity.Enqueue(new(++_activityId, project.Definition.Id, page.Definition.Id, node, wire));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        _stop.Cancel();
        foreach (var revision in _projectRevisions.Values) await CloseAsync(revision);
        await _worker;
        _gate.Dispose();
        _stop.Dispose();
    }
}
