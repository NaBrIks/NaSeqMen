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
}

/// <summary>
/// Optional plugin entry point for external node assemblies.
/// </summary>
public interface INodePlugin
{
    string PluginId { get; }
    string DisplayName { get; }
    IEnumerable<Func<INode>> GetNodeFactories();
}
