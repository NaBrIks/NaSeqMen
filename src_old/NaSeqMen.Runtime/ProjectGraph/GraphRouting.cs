// SPDX-License-Identifier: MIT
using System.Text.Json;
using NaSeqMen.Core;

namespace NaSeqMen.Runtime.ProjectGraph;

public sealed partial class ProjectRuntime
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string, string, string), byte> _watches = new();

    public void Watch(string project, string page, string node, string port, bool enabled)
    {
        var key = (project, page, node, port);
        if (enabled && _watches.Count < 128) _watches.TryAdd(key, 0);
        else if (!enabled) _watches.TryRemove(key, out _);
    }

    private IEnumerable<(PreparedPage Page, Node Node)> EventNodes(PreparedProject project, string connection, AdapterEvent input) =>
        project.Pages.Values.SelectMany(p => p.Nodes.Values.Where(n => n.Type == input.NodeType && n.ConnectionId == connection)
            .Select(n => (p, n)));

    private ValueTask PublishAsync(Revision revision, PreparedProject project, string connection, AdapterEvent input)
    {
        if (!Volatile.Read(ref revision.Active)) return ValueTask.CompletedTask;
        var packet = Packet.Create(input.Data, connection);
        if (!_queue.Writer.TryWrite(async () =>
        {
            foreach (var (page, node) in EventNodes(project, connection, input))
                await DispatchAsync(revision, project, page, node, "out", packet);
        })) Report(project.Definition.Id, connection, new IOException("Graph input queue is full."));
        return ValueTask.CompletedTask;
    }

    private CaptureReply Decide(Revision revision, PreparedProject project, string connection, AdapterEvent input)
    {
        if (!Volatile.Read(ref revision.Active)) return new(null, Active: false);
        var packet = Packet.Create(input.Data, connection);
        JsonElement? decision = null;
        var conflict = false;
        var budget = 512;
        var deferred = new List<(PreparedPage Page, Wire Wire, JsonElement Data)>();
        try
        {
            foreach (var (page, node) in EventNodes(project, connection, input))
                Visit(page, node, "out", input.Data, 0);
        }
        catch (Exception error)
        {
            Report(project.Definition.Id, connection, error);
            return new(GraphJson.Value(new { kind = "pass" }));
        }
        if (conflict)
        {
            Report(project.Definition.Id, connection, new InvalidDataException("Conflicting capture decisions."));
            return new(GraphJson.Value(new { kind = "pass" }));
        }
        return new(decision, () =>
        {
            if (deferred.Count != 0 && !_queue.Writer.TryWrite(async () =>
            {
                foreach (var (page, wire, data) in deferred)
                {
                    Pulse(project, page, wire.To, wire);
                    await RunAsync(revision, project, page, page.Nodes[wire.To], wire.Input, packet with { Data = data }, null);
                }
            })) Report(project.Definition.Id, connection, new IOException("Graph input queue is full."));
            return ValueTask.CompletedTask;
        });

        void Visit(PreparedPage page, Node node, string port, JsonElement data, int depth, bool active = true)
        {
            if (depth > 128 || --budget < 0) throw new InvalidDataException("Capture decision exceeds the graph limit.");
            if (active) Pulse(project, page, node.Id);
            Observe(project, page, node, port, packet with { Data = data });
            foreach (var wire in page.Edges[(node.Id, port)])
            {
                var next = page.Nodes[wire.To];
                if (!page.DecisionNodes.Contains(next.Id))
                {
                    deferred.Add((page, wire, data));
                    continue;
                }
                if (next.Type == "capture.reply")
                {
                    var value = next.Settings.Text("kind") is "pass" or "drop" or "replace" ? GraphJson.Value(new { kind = next.Settings.Text("kind") }) : data;
                    if (!GraphJson.Accepts("capture.decision", value)) throw new InvalidDataException("Invalid capture decision.");
                    conflict |= decision.HasValue;
                    decision = value;
                    Pulse(project, page, next.Id, wire);
                    Observe(project, page, next, wire.Input, packet with { Data = value });
                    continue;
                }
                if (!_types[next.Type].CaptureSafe) Pulse(project, page, next.Id, wire);
                var outputs = _types[next.Type].CaptureSafe && _types[next.Type].Adapter is { } adapterId
                    ? _adapters[adapterId].EvaluateCapture(next, data)
                    : next.Type == "switch" ? GraphNodes.Switch(next, data)
                    : [new Emission("out", GraphNodes.Map(next.Settings, data))];
                foreach (var emission in outputs)
                {
                    if (_types[next.Type].CaptureSafe && emission.Active) Pulse(project, page, next.Id, wire);
                    Visit(page, next, emission.Port, emission.Data, depth + 1, emission.Active);
                }
            }
        }
    }

    private async ValueTask DispatchAsync(Revision revision, PreparedProject project, PreparedPage page,
        Node from, string output, Packet packet, Func<Emission, ValueTask>? outlet = null, bool active = true)
    {
        if (!Volatile.Read(ref revision.Active) || _stop.IsCancellationRequested) return;
        var port = GraphPreparation.Find(page.Ports, from.Id, output, PortDirection.Output);
        if (!GraphJson.Accepts(port.Type, packet.Data)) throw new InvalidDataException($"{from.Id}.{output}: expected {port.Type}");
        if (active) Pulse(project, page, from.Id);
        Observe(project, page, from, output, packet);
        foreach (var wire in page.Edges[(from.Id, output)])
        {
            if (!_types[page.Nodes[wire.To].Type].CaptureSafe) Pulse(project, page, wire.To, wire);
            await RunAsync(revision, project, page, page.Nodes[wire.To], wire.Input, packet, outlet);
        }
    }

    private async ValueTask RunAsync(Revision revision, PreparedProject project, PreparedPage page,
        Node node, string input, Packet packet, Func<Emission, ValueTask>? outlet)
    {
        if (!Volatile.Read(ref revision.Active) || _stop.IsCancellationRequested) return;
        try
        {
            var port = GraphPreparation.Find(page.Ports, node.Id, input, PortDirection.Input);
            if (!GraphJson.Accepts(port.Type, packet.Data)) throw new InvalidDataException($"{node.Id}.{input}: expected {port.Type}");
            Observe(project, page, node, input, packet);
            IEnumerable<Emission> outputs;
            if (_types[node.Type].CaptureSafe && _types[node.Type].Adapter is { } safeAdapter)
            {
                outputs = _adapters[safeAdapter].EvaluateCapture(node, packet.Data);
                foreach (var emission in outputs) await DispatchAsync(revision, project, page, node, emission.Port, packet with { Data = emission.Data }, outlet, emission.Active);
                return;
            }
            switch (node.Type)
            {
                case "debug": case "capture.reply": return;
                case "switch": outputs = GraphNodes.Switch(node, packet.Data); break;
                case "map": outputs = [new("out", GraphNodes.Map(node.Settings, packet.Data))]; break;
                case "script":
                    var context = new GraphScriptContext(packet, input, async (targetId, targetPort, payload) =>
                    {
                        if (!page.Nodes.TryGetValue(targetId, out var target) || _types[target.Type].Adapter is null)
                            throw new InvalidDataException($"Unknown adapter node: {targetId}");
                        var inputPort = GraphPreparation.Find(page.Ports, targetId, targetPort, PortDirection.Input);
                        if (!GraphJson.Accepts(inputPort.Type, payload)) throw new InvalidDataException($"Invalid argument: {targetPort}");
                        if (project.Definition.DryRun && _types[target.Type].External)
                            return [new("suppressed", GraphJson.Empty)];
                        if (!revision.Sessions.TryGetValue((project.Definition.Id, target.ConnectionId!), out var adapter))
                            throw new InvalidOperationException("Connection is disabled or unavailable.");
                        return await adapter.InvokeAsync(target, targetPort, packet with { Data = payload }, _stop.Token);
                    });
                    await page.Scripts[node.Id](context, _stop.Token);
                    outputs = context.Outputs;
                    break;
                case "subgraph":
                    var nested = project.Pages[node.Settings.Text("page")!];
                    var inlet = nested.Nodes.Values.Single(n => n.Type == "inlet" && (n.Settings.Text("port") ?? n.Id) == input);
                    await DispatchAsync(revision, project, nested, inlet, "out", packet, emission =>
                        DispatchAsync(revision, project, page, node, emission.Port, packet with { Data = emission.Data }, outlet));
                    return;
                case "outlet":
                    if (outlet is not null) await outlet(new(node.Settings.Text("port") ?? node.Id, packet.Data));
                    return;
                default:
                    if (project.Definition.DryRun && _types[node.Type].External)
                    {
                        Trace(new(DateTimeOffset.UtcNow, project.Definition.Id, page.Definition.Id, node.Id, input, packet.Id, "dryRun", packet.Data));
                        return;
                    }
                    if (!revision.Sessions.TryGetValue((project.Definition.Id, node.ConnectionId!), out var session))
                        throw new InvalidOperationException("Connection is disabled or unavailable.");
                    outputs = await session.InvokeAsync(node, input, packet, _stop.Token);
                    break;
            }
            foreach (var emission in outputs)
                await DispatchAsync(revision, project, page, node, emission.Port, packet with { Data = emission.Data }, outlet);
        }
        catch (Exception error)
        {
            var value = GraphJson.Value(new { message = error.Message });
            Trace(new(DateTimeOffset.UtcNow, project.Definition.Id, page.Definition.Id, node.Id, input, packet.Id, "error", value));
            if (page.Ports[node.Id].Any(p => p.Id == "error" && p.Direction == PortDirection.Output))
                await DispatchAsync(revision, project, page, node, "error", packet with { Data = value }, outlet);
        }
    }

    private void Observe(PreparedProject project, PreparedPage page, Node node, string port, Packet packet)
    {
        if (node.Type is not ("debug" or "capture.reply") &&
            !_watches.ContainsKey((project.Definition.Id, page.Definition.Id, node.Id, port)) &&
            !_watches.ContainsKey((project.Definition.Id, page.Definition.Id, node.Id, ""))) return;
        Trace(new(DateTimeOffset.UtcNow, project.Definition.Id, page.Definition.Id, node.Id, port, packet.Id, "message", packet.Data));
    }
}
