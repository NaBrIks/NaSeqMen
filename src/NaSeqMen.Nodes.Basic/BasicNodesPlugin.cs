using NaSeqMen.Core.Nodes;

namespace NaSeqMen.Nodes.Basic;

public sealed class BasicNodesPlugin : INodePlugin
{
    public string PluginId => "naseqmen.basic";
    public string DisplayName => "Basic Starter Nodes";

    public IEnumerable<Func<INode>> GetNodeFactories()
    {
        yield return () => new KeyboardInputNode();
        yield return () => new ToUpperNode();
        yield return () => new KeyboardOutputNode();
        yield return () => new DebugNode();
    }
}
