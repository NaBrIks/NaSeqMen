using NaSeqMen.Core.Nodes;

namespace NaSeqMen.Nodes.Controls;

public sealed class ControlsNodesPlugin : INodePlugin
{
    public string PluginId => "naseqmen.controls";
    public string DisplayName => "Virtual Controls & Meters";

    public IEnumerable<Func<INode>> GetNodeFactories()
    {
        yield return () => new VirtualButtonNode();
        yield return () => new VirtualSliderNode();
        yield return () => new VuMeterNode();
    }
}
