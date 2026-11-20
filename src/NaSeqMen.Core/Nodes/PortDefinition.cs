namespace NaSeqMen.Core.Nodes;

public enum PortDirection
{
    Input,
    Output,
}

public enum PortDataType
{
    Any,
    Event,
    String,
    Number,
    Boolean,
    Object,
    Binary,
}

public static class PortDataTypeExtensions
{
    public static bool IsCompatibleWith(this PortDataType source, PortDataType target)
    {
        if (source == target)
            return true;
        if (source == PortDataType.Any || target == PortDataType.Any)
            return true;
        if (target == PortDataType.Event)
            return true;
        return false;
    }
}

public sealed record PortDefinition
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required PortDirection Direction { get; init; }
    public PortDataType DataType { get; init; } = PortDataType.Any;
    public bool AllowMultiple { get; init; } = true;
    public string? Description { get; init; }
}
