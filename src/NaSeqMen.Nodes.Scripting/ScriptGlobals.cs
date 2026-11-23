using System.Text.Json;
using NaSeqMen.Core.Messages;
using NaSeqMen.Core.Nodes;
using NaSeqMen.Core.Telemetry;

namespace NaSeqMen.Nodes.Scripting;

/// <summary>
/// Ergonomic global surface exposed to user C# scripts executed by <see cref="CSharpScriptNode"/>.
/// </summary>
public sealed class ScriptGlobals
{
    private readonly NodeContext _context;

    public ScriptGlobals(FlowMessage message, Dictionary<string, object?> state, NodeContext context)
    {
        msg = message;
        this.state = state;
        _context = context;
    }

    internal bool IsDropped { get; private set; }

    public FlowMessage msg { get; }

    public object? payload
    {
        get => msg.Payload;
        set => msg.Payload = value;
    }

    public string text => msg.PayloadAsString();

    public string? topic
    {
        get => msg.Topic;
        set => msg.Topic = value;
    }

    public Dictionary<string, object?> props => msg.Properties;

    public Dictionary<string, object?> state { get; }

    public bool? suppress
    {
        get
        {
            if (!msg.Properties.TryGetValue("suppress", out var raw) || raw is null)
                return null;

            return raw switch
            {
                bool b => b,
                JsonElement { ValueKind: JsonValueKind.True } => true,
                JsonElement { ValueKind: JsonValueKind.False } => false,
                string s when bool.TryParse(s, out var parsed) => parsed,
                _ => null,
            };
        }
        set
        {
            if (value.HasValue)
                msg.Properties["suppress"] = value.Value;
            else
                msg.Properties.Remove("suppress");
        }
    }

    public void Suppress(bool value = true)
    {
        msg.Properties["suppress"] = value;
    }

    public void SendKeys(string sequence)
    {
        msg.Properties["sendKeys"] = sequence;
    }

    public void SendMouse(string action)
    {
        msg.Properties["sendMouse"] = action;
    }

    public void Drop()
    {
        IsDropped = true;
    }

    public void Log(string message)
    {
        var entry = new DebugEntry
        {
            FlowId = _context.FlowId,
            NodeId = _context.NodeId,
            NodeTitle = string.IsNullOrWhiteSpace(_context.Title) ? CSharpScriptNode.TypeDescriptor.DisplayName : _context.Title,
            Label = "script",
            SourceNodeId = msg.SourceNodeId,
            SourcePortId = msg.SourcePortId,
            MessageId = msg.Id,
            Topic = msg.Topic,
            Payload = message,
            FullBasket = false,
        };

        _context.PublishDebug(entry);
    }

    public FlowMessage Clone(object? newPayload = null)
        => newPayload is null ? msg.Clone() : msg.WithPayload(newPayload);
}
