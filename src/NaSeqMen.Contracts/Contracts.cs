// SPDX-License-Identifier: MIT
namespace NaSeqMen.Contracts;

public sealed record Signal
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Time { get; init; } = DateTimeOffset.UtcNow;
    public string Module { get; init; } = "test";
    public string ConnectionId { get; init; } = "";
    public string Source { get; init; } = "";
    public string Control { get; init; } = "";
    public string Phase { get; init; } = "change";
    public string ValueType { get; init; } = "text";
    public string Value { get; init; } = "";
    public ModifierState? Modifiers { get; init; }
    public bool IsRepeat { get; init; }
    public long InputGeneration { get; init; }
    public string? OriginalControl { get; init; }
}

public sealed class Configuration
{
    public int Version { get; set; } = 1;
    public bool DryRun { get; set; } = true;
    public List<ConnectionDefinition> Connections { get; set; } = [];
    public List<NodeDefinition> Nodes { get; set; } = [];
    public List<EdgeDefinition> Edges { get; set; } = [];
    public List<InputMapping> Mappings { get; set; } = [];
}

public sealed class ConnectionDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Module { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public Dictionary<string, string> Settings { get; set; } = [];
}

public sealed class NodeDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    // source, event, condition, transform, script, action
    public string Kind { get; set; } = "source";
    public string Module { get; set; } = "";
    public string ConnectionId { get; set; } = "";
    public string Operation { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool PreviewOnly { get; set; }
    public int Order { get; set; }
    public string InputType { get; set; } = "any";
    public string OutputType { get; set; } = "any";
    public Dictionary<string, string> Settings { get; set; } = [];
}

public sealed class EdgeDefinition
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Outcome { get; set; } = "match";
}

public sealed class InputMapping
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Source { get; set; } = "";
    public string Control { get; set; } = "";
    public string Name { get; set; } = "";
    // observe and map are supported by passive Raw Input; suppress requires another backend.
    public string Policy { get; set; } = "observe";
}

[method: System.Text.Json.Serialization.JsonConstructor]
public sealed record DeviceDescriptor(string Id, string Name, string Kind, string HardwareId, bool CanSuppress = false, string? NameKey = null)
{
    public string? PhysicalDeviceId { get; init; }
    public string? PhysicalDeviceName { get; init; }

    public DeviceDescriptor(string id, LocalizedMessage name, string kind, string hardwareId, bool canSuppress = false)
        : this(id, name.Message, kind, hardwareId, canSuppress, name.Code) { }
}
[method: System.Text.Json.Serialization.JsonConstructor]
public sealed record OperationDescriptor(string Id, string Name, string[] Parameters, string? NameKey = null)
{
    public string? DestinationNameKey { get; init; }

    public OperationDescriptor(string id, LocalizedMessage name, string[] parameters) : this(id, name.Message, parameters, name.Code) { }
}
[method: System.Text.Json.Serialization.JsonConstructor]
public sealed record ModuleDescriptor(string Id, string Name, string Version, bool HasInput,
    bool CanSuppress, OperationDescriptor[] Actions, OperationDescriptor[] Conditions, string? NameKey = null)
{
    public ModuleDescriptor(string id, LocalizedMessage name, string version, bool hasInput, bool canSuppress, OperationDescriptor[] actions, OperationDescriptor[] conditions)
        : this(id, name.Message, version, hasInput, canSuppress, actions, conditions, name.Code) { }
}
public sealed record ActionRequest(string ConnectionId, string Operation, Dictionary<string, string> Arguments, Signal Input);
public sealed record ConditionRequest(string ConnectionId, string Operation, Dictionary<string, string> Arguments, Signal Input);
[method: System.Text.Json.Serialization.JsonConstructor]
public sealed record ActionResult(bool Success, string Message, bool Suppressed = false, string? MessageCode = null, Dictionary<string, object?>? MessageArgs = null)
{
    public ActionResult(bool Success, LocalizedMessage Message, bool Suppressed = false)
        : this(Success, Message.Message, Suppressed, Message.Code, Message.Args) { }
}
[method: System.Text.Json.Serialization.JsonConstructor]
public sealed record Diagnostic(DateTimeOffset Time, string SignalId, string NodeId, string Stage, string Message, Signal? Signal = null, string? NextNodeId = null, string? MessageCode = null, Dictionary<string, object?>? MessageArgs = null)
{
    public Diagnostic(DateTimeOffset Time, string SignalId, string NodeId, string Stage, LocalizedMessage Message, Signal? Signal = null, string? NextNodeId = null)
        : this(Time, SignalId, NodeId, Stage, Message.Message, Signal, NextNodeId, Message.Code, Message.Args) { }
}
public sealed record ValidationResult(bool Valid, string[] Errors, LocalizedMessage[]? Issues = null)
{
    public static ValidationResult FromIssues(IEnumerable<LocalizedMessage> issues)
    {
        var values = issues.ToArray();
        return new(values.Length == 0, values.Select(value => value.Message).ToArray(), values);
    }
}

public static class PortTypes
{
    public static readonly string[] Supported = ["any", "text", "number", "boolean"];
    public static string Input(NodeDefinition node) => node.Kind == "transform" && node.Operation == "scale" ? "number" : node.InputType;
    public static string Output(NodeDefinition node) => node.Kind == "transform"
        ? node.Operation == "scale" ? "number" : node.Operation == "text" ? "text" : node.OutputType
        : node.Kind == "event" && node.Operation == "sequence" ? "number" : node.OutputType;
    public static bool Compatible(NodeDefinition source, NodeDefinition target)
        => Compatible(Output(source), Input(target));
    public static bool Compatible(string output, string input)
        => output == "any" || input == "any" || output == input;
}

public interface IModuleHost
{
    ValueTask PublishAsync(Signal signal, CancellationToken cancellationToken = default);
    void Trace(Diagnostic diagnostic);
}

public interface INaModule : IAsyncDisposable
{
    ModuleDescriptor Descriptor { get; }
    ValueTask InitializeAsync(IModuleHost host, CancellationToken cancellationToken = default);
    ValueTask ConnectAsync(ConnectionDefinition connection, CancellationToken cancellationToken = default);
    ValueTask DisconnectAsync(string connectionId, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<DeviceDescriptor>> GetDevicesAsync(CancellationToken cancellationToken = default);
    ValueTask<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken = default);
    ValueTask<bool> EvaluateAsync(ConditionRequest request, CancellationToken cancellationToken = default);
}

public abstract class NaModule : INaModule
{
    protected IModuleHost Host { get; private set; } = null!;
    public abstract ModuleDescriptor Descriptor { get; }
    public virtual ValueTask InitializeAsync(IModuleHost host, CancellationToken cancellationToken = default)
    { Host = host; return ValueTask.CompletedTask; }
    public virtual ValueTask ConnectAsync(ConnectionDefinition connection, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public virtual ValueTask DisconnectAsync(string connectionId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public virtual ValueTask<IReadOnlyList<DeviceDescriptor>> GetDevicesAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<DeviceDescriptor>>([]);
    public virtual ValueTask<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new ActionResult(false, MessageCatalog.M("module.unsupportedAction")));
    public virtual ValueTask<bool> EvaluateAsync(ConditionRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
