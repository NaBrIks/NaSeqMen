using NaSeqMen.Core.Nodes;

namespace NaSeqMen.Nodes.Windows;

public sealed class WindowsNodesPlugin : INodePlugin
{
    public string PluginId => "naseqmen.windows";
    public string DisplayName => "Windows Window Management Nodes";

    public IEnumerable<Func<INode>> GetNodeFactories()
    {
        yield return () => new WindowFilterNode();
        yield return () => new WindowTargetNode();
        yield return () => new WindowPostTextNode();
    }
}
