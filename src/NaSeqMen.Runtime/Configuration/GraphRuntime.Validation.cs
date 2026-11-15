// SPDX-License-Identifier: MIT
using NaSeqMen.Contracts;
using Microsoft.CodeAnalysis.Scripting;
using static NaSeqMen.Contracts.MessageCatalog;

namespace NaSeqMen.Runtime;

public sealed partial class GraphRuntime
{
    private static void CheckNodeRoles(Prepared previous, Prepared candidate, List<LocalizedMessage> errors)
    {
        foreach (var (id, node) in candidate.Nodes)
            if (previous.Nodes.TryGetValue(id, out var original) && original.Kind != node.Kind)
                errors.Add(M("runtime.fixedNodeRole", ("node", node.Name)));
    }

    private (Prepared? Prepared, List<LocalizedMessage> Errors) Prepare(Configuration configuration)
    {
        var errors = new List<LocalizedMessage>();
        Configuration config;
        try { config = Clone(configuration); }
        catch (Exception ex) { return (null, [M("runtime.invalidConfig", ("detail", FromException(ex)))]); }
        if (config.Version != 1) errors.Add(M("runtime.configVersion"));
        if (config.Nodes is null || config.Edges is null || config.Connections is null || config.Mappings is null)
            return (null, [M("runtime.configArrays")]);
        if (config.Nodes.Count > 500 || config.Edges.Count > 2000) errors.Add(M("runtime.configLimits"));
        if (config.Nodes.Any(n => n is null) || config.Edges.Any(e => e is null) ||
            config.Connections.Any(c => c is null) || config.Mappings.Any(m => m is null))
            return (null, [M("runtime.configNullItems")]);
        CheckIds(config.Nodes.Select(n => n.Id), "nodes", errors);
        CheckIds(config.Connections.Select(c => c.Id), "connections", errors);
        CheckIds(config.Mappings.Select(m => m.Id), "mappings", errors);
        var connectionIds = config.Connections.Where(c => !string.IsNullOrWhiteSpace(c.Id)).Select(c => c.Id).ToHashSet();
        var nodeIds = config.Nodes.Where(n => !string.IsNullOrWhiteSpace(n.Id)).Select(n => n.Id).ToHashSet();
        foreach (var connection in config.Connections)
        {
            if (string.IsNullOrEmpty(connection.Module) || !_modules.ContainsKey(connection.Module))
                errors.Add(M("runtime.connectionModule", ("connection", connection.Name), ("module", connection.Module)));
            if (connection.Settings is null || connection.Settings.Any(p => p.Value is null))
                errors.Add(M("runtime.connectionSettings", ("connection", connection.Name)));
        }
        var runners = new Dictionary<string, ScriptRunner<Signal?>>();
        foreach (var node in config.Nodes)
        {
            if (node.PreviewOnly && node.Kind is not ("source" or "action"))
                errors.Add(M("runtime.previewKind", ("node", node.Name)));
            if (!PortTypes.Supported.Contains(node.InputType) || !PortTypes.Supported.Contains(node.OutputType))
                errors.Add(M("runtime.portType", ("node", node.Name)));
            if (node.Kind == "transform" && node.Operation == "scale" &&
                (node.InputType is not ("any" or "number") || node.OutputType is not ("any" or "number")))
                errors.Add(M("runtime.scalePorts", ("node", node.Name)));
            if (node.Kind == "transform" && node.Operation == "text" && node.OutputType is not ("any" or "text"))
                errors.Add(M("runtime.textPort", ("node", node.Name)));
            if (node.Settings is null || node.Settings.Any(p => p.Value is null))
            { errors.Add(M("runtime.nodeSettings", ("node", node.Name))); continue; }
            if (node.ConnectionId is null || node.Module is null || node.Operation is null)
            { errors.Add(M("runtime.nodeNullStrings", ("node", node.Name))); continue; }
            if (node.ConnectionId.Length > 0)
            {
                if (!connectionIds.Contains(node.ConnectionId)) errors.Add(M("runtime.connectionMissing", ("node", node.Name)));
                else if (node.Module.Length > 0 && config.Connections.First(c => c.Id == node.ConnectionId).Module != node.Module)
                    errors.Add(M("runtime.connectionMismatch", ("node", node.Name)));
            }
            switch (node.Kind)
            {
                case "source":
                    if (node.Module.Length > 0 && node.Module != "test" && !_modules.ContainsKey(node.Module))
                        errors.Add(M("runtime.sourceModule", ("node", node.Name)));
                    break;
                case "event":
                    if (!ValidEvent(node, config)) errors.Add(M("runtime.eventInvalid", ("node", node.Name)));
                    break;
                case "condition":
                    if (node.Module.Length > 0) CheckOperation(node, condition: true, errors);
                    else if (!ValidBuiltInCondition(node))
                        errors.Add(M("runtime.conditionUnknown", ("node", node.Name)));
                    break;
                case "transform":
                    if (node.Operation == "scale")
                    {
                        if (!FiniteNumber(node.Settings.GetValueOrDefault("factor", "1"), out _) ||
                            !FiniteNumber(node.Settings.GetValueOrDefault("offset", "0"), out _))
                            errors.Add(M("runtime.scaleParameters", ("node", node.Name)));
                    }
                    else if (node.Operation != "text") errors.Add(M("runtime.transformUnknown", ("node", node.Name)));
                    break;
                case "script":
                    if (!node.Settings.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
                        errors.Add(M("runtime.scriptMissing", ("node", node.Name)));
                    else if (!string.IsNullOrWhiteSpace(node.Id))
                    {
                        try { runners[node.Id] = _compiler.Compile(code); }
                        catch (Exception ex) { errors.Add(M("runtime.scriptCompile", ("node", node.Name), ("detail", FromException(ex)))); }
                    }
                    break;
                case "action": CheckOperation(node, condition: false, errors); break;
                default: errors.Add(M("runtime.nodeKind", ("node", node.Name), ("kind", node.Kind))); break;
            }
        }
        foreach (var mapping in config.Mappings)
        {
            if (mapping.Policy is not ("observe" or "map"))
                errors.Add(M("runtime.mappingSuppression", ("mapping", mapping.Name)));
            if (string.IsNullOrWhiteSpace(mapping.Source) || string.IsNullOrWhiteSpace(mapping.Control))
                errors.Add(M("runtime.mappingInput"));
            if (mapping.Policy == "map" && string.IsNullOrWhiteSpace(mapping.Name)) errors.Add(M("runtime.mappingName"));
        }
        if (config.Mappings.GroupBy(m => (m.Source, m.Control)).Any(g => g.Count() > 1))
            errors.Add(M("runtime.mappingDuplicate"));
        var edges = new HashSet<(string, string)>();
        foreach (var edge in config.Edges)
        {
            if (!nodeIds.Contains(edge.From) || !nodeIds.Contains(edge.To)) errors.Add(M("runtime.edgeMissingNode"));
            if (!edges.Add((edge.From, edge.To))) errors.Add(M("runtime.edgeDuplicate"));
            if (config.Nodes.Any(n => n.Id == edge.To && n.Kind == "source")) errors.Add(M("runtime.edgeIntoSource"));
            var from = config.Nodes.FirstOrDefault(n => n.Id == edge.From);
            var to = config.Nodes.FirstOrDefault(n => n.Id == edge.To);
            if (from is not null && to is not null && !PortTypes.Compatible(from, to))
                errors.Add(M("runtime.edgeTypes", ("from", from.Name), ("output", PortTypes.Output(from)), ("to", to.Name), ("input", PortTypes.Input(to))));
        }
        if (errors.Count == 0 && HasCycle(config)) errors.Add(M("runtime.graphCycle"));
        return errors.Count == 0 ? (new Prepared(config, runners), errors) : (null, errors);
    }

    private static void CheckIds(IEnumerable<string> ids, string kind, List<LocalizedMessage> errors)
    {
        var values = ids.ToArray();
        if (values.Any(string.IsNullOrWhiteSpace)) errors.Add(M("runtime.idEmpty", ("kind", M("runtime.entity." + kind))));
        if (values.Distinct().Count() != values.Length) errors.Add(M("runtime.idDuplicate", ("kind", M("runtime.entity." + kind))));
    }

    private void CheckOperation(NodeDefinition node, bool condition, List<LocalizedMessage> errors)
    {
        if (!_modules.TryGetValue(node.Module, out var module))
        { errors.Add(M("runtime.nodeModule", ("node", node.Name), ("module", node.Module))); return; }
        var operations = condition ? module.Descriptor.Conditions : module.Descriptor.Actions;
        if (!operations.Any(o => o.Id == node.Operation)) errors.Add(M("runtime.nodeOperation", ("node", node.Name), ("operation", node.Operation)));
    }

    private static bool HasCycle(Configuration config)
    {
        var degrees = config.Nodes.ToDictionary(n => n.Id, _ => 0);
        foreach (var edge in config.Edges) degrees[edge.To]++;
        var ready = new Queue<string>(degrees.Where(p => p.Value == 0).Select(p => p.Key));
        var count = 0;
        while (ready.TryDequeue(out var id))
        {
            count++;
            foreach (var edge in config.Edges.Where(e => e.From == id))
                if (--degrees[edge.To] == 0) ready.Enqueue(edge.To);
        }
        return count != config.Nodes.Count;
    }

}
