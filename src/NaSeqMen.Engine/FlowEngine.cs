using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using NaSeqMen.Core.Graph;
using NaSeqMen.Core.Messages;
using NaSeqMen.Core.Nodes;
using NaSeqMen.Core.Telemetry;

namespace NaSeqMen.Engine;

public sealed class FlowEngine : IAsyncDisposable
{
    private const int MaxHopCount = 64;
    private const int MaxDebugHistory = 200;

    private readonly NodeRegistry _registry;
    private readonly SemaphoreSlim _deployLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, Channel<EngineStreamEvent>> _subscribers = new();
    private readonly ConcurrentQueue<DebugEntry> _debugHistory = new();
    private readonly ConcurrentDictionary<string, object> _widgetStates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string?> _nodeStatuses = new(StringComparer.Ordinal);

    private WorkspaceDocument _workspace = new();
    private Dictionary<string, RunningNode> _runningNodes = new(StringComparer.Ordinal);
    private Dictionary<(string NodeId, string PortId), List<EdgeBinding>> _routingTable = new();
    private CancellationTokenSource? _runCts;
    private volatile bool _isPaused;

    private sealed record RunningNode(string FlowId, NodeInstance Instance, INode Node);
    private sealed record EdgeBinding(string FlowId, EdgeConnection Edge);

    public FlowEngine(NodeRegistry registry)
    {
        _registry = registry;
    }

    public WorkspaceDocument CurrentWorkspace => _workspace;
    public bool IsPaused => _isPaused;
    public event Action<bool>? PausedChanged;

    public IReadOnlyList<DebugEntry> GetDebugHistory() => _debugHistory.ToArray();

