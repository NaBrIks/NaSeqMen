using System.Text.Json;
using NaSeqMen.Core.Messages;

namespace NaSeqMen.Core.Nodes;

/// <summary>
/// Core contract implemented by every node (built-in or external plugin).
/// </summary>
public interface INode : IAsyncDisposable
{
    NodeTypeDescriptor Descriptor { get; }

    Task InitializeAsync(NodeContext context, CancellationToken cancellationToken);

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    Task OnInputAsync(string inputPortId, FlowMessage message, CancellationToken cancellationToken);

    Task OnManualTriggerAsync(FlowMessage? message, CancellationToken cancellationToken);

    Task OnInteractAsync(string action, JsonElement? data, CancellationToken cancellationToken);
}

/// <summary>
/// Optional plugin entry point for built-in and dynamically loaded node assemblies.
/// </summary>
public interface INodePlugin
{
    string PluginId { get; }
    string DisplayName { get; }
    IEnumerable<Func<INode>> GetNodeFactories();

    Task<object?> HandleActionAsync(string action, JsonElement? data, CancellationToken cancellationToken)
        => Task.FromResult<object?>(null);
}
