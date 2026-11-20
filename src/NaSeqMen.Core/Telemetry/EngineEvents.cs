namespace NaSeqMen.Core.Telemetry;

public sealed record DebugEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..10];
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public required string FlowId { get; init; }
    public required string NodeId { get; init; }
    public required string NodeTitle { get; init; }
    public string? Label { get; init; }
    public string? SourceNodeId { get; init; }
    public string? SourcePortId { get; init; }
    public string? MessageId { get; init; }
    public string? Topic { get; init; }
    public object? Payload { get; init; }
    public Dictionary<string, object?>? Properties { get; init; }
    public bool FullBasket { get; init; } = true;
}

public sealed record WireActivityEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public required string FlowId { get; init; }
    public required string EdgeId { get; init; }
    public required string FromNodeId { get; init; }
    public required string ToNodeId { get; init; }
    public string? Preview { get; init; }
}

public sealed record NodeStatusEvent
{
    public required string NodeId { get; init; }
    public string? StatusText { get; init; }
}

public sealed record EngineStreamEvent
{
    public required string Kind { get; init; }
    public object? Data { get; init; }
}
