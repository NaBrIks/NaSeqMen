namespace NaSeqMen.Core.Nodes;

public enum ParameterKind
{
    String,
    Number,
    Boolean,
    Select,
    Multiline,
    Key,
    KeyCombo,
    RuleList,
}

public sealed record ParameterOption(string Value, string Label);

public sealed record ParameterDefinition
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public ParameterKind Kind { get; init; } = ParameterKind.String;
    public object? DefaultValue { get; init; }
    public string? Description { get; init; }
    public string? Placeholder { get; init; }
    public bool Inline { get; init; }
    public bool Secret { get; init; }
    public IReadOnlyList<ParameterOption>? Options { get; init; }
}
