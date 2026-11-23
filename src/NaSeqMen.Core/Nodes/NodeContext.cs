using System.Text.Json;
using NaSeqMen.Core.Messages;
using NaSeqMen.Core.Telemetry;

namespace NaSeqMen.Core.Nodes;

/// <summary>
/// Runtime services injected into a node instance by the FlowEngine.
/// </summary>
public sealed class NodeContext
{
    private readonly Func<string, string, FlowMessage, CancellationToken, Task> _emitFunc;
    private readonly Action<DebugEntry> _debugAction;
    private readonly Action<string, string?> _statusAction;
    private readonly Action<string, object> _widgetAction;

    public NodeContext(
        string nodeId,
        string flowId,
        string title,
        IReadOnlyDictionary<string, JsonElement> parameters,
        Func<string, string, FlowMessage, CancellationToken, Task> emitFunc,
        Action<DebugEntry> debugAction,
        Action<string, string?> statusAction,
        Action<string, object>? widgetAction = null)
    {
        NodeId = nodeId;
        FlowId = flowId;
        Title = title;
        Parameters = parameters;
        _emitFunc = emitFunc;
        _debugAction = debugAction;
        _statusAction = statusAction;
        _widgetAction = widgetAction ?? ((_, _) => { });
    }

    public string NodeId { get; }
    public string FlowId { get; }
    public string Title { get; }
    public IReadOnlyDictionary<string, JsonElement> Parameters { get; }

    public Task EmitAsync(string outputPortId, FlowMessage message, CancellationToken cancellationToken = default)
        => _emitFunc(NodeId, outputPortId, message, cancellationToken);

    public void PublishDebug(DebugEntry entry) => _debugAction(entry);

    public void ReportStatus(string? text) => _statusAction(NodeId, text);

    public void PublishWidgetState(object state) => _widgetAction(NodeId, state);

    public string GetString(string key, string defaultValue = "")
    {
        if (!Parameters.TryGetValue(key, out var elem))
            return defaultValue;

        return elem.ValueKind switch
        {
            JsonValueKind.String => elem.GetString() ?? defaultValue,
            JsonValueKind.Null or JsonValueKind.Undefined => defaultValue,
            _ => elem.GetRawText(),
        };
    }

    public bool GetBool(string key, bool defaultValue = false)
    {
        if (!Parameters.TryGetValue(key, out var elem))
            return defaultValue;

        return elem.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(elem.GetString(), out var parsed) => parsed,
            _ => defaultValue,
        };
    }

    public double GetNumber(string key, double defaultValue = 0)
    {
        if (!Parameters.TryGetValue(key, out var elem))
            return defaultValue;

        if (elem.ValueKind == JsonValueKind.Number && elem.TryGetDouble(out var n))
            return n;

        if (elem.ValueKind == JsonValueKind.String && double.TryParse(elem.GetString(), out var parsed))
            return parsed;

        return defaultValue;
    }
}
