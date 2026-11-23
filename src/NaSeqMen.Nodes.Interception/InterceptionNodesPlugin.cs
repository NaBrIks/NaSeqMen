using NaSeqMen.Core.Nodes;

namespace NaSeqMen.Nodes.Interception;

/// <summary>
/// External node plugin registering universal Interception hardware input and remapping nodes.
/// </summary>
public sealed class InterceptionNodesPlugin : INodePlugin
{
    public string PluginId => "naseqmen.interception";
    public string DisplayName => "Interception Hardware Input Nodes";

    public IEnumerable<Func<INode>> GetNodeFactories()
    {
        yield return () => new InterceptionDeviceNode();
        yield return () => new InterceptionHubNode();
        yield return () => new HotkeySwitchNode();
        yield return () => new InputProbeNode();
        yield return () => new StrokeActionNode();
        yield return () => new ChordMatcherNode();
    }
}
