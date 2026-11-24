using System.Text.Json;
using NaSeqMen.Core.Nodes;

namespace NaSeqMen.Nodes.Interception;

public sealed class InterceptionNodesPlugin : INodePlugin
{
    public string PluginId => "naseqmen.interception";
    public string DisplayName => "Interception Driver Nodes";

    public IEnumerable<Func<INode>> GetNodeFactories()
    {
        yield return () => new InterceptionDeviceNode();
        yield return () => new InterceptionHubNode();
        yield return () => new HotkeySwitchNode();
        yield return () => new InputProbeNode();
        yield return () => new StrokeActionNode();
        yield return () => new ChordMatcherNode();
    }

    public Task<object?> HandleActionAsync(string action, JsonElement? data, CancellationToken cancellationToken)
    {
        switch (action.ToLowerInvariant())
        {
            case "devices":
            {
                string? dllPath = null;
                if (data is { ValueKind: JsonValueKind.Object } obj &&
                    obj.TryGetProperty("dllPath", out var p) &&
                    p.ValueKind == JsonValueKind.String)
                {
                    var s = p.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        dllPath = s;
                }

                var snapshot = InterceptionDriverService.Instance.GetSnapshot(dllPath, refreshDevices: true);
                return Task.FromResult<object?>(snapshot);
            }

            case "panic-release":
                InterceptionDriverService.Instance.EmergencyReleaseAllFilters();
                return Task.FromResult<object?>(new { ok = true });

            case "reset-panic":
                InterceptionDriverService.Instance.ResetPanic();
                return Task.FromResult<object?>(new { ok = true });

            default:
                return Task.FromResult<object?>(null);
        }
    }
}
