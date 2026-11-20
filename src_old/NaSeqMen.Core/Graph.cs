// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NaSeqMen.Core;

[JsonConverter(typeof(JsonStringEnumConverter<PortDirection>))]
public enum PortDirection { Input, Output }

public sealed record Port(string Id, PortDirection Direction, string Type = "any");
public sealed record Parameter(string Id, string Type, bool Required = false,
    string[]? Options = null, string? LabelKey = null, string? DescriptionKey = null,
    JsonElement? DefaultValue = null, Parameter[]? Properties = null, string? ItemType = null, string? Resource = null, string? Format = null);
public sealed record NodeType(string Id, string Label, Port[] Ports, string Category = "logic",
    string? Adapter = null, bool External = false, string? DescriptionKey = null, Parameter[]? Parameters = null,
    string? ManualInput = null, bool CaptureSafe = false, string[]? SummaryFields = null);
public sealed record AdapterType(string Id, Parameter[] Parameters);
public sealed record Connection(string Id, string Name, string Adapter, JsonElement Settings, bool Enabled = true);
public sealed record Node(string Id, string Type, string Name, string? ConnectionId, JsonElement Settings, double X = 20, double Y = 20,
    string? LabelKey = null);
public sealed record Wire(string From, string Output, string To, string Input);
public sealed record GraphPage(string Id, string Name, Node[] Nodes, Wire[] Wires);
public sealed record WorkflowProject(string Id, string Name, Connection[] Connections, GraphPage[] Pages, bool DryRun = true);
public sealed record Workspace(WorkflowProject[] Projects, int Version = 2);
public sealed record Packet(string Id, JsonElement Data, string? Origin = null)
{
    public static Packet Create(JsonElement data, string? origin = null) => new(Guid.NewGuid().ToString("N"), data, origin);
}
public sealed record Emission(string Port, JsonElement Data, bool Active = true);
public sealed record TraceEntry(DateTimeOffset Time, string Project, string Page, string Node, string Port,
    string MessageId, string Kind, JsonElement Data, string? Detail = null);
public sealed record AdapterEvent(string NodeType, JsonElement Data);
public sealed record CaptureReply(JsonElement? Decision, Func<ValueTask>? Continue = null, bool Active = true);
public sealed record AdapterHost(Func<AdapterEvent, ValueTask> Publish,
    Func<AdapterEvent, CaptureReply> Decide, Action<Exception> Error);

public interface IGraphAdapter
{
    string Id { get; }
    AdapterType Describe() => new(Id, []);
    IReadOnlyList<NodeType> Nodes { get; }
    void Validate(Connection connection);
    void ValidateNode(Node node, Connection connection) { }
    IReadOnlyList<Emission> EvaluateCapture(Node node, JsonElement data) => throw new NotSupportedException(node.Type);
    ValueTask<IAdapterSession> OpenAsync(Connection connection, AdapterHost host, CancellationToken cancellationToken);
    ValueTask<IAdapterSession?> OpenTestAsync(Connection connection, AdapterHost host, CancellationToken cancellationToken)
        => ValueTask.FromResult<IAdapterSession?>(null);
}

public interface IAdapterSession : IAsyncDisposable
{
    ValueTask<IReadOnlyList<Emission>> InvokeAsync(Node node, string port, Packet message, CancellationToken cancellationToken);
}

public static class GraphJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
    public static JsonElement Empty { get; } = JsonSerializer.SerializeToElement(new { });
    public static JsonElement Value<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
    public static JsonElement Get(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
    public static string? Text(this JsonElement element, string name) =>
        element.Get(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    public static JsonElement OrEmpty(this JsonElement element) =>
        element.ValueKind == JsonValueKind.Undefined ? Empty : element;
    public static bool Accepts(string type, JsonElement value) => type switch
    {
        "any" => true,
        "object" => value.ValueKind == JsonValueKind.Object,
        "number" => value.ValueKind == JsonValueKind.Number,
        "text" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "capture.decision" => value.ValueKind == JsonValueKind.Object &&
            value.Text("kind") is "pass" or "drop" or "replace" or "adapter",
        _ => value.ValueKind == JsonValueKind.Object
    };
}
