namespace NaSeqMen.Core.Nodes;

/// <summary>
/// Self-describing metadata for a node type, shared between Engine and Editor UI.
/// </summary>
public sealed record NodeTypeDescriptor
{
    public required string TypeId { get; init; }
    public required string DisplayName { get; init; }
    public required string Category { get; init; }
    public string Color { get; init; } = "#4b5563";
    public string? Description { get; init; }
    public bool SupportsManualTrigger { get; init; }
    public string? WidgetKind { get; init; }
    public IReadOnlyList<PortDefinition> Inputs { get; init; } = Array.Empty<PortDefinition>();
    public IReadOnlyList<PortDefinition> Outputs { get; init; } = Array.Empty<PortDefinition>();
    public IReadOnlyList<ParameterDefinition> Parameters { get; init; } = Array.Empty<ParameterDefinition>();
}