    public IReadOnlyDictionary<string, object> GetWidgetStates()
        => new Dictionary<string, object>(_widgetStates, StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string?> GetNodeStatuses()
        => new Dictionary<string, string?>(_nodeStatuses, StringComparer.Ordinal);

    public void ClearDebugHistory()
    {
        while (_debugHistory.TryDequeue(out _)) { }
        Broadcast(new EngineStreamEvent { Kind = "debug.cleared" });
    }

    public (Guid Id, ChannelReader<EngineStreamEvent> Reader) Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<EngineStreamEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _subscribers[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var ch))
        {
            ch.Writer.TryComplete();
        }
    }

    public async Task DeployAsync(WorkspaceDocument workspace, CancellationToken cancellationToken = default)
    {
        await _deployLock.WaitAsync(cancellationToken);
        try
        {
            await StopInternalAsync();

            _workspace = workspace;
            _runCts = new CancellationTokenSource();
            var ct = _runCts.Token;

            var nodes = new Dictionary<string, RunningNode>(StringComparer.Ordinal);
            var routes = new Dictionary<(string NodeId, string PortId), List<EdgeBinding>>();

            foreach (var flow in workspace.Flows)
            {
                if (!flow.Enabled)
                    continue;

                foreach (var inst in flow.Nodes)
                {
                    if (!inst.Enabled)
                        continue;

                    var node = _registry.CreateNode(inst.TypeId);
                    if (node is null)
                        continue;

                    var ctx = new NodeContext(
                        nodeId: inst.Id,
                        flowId: flow.Id,
                        title: string.IsNullOrWhiteSpace(inst.Title) ? node.Descriptor.DisplayName : inst.Title,
                        parameters: inst.Parameters,
                        emitFunc: RouteMessageAsync,
                        debugAction: PublishDebug,
                        statusAction: ReportNodeStatus,
                        widgetAction: PublishWidgetState);

                    await node.InitializeAsync(ctx, ct);
                    nodes[inst.Id] = new RunningNode(flow.Id, inst, node);
                }

                foreach (var edge in flow.Edges)
                {
                    if (!nodes.ContainsKey(edge.FromNodeId) || !nodes.ContainsKey(edge.ToNodeId))
                        continue;

                    var key = (edge.FromNodeId, edge.FromPortId);
                    if (!routes.TryGetValue(key, out var list))
                    {
                        list = [];
                        routes[key] = list;
                    }
                    list.Add(new EdgeBinding(flow.Id, edge));
                }
            }

            _runningNodes = nodes;
            _routingTable = routes;
            _isPaused = false;

            foreach (var rn in _runningNodes.Values)
            {
                await rn.Node.StartAsync(ct);
            }

            PausedChanged?.Invoke(false);

            Broadcast(new EngineStreamEvent
            {
                Kind = "engine.deployed",
                Data = new
                {
                    nodeCount = _runningNodes.Count,
                    edgeCount = _routingTable.Values.Sum(l => l.Count),
                    paused = _isPaused,
                    timestamp = DateTimeOffset.UtcNow,
                },
            });
        }
        finally
        {
            _deployLock.Release();
        }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await _deployLock.WaitAsync(cancellationToken);
        try
        {
            if (_isPaused)
                return;

            await StopInternalAsync();
            _isPaused = true;
            PausedChanged?.Invoke(true);
            Broadcast(new EngineStreamEvent { Kind = "engine.state", Data = new { paused = true } });
        }
        finally
        {
            _deployLock.Release();
        }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        if (!_isPaused)
            return;

        await DeployAsync(_workspace, cancellationToken);
    }

    public async Task<bool> TriggerNodeAsync(string nodeId, object? payload = null, CancellationToken cancellationToken = default)
    {
        if (!_runningNodes.TryGetValue(nodeId, out var running))
            return false;

        FlowMessage? msg = payload is null
            ? null
            : new FlowMessage
            {
                Topic = "manual",
                Payload = payload,
                OriginNodeId = nodeId,
            };

        await running.Node.OnManualTriggerAsync(msg, cancellationToken);
        return true;
    }

    public async Task<bool> InteractNodeAsync(string nodeId, string action, JsonElement? data = null, CancellationToken cancellationToken = default)
    {
        if (!_runningNodes.TryGetValue(nodeId, out var running))
            return false;

        await running.Node.OnInteractAsync(action, data, cancellationToken);
        return true;
    }

    private async Task RouteMessageAsync(string fromNodeId, string fromPortId, FlowMessage message, CancellationToken cancellationToken)
    {
        if (message.HopCount >= MaxHopCount)
            return;

        Broadcast(new EngineStreamEvent
        {
            Kind = "node.activity",
            Data = new { nodeId = fromNodeId },
        });

        if (!_routingTable.TryGetValue((fromNodeId, fromPortId), out var bindings) || bindings.Count == 0)
            return;

        var ct = _runCts?.Token ?? cancellationToken;
        var preview = message.PayloadAsString();
        if (preview.Length > 32)
            preview = preview[..32] + "…";

        var tasks = new List<Task>(bindings.Count);
        foreach (var binding in bindings)
        {
            if (!_runningNodes.TryGetValue(binding.Edge.ToNodeId, out var target))
                continue;

            var delivered = message.Clone();
            delivered.SourceNodeId = fromNodeId;
            delivered.SourcePortId = fromPortId;
            delivered.HopCount = message.HopCount + 1;

            Broadcast(new EngineStreamEvent
            {
                Kind = "wire.activity",
                Data = new WireActivityEvent
                {
                    FlowId = binding.FlowId,
                    EdgeId = binding.Edge.Id,
                    FromNodeId = binding.Edge.FromNodeId,
                    ToNodeId = binding.Edge.ToNodeId,
                    Preview = preview,
                },
            });

            tasks.Add(InvokeTargetSafeAsync(target.Node, binding.Edge.ToPortId, delivered, ct));
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks);
        }
    }

    private async Task InvokeTargetSafeAsync(INode target, string inputPortId, FlowMessage message, CancellationToken ct)
    {
        try
        {
            await target.OnInputAsync(inputPortId, message, ct);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown/redeploy
        }
        catch (Exception ex)
        {
            PublishDebug(new DebugEntry
            {
                FlowId = string.Empty,
                NodeId = message.SourceNodeId ?? string.Empty,
                NodeTitle = target.Descriptor.DisplayName,
                Label = "ERROR",
                Topic = "engine.error",
                Payload = ex.Message,
                FullBasket = false,
            });
        }
    }

    private void PublishDebug(DebugEntry entry)
    {
        _debugHistory.Enqueue(entry);
        while (_debugHistory.Count > MaxDebugHistory)
        {
            _debugHistory.TryDequeue(out _);
        }

        Broadcast(new EngineStreamEvent { Kind = "debug.entry", Data = entry });
    }

    private void ReportNodeStatus(string nodeId, string? statusText)
    {
        _nodeStatuses[nodeId] = statusText;
        Broadcast(new EngineStreamEvent
        {
            Kind = "node.status",
            Data = new NodeStatusEvent { NodeId = nodeId, StatusText = statusText },
        });
    }

    private void PublishWidgetState(string nodeId, object state)
    {
        _widgetStates[nodeId] = state;
        Broadcast(new EngineStreamEvent
        {
            Kind = "node.widget",
            Data = new { nodeId, state },
        });
    }

    private void Broadcast(EngineStreamEvent evt)
    {
        foreach (var ch in _subscribers.Values)
        {
            ch.Writer.TryWrite(evt);
        }
    }

    private async Task StopInternalAsync()
    {
        if (_runCts is not null)
        {
            await _runCts.CancelAsync();
            _runCts.Dispose();
            _runCts = null;
        }

        foreach (var rn in _runningNodes.Values)
        {
            try { await rn.Node.StopAsync(CancellationToken.None); } catch { }
            try { await rn.Node.DisposeAsync(); } catch { }
        }

        _runningNodes = new(StringComparer.Ordinal);
        _routingTable = new();
        _widgetStates.Clear();
        _nodeStatuses.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await _deployLock.WaitAsync();
        try
        {
            await StopInternalAsync();
            foreach (var ch in _subscribers.Values)
            {
                ch.Writer.TryComplete();
            }
            _subscribers.Clear();
        }
        finally
        {
            _deployLock.Release();
            _deployLock.Dispose();
        }
    }
}
