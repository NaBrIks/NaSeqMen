using System.Text.Json;

namespace NaSeqMen.Core.Graph;

public sealed class WorkspaceDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "NaSeqMen Workspace";
    public List<FlowTab> Flows { get; set; } = [];
}

public sealed class FlowTab
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "Flow 1";
    public bool Enabled { get; set; } = true;
    public List<NodeInstance> Nodes { get; set; } = [];
    public List<EdgeConnection> Edges { get; set; } = [];
}

public sealed class NodeInstance
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string TypeId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public bool Collapsed { get; set; }
    public bool Enabled { get; set; } = true;
    public Dictionary<string, JsonElement> Parameters { get; set; } = new(StringComparer.Ordinal);
}

public sealed class EdgeConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string FromNodeId { get; set; } = string.Empty;
    public string FromPortId { get; set; } = string.Empty;
    public string ToNodeId { get; set; } = string.Empty;
    public string ToPortId { get; set; } = string.Empty;
}
