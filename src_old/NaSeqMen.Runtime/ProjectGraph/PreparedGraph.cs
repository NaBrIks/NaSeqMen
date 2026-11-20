// SPDX-License-Identifier: MIT
using System.Text.Json;
using Microsoft.CodeAnalysis.Scripting;
using NaSeqMen.Core;
using NaSeqMen.Scripting;

namespace NaSeqMen.Runtime.ProjectGraph;

internal sealed record PreparedPage(GraphPage Definition, Dictionary<string, Node> Nodes,
    Dictionary<string, Port[]> Ports, ILookup<(string Node, string Port), Wire> Edges,
    Dictionary<string, ScriptRunner<object?>> Scripts, HashSet<string> DecisionNodes);
internal sealed record PreparedProject(WorkflowProject Definition, Dictionary<string, PreparedPage> Pages);

internal static class GraphPreparation
{
    internal static PreparedProject[] Prepare(Workspace config, IReadOnlyDictionary<string, NodeType> types,
        IReadOnlyDictionary<string, IGraphAdapter> adapters)
    {
        if (config.Version != 2) throw new InvalidDataException("Unsupported workspace version.");
        _ = Index(config.Projects, x => x.Id);
        var scripts = new ScriptCompiler();
        var result = new List<PreparedProject>();
        var activeCaptures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in config.Projects)
        {
            var connections = Index(project.Connections, x => x.Id);
            foreach (var connection in connections.Values)
            {
                if (!adapters.TryGetValue(connection.Adapter, out var adapter)) throw new InvalidDataException(connection.Adapter);
                adapter.Validate(connection);
                if (connection.Enabled && !project.DryRun && !activeCaptures.Add(connection.Adapter) &&
                    adapter.Nodes.Any(n => n.Ports.Any(p => p.Type == "capture.packet")))
                    throw new InvalidDataException($"Capture adapter already active: {connection.Adapter}");
            }
            var pages = Index(project.Pages, x => x.Id);
            var children = pages.ToDictionary(p => p.Key, p => p.Value.Nodes.Where(n => n.Type == "subgraph")
                .Select(n => n.Settings.Text("page") ?? "").ToArray());
            CheckCycles(children);
            var prepared = new Dictionary<string, PreparedPage>();
            foreach (var page in project.Pages)
            {
                var nodes = Index(page.Nodes, x => x.Id);
                var ports = new Dictionary<string, Port[]>();
                var runners = new Dictionary<string, ScriptRunner<object?>>();
                foreach (var node in page.Nodes)
                {
                    if (!types.TryGetValue(node.Type, out var type)) throw new InvalidDataException(node.Type);
                    if (!double.IsFinite(node.X) || !double.IsFinite(node.Y)) throw new InvalidDataException(node.Id);
                    if (type.Adapter is { } owner && (node.ConnectionId is not { } connection ||
                        !connections.TryGetValue(connection, out var selected) || selected.Adapter != owner))
                        throw new InvalidDataException($"Missing {owner} connection: {node.Id}");
                    if (type.Adapter is { } adapterId) adapters[adapterId].ValidateNode(node, connections[node.ConnectionId!]);
                    ports[node.Id] = node.Type == "subgraph" ? Exposed(pages[node.Settings.Text("page")!]) : GraphNodes.Ports(node, type);
                    _ = Index(ports[node.Id], p => p.Id);
                    if (node.Type == "script")
                        runners.Add(node.Id, scripts.Compile<object?, GraphScriptContext>(
                            (node.Settings.Text("code") ?? "") + "\nreturn null;",
                            typeof(GraphScriptContext).Assembly, typeof(JsonElement).Assembly, typeof(Packet).Assembly));
                }
                foreach (var wire in page.Wires)
                {
                    var source = Find(ports, wire.From, wire.Output, PortDirection.Output);
                    var target = Find(ports, wire.To, wire.Input, PortDirection.Input);
                    if (source.Type != target.Type && source.Type != "any" && target.Type != "any")
                        throw new InvalidDataException($"Incompatible ports: {wire.From}.{wire.Output} → {wire.To}.{wire.Input}");
                }
                if (page.Wires.Distinct().Count() != page.Wires.Length) throw new InvalidDataException("Duplicate wire.");
                CheckCycles(nodes.Keys.ToDictionary(id => id, id => page.Wires.Where(w => w.From == id).Select(w => w.To).ToArray()));
                var decision = nodes.Values.Where(n => n.Type == "capture.reply").Select(n => n.Id).ToHashSet();
                bool changed;
                do
                {
                    changed = false;
                    foreach (var wire in page.Wires)
                        if (decision.Contains(wire.To)) changed |= decision.Add(wire.From);
                } while (changed);
                foreach (var node in nodes.Values.Where(n => decision.Contains(n.Id)))
                {
                    if (node.Type is not ("switch" or "map" or "capture.reply") && !types[node.Type].CaptureSafe &&
                        !ports[node.Id].Any(p => p.Direction == PortDirection.Output && p.Type == "capture.packet"))
                        throw new InvalidDataException($"Unsupported capture decision node: {node.Id}");
                    if (node.Type == "switch" && node.Settings.Get("all").ValueKind == JsonValueKind.True)
                        throw new InvalidDataException($"Capture switch requires first-match routing: {node.Id}");
                }
                prepared.Add(page.Id, new(page, nodes, ports, page.Wires.ToLookup(w => (w.From, w.Output)), runners, decision));
            }
            var captureHandlers = prepared.Values.SelectMany(p => p.Nodes.Values
                .Where(n => p.DecisionNodes.Contains(n.Id) && portsContainCapture(p, n)))
                .GroupBy(n => n.ConnectionId);
            if (captureHandlers.Any(group => group.Count() > 1)) throw new InvalidDataException("Multiple capture decision handlers.");
            result.Add(new(project, prepared));
        }
        return [.. result];

        static bool portsContainCapture(PreparedPage p, Node n) => p.Ports[n.Id].Any(port => port.Direction == PortDirection.Output && port.Type == "capture.packet") && !p.Ports[n.Id].Any(port => port.Direction == PortDirection.Input);
    }

    internal static Port Find(Dictionary<string, Port[]> ports, string node, string id, PortDirection direction) =>
        ports.TryGetValue(node, out var list) && list.FirstOrDefault(p => p.Id == id && p.Direction == direction) is { } port
            ? port : throw new InvalidDataException($"Unknown port: {node}.{id}");

    private static Port[] Exposed(GraphPage page) => page.Nodes.Where(n => n.Type is "inlet" or "outlet")
        .Select(n => new Port(n.Settings.Text("port") ?? n.Id, n.Type == "inlet" ? PortDirection.Input : PortDirection.Output,
            n.Settings.Text("type") ?? "any")).ToArray();

    private static Dictionary<string, T> Index<T>(IEnumerable<T> values, Func<T, string> id)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in values)
        {
            var key = id(item);
            if (string.IsNullOrWhiteSpace(key) || !result.TryAdd(key, item))
                throw new InvalidDataException($"Invalid or duplicate ID: {key}");
        }
        return result;
    }

    private static void CheckCycles(Dictionary<string, string[]> links)
    {
        var visiting = new HashSet<string>();
        var done = new HashSet<string>();
        foreach (var key in links.Keys) Visit(key);
        void Visit(string key)
        {
            if (done.Contains(key)) return;
            if (!links.ContainsKey(key)) throw new InvalidDataException($"Unknown page: {key}");
            if (!visiting.Add(key)) throw new InvalidDataException($"Cycle: {key}");
            foreach (var next in links[key]) Visit(next);
            visiting.Remove(key);
            done.Add(key);
        }
    }
}
