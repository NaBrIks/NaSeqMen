using System.Reflection;
using NaSeqMen.Core.Nodes;

namespace NaSeqMen.Engine;

public sealed class NodeRegistry
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private sealed record Entry(NodeTypeDescriptor Descriptor, Func<INode> Factory);

    public void Register(Func<INode> factory)
    {
        var probe = factory();
        var descriptor = probe.Descriptor;
        _ = probe.DisposeAsync();
        _entries[descriptor.TypeId] = new Entry(descriptor, factory);
    }

    public void RegisterPlugin(INodePlugin plugin)
    {
        foreach (var factory in plugin.GetNodeFactories())
        {
            Register(factory);
        }
    }

    public void RegisterFromAssembly(Assembly assembly)
    {
        var pluginType = typeof(INodePlugin);
        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract || type.IsInterface || !pluginType.IsAssignableFrom(type))
                continue;

            if (Activator.CreateInstance(type) is INodePlugin plugin)
            {
                RegisterPlugin(plugin);
            }
        }
    }

    public IReadOnlyList<NodeTypeDescriptor> GetDescriptors()
        => _entries.Values
            .Select(e => e.Descriptor)
            .OrderBy(d => d.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public bool TryGetDescriptor(string typeId, out NodeTypeDescriptor descriptor)
    {
        if (_entries.TryGetValue(typeId, out var entry))
        {
            descriptor = entry.Descriptor;
            return true;
        }

        descriptor = null!;
        return false;
    }

    public INode? CreateNode(string typeId)
        => _entries.TryGetValue(typeId, out var entry) ? entry.Factory() : null;
}
