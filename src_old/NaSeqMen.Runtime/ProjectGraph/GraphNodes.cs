// SPDX-License-Identifier: MIT
using System.Text.Json;
using NaSeqMen.Core;
using NaSeqMen.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace NaSeqMen.Runtime.ProjectGraph;

public sealed class GraphScriptContext(Packet input, string port,
    Func<string, string, JsonElement, ValueTask<IReadOnlyList<Emission>>> invoke)
{
    private readonly List<Emission> _outputs = [];
    public JsonElement Input { get; } = input.Data;
    public string InputPort { get; } = port;
    public IReadOnlyList<Emission> Outputs => _outputs;
    public void Emit(string port, object? value) => _outputs.Add(new(port, GraphJson.Value(value)));
    public ValueTask<IReadOnlyList<Emission>> CallAsync(string node, string port, JsonElement arguments) =>
        invoke(node, port, arguments);
}

internal static class GraphNodes
{
    internal static readonly NodeType[] Types =
    [
        new("inject", "Inject", [new("out", PortDirection.Output)], "input", Parameters: [new("payload", "object")]),
        new("debug", "Debug", [new("in", PortDirection.Input)], "debug"),
        new("switch", "Switch", [new("in", PortDirection.Input)], Parameters: [new("all", "boolean"), new("outputs", "array", ItemType: "text"), new("cases", "array", Properties: [new("field", "text", true), new("equals", "any", true), new("port", "text", true)])]),
        new("map", "Map", [new("in", PortDirection.Input), new("out", PortDirection.Output)], Parameters: [new("inputType", "text", DefaultValue: GraphJson.Value("any")), new("outputType", "text", DefaultValue: GraphJson.Value("any")), new("convert", "text", Options: ["", "text", "parse-number", "parse-boolean"]), new("fields", "object")]),
        new("script", "C#", [new("in", PortDirection.Input)], Parameters: [new("code", "code", true), new("inputs", "array", DefaultValue: GraphJson.Value(new[] { "in" }), ItemType: "text"), new("outputs", "array", DefaultValue: GraphJson.Value(new[] { "out" }), ItemType: "text")]),
        new("subgraph", "Subgraph", [new("in", PortDirection.Input), new("out", PortDirection.Output)], Parameters: [new("page", "text", true, Resource: "pages")]),
        new("inlet", "Inlet", [new("out", PortDirection.Output)], "input", Parameters: [new("port", "text", true), new("type", "text", DefaultValue: GraphJson.Value("any"))]),
        new("outlet", "Outlet", [new("in", PortDirection.Input)], "output", Parameters: [new("port", "text", true), new("type", "text", DefaultValue: GraphJson.Value("any"))]),
        new("capture.reply", "Resolve capture", [new("in", PortDirection.Input)], "output", Parameters: [new("kind", "text", Options: ["input", "pass", "drop", "replace"], DefaultValue: GraphJson.Value("input"))])
    ];

    internal static Port[] Ports(Node node, NodeType type)
    {
        if (node.Type == "map") return
        [
            new("in", PortDirection.Input, node.Settings.Text("inputType") ?? "any"),
            new("out", PortDirection.Output, node.Settings.Text("outputType") ?? "any")
        ];
        if (node.Type is not ("script" or "switch")) return type.Ports;
        var outputs = ReadPorts("outputs", PortDirection.Output, node.Type == "script" ? [new("out", PortDirection.Output)] : []);
        if (node.Type == "switch") return [.. type.Ports, .. outputs, new("default", PortDirection.Output)];
        return [.. ReadPorts("inputs", PortDirection.Input, type.Ports), .. outputs];

        Port[] ReadPorts(string field, PortDirection direction, Port[] fallback) =>
            node.Settings.Get(field) is { ValueKind: JsonValueKind.Array } list
                ? list.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String
                    ? new Port(x.GetString()!, direction)
                    : new Port(x.Text("id") ?? "", direction, x.Text("type") ?? "any")).ToArray()
                : fallback;
    }

    internal static IEnumerable<Emission> Switch(Node node, JsonElement data)
    {
        var rules = node.Settings.Get("cases");
        var all = node.Settings.Get("all").ValueKind == JsonValueKind.True;
        var matched = false;
        if (rules.ValueKind == JsonValueKind.Array)
            foreach (var rule in rules.EnumerateArray())
            {
                var actual = Field(data, rule.Text("field"));
                var expected = rule.Get("equals");
                if (actual.ValueKind == JsonValueKind.Undefined || expected.ValueKind == JsonValueKind.Undefined ||
                    !JsonElement.DeepEquals(actual, expected)) continue;
                matched = true;
                yield return new(rule.Text("port")!, data);
                if (!all) yield break;
            }
        if (!matched) yield return new("default", data);
    }

    internal static JsonElement Map(JsonElement settings, JsonElement data)
    {
        var fields = settings.Get("fields");
        if (fields.ValueKind != JsonValueKind.Object) return Convert(data, settings.Text("convert"));
        var result = new Dictionary<string, JsonElement>();
        foreach (var field in fields.EnumerateObject())
        {
            var value = field.Value.Text("from") is { } source ? Field(data, source) : field.Value.Get("value");
            if (value.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException(field.Name);
            result.Add(field.Name, Convert(value, field.Value.Text("convert")));
        }
        return GraphJson.Value(result);
    }

    private static JsonElement Convert(JsonElement value, string? conversion)
    {
        if (conversion is null or "") return value;
        return conversion switch
        {
            "text" => GraphJson.Value(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()),
            "parse-number" when value.ValueKind == JsonValueKind.String &&
                double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) => GraphJson.Value(number),
            "parse-boolean" when value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var flag) => GraphJson.Value(flag),
            _ => throw new InvalidDataException($"Cannot apply conversion: {conversion}")
        };
    }

    private static JsonElement Field(JsonElement input, string? path)
    {
        foreach (var part in (path ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries)) input = input.Get(part);
        return input;
    }
}
