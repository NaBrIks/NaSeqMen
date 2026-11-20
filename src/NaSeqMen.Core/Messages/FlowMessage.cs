using System.Text.Json;

namespace NaSeqMen.Core.Messages;

/// <summary>
/// Event and data basket traveling along wires between node ports (Node-RED msg paradigm).
/// </summary>
public sealed class FlowMessage
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public string? Topic { get; set; }

    public object? Payload { get; set; }

    public Dictionary<string, object?> Properties { get; init; } = new(StringComparer.Ordinal);

    public string? OriginNodeId { get; set; }

    public string? SourceNodeId { get; set; }

    public string? SourcePortId { get; set; }

    public int HopCount { get; set; }

    public FlowMessage Clone(object? newPayload = null, bool replacePayload = false)
    {
        var copy = new FlowMessage
        {
            Id = Id,
            Timestamp = Timestamp,
            Topic = Topic,
            Payload = replacePayload ? newPayload : Payload,
            OriginNodeId = OriginNodeId,
            SourceNodeId = SourceNodeId,
            SourcePortId = SourcePortId,
            HopCount = HopCount,
            Properties = new Dictionary<string, object?>(Properties, StringComparer.Ordinal),
        };
        return copy;
    }

    public FlowMessage WithPayload(object? payload) => Clone(payload, replacePayload: true);

    public string PayloadAsString()
    {
        return Payload switch
        {
            null => string.Empty,
            string s => s,
            JsonElement je => je.ValueKind == JsonValueKind.String ? je.GetString() ?? string.Empty : je.GetRawText(),
            _ => Payload.ToString() ?? string.Empty,
        };
    }
}
