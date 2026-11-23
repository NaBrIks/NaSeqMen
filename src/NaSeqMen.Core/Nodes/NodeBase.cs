using System.Text.Json;
using NaSeqMen.Core.Messages;

namespace NaSeqMen.Core.Nodes;

/// <summary>
/// Abstract base class for nodes. Implements no concrete domain behavior,
/// providing only lifecycle, context, and message emission helpers.
/// </summary>
public abstract class NodeBase : INode
{
    protected NodeContext Context { get; private set; } = null!;

    public abstract NodeTypeDescriptor Descriptor { get; }

    public virtual Task InitializeAsync(NodeContext context, CancellationToken cancellationToken)
    {
        Context = context;
        return Task.CompletedTask;
    }

    public virtual Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public virtual Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public virtual Task OnInputAsync(string inputPortId, FlowMessage message, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public virtual Task OnManualTriggerAsync(FlowMessage? message, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public virtual Task OnInteractAsync(string action, JsonElement? data, CancellationToken cancellationToken)
        => Task.CompletedTask;

    protected Task EmitAsync(string outputPortId, FlowMessage message, CancellationToken cancellationToken = default)
        => Context.EmitAsync(outputPortId, message, cancellationToken);

    protected void PublishWidgetState(object state) => Context.PublishWidgetState(state);

    protected string GetString(string key, string defaultValue = "") => Context.GetString(key, defaultValue);
    protected bool GetBool(string key, bool defaultValue = false) => Context.GetBool(key, defaultValue);
    protected double GetNumber(string key, double defaultValue = 0) => Context.GetNumber(key, defaultValue);

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
