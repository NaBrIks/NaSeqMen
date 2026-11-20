// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.CodeAnalysis.Scripting;
using NaSeqMen.Contracts;
using static NaSeqMen.Contracts.MessageCatalog;
using NaSeqMen.Scripting;

namespace NaSeqMen.Runtime;

public sealed partial class GraphRuntime : IModuleHost, IAsyncDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Dictionary<string, INaModule> _modules;
    private readonly ScriptCompiler _compiler = new();
    private readonly SemaphoreSlim _execution = new(1, 1);
    private readonly Channel<(Signal Signal, long Epoch, long DiscoveryVersion)> _input = Channel.CreateBounded<(Signal, long, long)>(
        new BoundedChannelOptions(2048) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _stop = new();
    private readonly string _configurationPath;
    private readonly HashSet<string> _connected = new(StringComparer.Ordinal);
    private Prepared _current = new(new Configuration(), new());
    private Task? _worker;
    private long _queueEpoch;

    public GraphRuntime(IEnumerable<INaModule> modules, string configurationPath, DiagnosticHub? diagnostics = null)
    {
        _modules = modules.ToDictionary(m => m.Descriptor.Id, StringComparer.Ordinal);
        _configurationPath = configurationPath;
        Diagnostics = diagnostics ?? new DiagnosticHub();
    }

    public DiagnosticHub Diagnostics { get; }
    public bool DryRun => _current.Config.DryRun;
    public IReadOnlyList<ModuleDescriptor> Modules => _modules.Values.Select(m => m.Descriptor).ToArray();
    public Configuration GetConfiguration() => Clone(_current.Config);

    public async Task StartAsync(Configuration configuration, CancellationToken cancellationToken = default)
    {
        foreach (var module in _modules.Values) await module.InitializeAsync(this, cancellationToken);
        var result = await ApplyAsync(configuration, persist: !File.Exists(_configurationPath),
            cancellationToken, tolerateUnavailableConnections: true);
        if (!result.Valid) throw new InvalidDataException(string.Join(Environment.NewLine, result.Errors));
        _worker = Task.Run(ProcessQueueAsync);
    }

    public async ValueTask<IReadOnlyList<DeviceDescriptor>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var devices = new List<DeviceDescriptor>();
        foreach (var module in _modules.Values)
        {
            try { devices.AddRange(await module.GetDevicesAsync(cancellationToken)); }
            catch (Exception ex) { Trace(new(DateTimeOffset.UtcNow, "", "", "error", M("runtime.moduleError", ("module", module.Descriptor.Id), ("detail", FromException(ex))))); }
        }
        return devices;
    }

    public ValidationResult Validate(Configuration configuration)
    {
        var (prepared, errors) = Prepare(configuration);
        if (prepared is not null) CheckNodeRoles(_current, prepared, errors);
        return ValidationResult.FromIssues(errors);
    }

    public async Task<ValidationResult> ApplyAsync(Configuration configuration, bool persist = true,
        CancellationToken cancellationToken = default, bool tolerateUnavailableConnections = false)
    {
        var (prepared, errors) = Prepare(configuration);
        if (prepared is null || errors.Count > 0) return ValidationResult.FromIssues(errors);
        await _execution.WaitAsync(cancellationToken);
        try
        {
            var previous = _current;
            CheckNodeRoles(previous, prepared, errors);
            if (errors.Count > 0) return ValidationResult.FromIssues(errors);
            var changed = prepared.Config.Connections.Where(c => c.Enabled &&
                (!_connected.Contains(c.Id) || !previous.Config.Connections.Any(p => p.Enabled && ConnectionEqual(p, c)))).ToArray();
            var removed = previous.Config.Connections.Where(p => p.Enabled && _connected.Contains(p.Id) &&
                !prepared.Config.Connections.Any(c => c.Enabled && ConnectionEqual(p, c))).ToArray();
            var started = new List<ConnectionDefinition>();
            try
            {
                foreach (var connection in removed)
                {
                    await _modules[connection.Module].DisconnectAsync(connection.Id, cancellationToken);
                    _connected.Remove(connection.Id);
                }
                foreach (var connection in changed)
                {
                    started.Add(connection);
                    try
                    {
                        await _modules[connection.Module].ConnectAsync(connection, cancellationToken);
                        _connected.Add(connection.Id);
                    }
                    catch (Exception ex) when (tolerateUnavailableConnections && ex is not OperationCanceledException)
                    {
                        _connected.Remove(connection.Id);
                        try { await _modules[connection.Module].DisconnectAsync(connection.Id, CancellationToken.None); } catch { }
                        Trace(new(DateTimeOffset.UtcNow, "", "", "error",
                            M("runtime.connectionUnavailable", ("connection", connection.Name), ("detail", FromException(ex)))));
                    }
                }
                if (persist) await SaveAsync(prepared.Config, cancellationToken);
                _current = prepared;
                return new(true, []);
            }
            catch (Exception ex)
            {
                foreach (var connection in started)
                {
                    _connected.Remove(connection.Id);
                    try { await _modules[connection.Module].DisconnectAsync(connection.Id, CancellationToken.None); } catch { }
                }
                foreach (var connection in removed)
                    try
                    {
                        await _modules[connection.Module].ConnectAsync(connection, CancellationToken.None);
                        _connected.Add(connection.Id);
                    }
                    catch (Exception rollback) { Trace(new(DateTimeOffset.UtcNow, "", "", "error", M("runtime.connectionRestore", ("detail", FromException(rollback))))); }
                return ValidationResult.FromIssues([M("runtime.applyFailed", ("detail", FromException(ex)))]);
            }
        }
        finally { _execution.Release(); }
    }

    private async Task SaveAsync(Configuration configuration, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_configurationPath)!);
        var temporary = _configurationPath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(configuration, Json), cancellationToken);
            File.Move(temporary, _configurationPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public ValueTask PublishAsync(Signal signal, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TryPublish(signal);
        return ValueTask.CompletedTask;
    }

    public bool TryPublish(Signal signal)
    {
        Trace(new(DateTimeOffset.UtcNow, signal.Id, "", "input", $"{signal.Source}: {signal.Control} {signal.Phase}", signal));
        var discoveryVersion = Interlocked.Read(ref _discoveryVersion);
        if (!CanRoute(discoveryVersion)) return true;
        if (!_input.Writer.TryWrite((signal, Interlocked.Read(ref _queueEpoch), discoveryVersion)))
        {
            Interlocked.Increment(ref _queueEpoch);
            Trace(new(DateTimeOffset.UtcNow, signal.Id, "", "error", M("runtime.inputFull"), signal));
            return false;
        }
        return true;
    }

    public void Trace(Diagnostic diagnostic) => Diagnostics.Publish(diagnostic);

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (var (signal, epoch, discoveryVersion) in _input.Reader.ReadAllAsync(_stop.Token))
                await ProcessAsync(signal, _stop.Token, epoch, discoveryVersion);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public Task ProcessAsync(Signal original, CancellationToken cancellationToken = default)
        => ProcessAsync(original, cancellationToken, Interlocked.Read(ref _queueEpoch), Interlocked.Read(ref _discoveryVersion));

    private async Task ProcessAsync(Signal original, CancellationToken cancellationToken, long epoch, long discoveryVersion)
    {
        if (!CanRoute(discoveryVersion)) return;
        await _execution.WaitAsync(cancellationToken);
        try
        {
            if (!CanRoute(discoveryVersion)) return;
            var prepared = _current;
            if (prepared.Epoch != epoch)
            {
                foreach (var sequence in prepared.Sequences.Values) sequence.Reset();
                prepared.Epoch = epoch;
            }
            ResetDisconnectedSequences(prepared, original);
            var signal = original;
            var budget = new VisitBudget(discoveryVersion);
            var mapping = prepared.Config.Mappings.FirstOrDefault(m => m.Source == original.Source && m.Control == original.Control);
            if (mapping?.Policy == "map") signal = original with { Control = mapping.Name, OriginalControl = original.Control };
            foreach (var source in prepared.Config.Nodes.Where(n => n.Enabled && n.Kind == "source")
                         .OrderBy(n => n.Order).ThenBy(n => n.Id, StringComparer.Ordinal))
            {
                if (MatchesSource(source, signal))
                    await VisitAsync(source, signal, prepared, budget, source.PreviewOnly ? source : null, cancellationToken);
            }
        }
        finally { _execution.Release(); }
    }

    private async Task VisitAsync(NodeDefinition node, Signal signal, Prepared prepared, VisitBudget budget,
        NodeDefinition? previewSource, CancellationToken cancellationToken, NodeDefinition? upstream = null)
    {
        if (!CanRoute(budget.DiscoveryVersion) || (!node.Enabled && node.Kind != "event")) return;
        cancellationToken.ThrowIfCancellationRequested();
        if (++budget.Visited > 4096)
        {
            if (budget.Visited == 4097) Emit(node, signal, "error", M("runtime.visitLimit"));
            return;
        }
        Signal? output = signal;
        var edgeOutcome = "match";
        try
        {
            if (node.Kind == "event")
            {
                if (node.Operation == "default" && !node.Enabled) return;
                var outcome = node.Enabled ? MatchEvent(node, signal, prepared, out output) : EventOutcome.Miss;
                if (outcome == EventOutcome.Pending) return;
                edgeOutcome = outcome == EventOutcome.Match ? "match" : "miss";
                if (outcome == EventOutcome.Match)
                {
                    CheckType(signal, PortTypes.Input(node), "input");
                    EmitArrival();
                    if (node.Operation == "sequence")
                        Emit(node, output, "transformed", M("runtime.sequenceMatched", ("event", output.Control)));
                }
                else if (prepared.Config.Edges.Any(e => e.From == node.Id && e.Outcome == "miss"))
                    Emit(node, signal, "blocked", M("runtime.eventMiss"));
            }
            else
            {
                EmitArrival();
                CheckType(signal, PortTypes.Input(node), "input");
            }
            switch (node.Kind)
            {
                case "condition":
                    var pass = node.Module.Length == 0
                        ? EvaluateBuiltInCondition(node, signal)
                        : await _modules[node.Module].EvaluateAsync(new(node.ConnectionId, node.Operation, Expand(node.Settings, signal), signal), cancellationToken);
                    Emit(node, signal, pass ? "passed" : "blocked", pass ? M("runtime.conditionPassed") : M("runtime.conditionBlocked"));
                    if (!pass) return;
                    break;
                case "transform":
                    if (node.Operation == "scale")
                    {
                        if (signal.ValueType != "number" || !FiniteNumber(signal.Value, out var value))
                            throw Error<InvalidDataException>(M("runtime.scaleInput"));
                        var result = value * double.Parse(node.Settings.GetValueOrDefault("factor", "1"), CultureInfo.InvariantCulture)
                            + double.Parse(node.Settings.GetValueOrDefault("offset", "0"), CultureInfo.InvariantCulture);
                        if (!double.IsFinite(result)) throw Error<InvalidDataException>(M("runtime.scaleResult"));
                        output = signal with { ValueType = "number", Value = result.ToString("G17", CultureInfo.InvariantCulture) };
                    }
                    else output = signal with { ValueType = "text", Value = Expand(node.Settings.GetValueOrDefault("template", "{value}"), signal) };
                    Emit(node, output, "transformed", output.Value);
                    break;
                case "script":
                    var context = new ScriptContext(
                        (module, connection, operation, arguments, token) => ExecuteActionAsync(node, signal, prepared,
                            previewSource, module, connection, operation, arguments, token, budget.DiscoveryVersion),
                        message => Emit(node, signal, "script", message), cancellationToken);
                    output = await prepared.Scripts[node.Id](new ScriptGlobals { Input = signal, Context = context }, cancellationToken);
                    if (output is null) { Emit(node, signal, "blocked", M("runtime.scriptNull")); return; }
                    output = output with { Id = signal.Id, Time = signal.Time };
                    Emit(node, output, "script", M("runtime.scriptOutput"));
                    break;
                case "action":
                    var action = await ExecuteActionAsync(node, signal, prepared, previewSource, node.Module, node.ConnectionId,
                        node.Operation, Expand(node.Settings, signal), cancellationToken, budget.DiscoveryVersion);
                    if (!action.Success && !action.Suppressed) return;
                    break;
            }
            if (edgeOutcome == "match") CheckType(output, PortTypes.Output(node), "output");
            foreach (var next in prepared.Config.Edges.Where(e => e.From == node.Id && e.Outcome == edgeOutcome)
                         .Select(e => prepared.Nodes[e.To]).OrderBy(n => n.Order).ThenBy(n => n.Id, StringComparer.Ordinal))
            {
                await VisitAsync(next, output, prepared, budget, previewSource, cancellationToken, upstream: node);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { Emit(node, signal, "error", FromException(ex)); }

        void EmitArrival()
        {
            if (upstream is not null)
                Trace(new(DateTimeOffset.UtcNow, signal.Id, upstream.Id, "routed", M("runtime.routed", ("node", node.Name)), signal, NextNodeId: node.Id));
            Emit(node, signal, "received", node.Name);
        }
    }

    private async ValueTask<ActionResult> ExecuteActionAsync(NodeDefinition node, Signal signal, Prepared prepared,
        NodeDefinition? previewSource, string moduleId, string connectionId, string operation,
        Dictionary<string, string> arguments, CancellationToken cancellationToken, long discoveryVersion)
    {
        if (!CanRoute(discoveryVersion)) return new(false, M("runtime.suppressed"), Suppressed: true);
        if (!_modules.TryGetValue(moduleId, out var module) || !module.Descriptor.Actions.Any(a => a.Id == operation))
            throw Error<InvalidOperationException>(M("runtime.actionUnknown", ("module", moduleId), ("operation", operation)));
        if (connectionId.Length > 0 && !prepared.Config.Connections.Any(c => c.Id == connectionId && c.Module == moduleId && c.Enabled))
            throw Error<InvalidOperationException>(M("runtime.actionConnection"));
        Emit(node, signal, "intended", $"{moduleId}/{operation}: {JsonSerializer.Serialize(arguments)}");
        if (prepared.Config.DryRun || previewSource is not null || node.PreviewOnly)
        {
            var reason = prepared.Config.DryRun ? M("runtime.dryRun")
                : previewSource is not null ? M("runtime.previewSource", ("node", previewSource.Name))
                : M("runtime.previewAction");
            Emit(node, signal, "suppressed", reason);
            return new(false, M("runtime.suppressed"), Suppressed: true);
        }
        var result = await module.ExecuteAsync(new(connectionId, operation, arguments, signal), cancellationToken);
        Trace(new(DateTimeOffset.UtcNow, signal.Id, node.Id, result.Success ? "completed" : "error", result.Message, signal, MessageCode: result.MessageCode, MessageArgs: result.MessageArgs));
        return result;
    }

    private void Emit(NodeDefinition node, Signal signal, string stage, LocalizedMessage message)
        => Trace(new(DateTimeOffset.UtcNow, signal.Id, node.Id, stage, message, signal));

    private void Emit(NodeDefinition node, Signal signal, string stage, string message)
        => Trace(new(DateTimeOffset.UtcNow, signal.Id, node.Id, stage, message, signal));

    private static bool MatchesSource(NodeDefinition source, Signal signal, bool sequence = false)
    {
        if (source.Module.Length > 0 && source.Module != signal.Module) return false;
        if (source.ConnectionId.Length > 0 && source.ConnectionId != signal.ConnectionId) return false;
        foreach (var field in new[] { "module", "source", "control", "phase" })
        {
            if (sequence && field is "control" or "phase") continue;
            if (source.Settings.TryGetValue(field, out var expected) && expected.Length > 0 && Field(signal, field) != expected) return false;
        }
        return true;
    }

    private static readonly HashSet<string> Fields = ["module", "connectionId", "source", "control", "phase", "value", "valueType"];
    private static string Field(Signal signal, string field) => field switch
    {
        "module" => signal.Module, "connectionId" => signal.ConnectionId, "source" => signal.Source,
        "control" => signal.Control, "phase" => signal.Phase, "value" => signal.Value, "valueType" => signal.ValueType,
        _ => throw Error<ArgumentException>(M("runtime.fieldUnknown", ("field", field)))
    };
    private static Dictionary<string, string> Expand(Dictionary<string, string> values, Signal signal)
        => values.ToDictionary(p => p.Key, p => Expand(p.Value, signal));
    private static string Expand(string value, Signal signal)
        => value.Replace("{value}", signal.Value).Replace("{control}", signal.Control).Replace("{phase}", signal.Phase);
    private static bool FiniteNumber(string value, out double number)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
    private static void CheckType(Signal signal, string declared, string direction)
    {
        if (signal.ValueType is not ("text" or "number" or "boolean"))
            throw Error<InvalidDataException>(M("runtime.valueType", ("type", signal.ValueType)));
        if (declared != "any" && declared != signal.ValueType)
            throw Error<InvalidDataException>(M("runtime.typeMismatch", ("direction", M("runtime.entity." + direction)), ("expected", declared), ("actual", signal.ValueType)));
    }
    private static Configuration Clone(Configuration configuration)
        => JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(configuration, Json), Json)!;
    private static bool ConnectionEqual(ConnectionDefinition left, ConnectionDefinition right)
        => left.Id == right.Id && left.Module == right.Module && left.Enabled == right.Enabled &&
            left.Settings.Count == right.Settings.Count && left.Settings.All(p => right.Settings.TryGetValue(p.Key, out var value) && value == p.Value);

    public async ValueTask DisposeAsync()
    {
        _input.Writer.TryComplete();
        _stop.Cancel();
        if (_worker is not null)
            try { await _worker.WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
        foreach (var module in _modules.Values)
            try { await module.DisposeAsync(); } catch { }
        _stop.Dispose();
    }

    private sealed class Prepared(Configuration config, Dictionary<string, ScriptRunner<Signal?>> scripts)
    {
        public Configuration Config { get; } = config;
        public Dictionary<string, ScriptRunner<Signal?>> Scripts { get; } = scripts;
        public Dictionary<string, NodeDefinition> Nodes { get; } = config.Nodes.ToDictionary(n => n.Id);
        public Dictionary<string, EventSequence> Sequences { get; } = config.Nodes
            .Where(n => n.Kind == "event" && n.Operation == "sequence")
            .ToDictionary(n => n.Id, n => EventSequence.Parse(n.Settings)!);
        public long Epoch { get; set; }
    }

    private sealed class VisitBudget(long discoveryVersion)
    {
        public long DiscoveryVersion { get; } = discoveryVersion;
        public int Visited;
    }
}
