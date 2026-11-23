using NaSeqMen.Core.Nodes;

namespace NaSeqMen.Nodes.Obs;

public sealed class ObsNodesPlugin : INodePlugin
{
    public string PluginId => "naseqmen.obs";
    public string DisplayName => "OBS Studio Nodes";

    public IEnumerable<Func<INode>> GetNodeFactories()
    {
        yield return () => new ObsStudioNode();
    }
}
