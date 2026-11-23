using NaSeqMen.Core.Nodes;

namespace NaSeqMen.Nodes.Scripting;

public sealed class ScriptingNodesPlugin : INodePlugin
{
    public string PluginId => "naseqmen.scripting";
    public string DisplayName => "C# Scripting";

    public IEnumerable<Func<INode>> GetNodeFactories()
    {
        yield return () => new CSharpScriptNode();
    }
}
