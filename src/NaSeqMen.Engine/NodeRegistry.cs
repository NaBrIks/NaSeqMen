using System.Reflection;
using System.Text.Json;
using NaSeqMen.Core.Nodes;

namespace NaSeqMen.Engine;

public sealed record LoadedPluginInfo(
    string PluginId,
    string DisplayName,
    string Source,
    string? AssemblyPath,
    IReadOnlyList<string> NodeTypeIds,
    string? Error = null);

public sealed record NodeRegistrationInfo(
    NodeTypeDescriptor Descriptor,
    string PluginId,
    Assembly Assembly,
    string? AssemblyPath,
    string Source);

public sealed class NodeRegistry
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, INodePlugin> _pluginsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LoadedPluginInfo> _loadedPlugins = [];

    private sealed record Entry(NodeRegistrationInfo Info, Func<INode> Factory);

    public NodeTypeDescriptor Register(
        Func<INode> factory,
        string pluginId = "custom",
        Assembly? assembly = null,
        string? assemblyPath = null,
        string source = "built-in")
    {
        var probe = factory();
        var descriptor = probe.Descriptor;
        var resolvedAssembly = assembly ?? probe.GetType().Assembly;
        _ = probe.DisposeAsync();
        var info = new NodeRegistrationInfo(descriptor, pluginId, resolvedAssembly, assemblyPath, source);
        _entries[descriptor.TypeId] = new Entry(info, factory);
        return descriptor;
    }

    public void RegisterPlugin(INodePlugin plugin, string source = "built-in", string? assemblyPath = null)
    {
        var pluginAssembly = plugin.GetType().Assembly;
        var nodeIds = new List<string>();
        foreach (var factory in plugin.GetNodeFactories())
        {
            var desc = Register(factory, plugin.PluginId, pluginAssembly, assemblyPath, source);
            nodeIds.Add(desc.TypeId);
        }

        _pluginsById[plugin.PluginId] = plugin;
        _loadedPlugins.Add(new LoadedPluginInfo(
            plugin.PluginId,
            plugin.DisplayName,
            source,
            assemblyPath,
            nodeIds));
    }

    public void RecordPluginLoadError(string name, string assemblyPath, string error)
    {
        _loadedPlugins.Add(new LoadedPluginInfo(
            name,
            name,
            "plugin",
            assemblyPath,
            Array.Empty<string>(),
            error));
    }

    public void RegisterFromAssembly(Assembly assembly, string source = "plugin", string? assemblyPath = null)
    {
        var pluginType = typeof(INodePlugin);
        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract || type.IsInterface || !pluginType.IsAssignableFrom(type))
                continue;

            if (Activator.CreateInstance(type) is INodePlugin plugin)
            {
                RegisterPlugin(plugin, source, assemblyPath ?? assembly.Location);
            }
        }
    }

    public IReadOnlyList<LoadedPluginInfo> GetLoadedPlugins() => _loadedPlugins.ToArray();

    public async Task<(bool Found, object? Result)> InvokePluginActionAsync(
        string pluginId,
        string action,
        JsonElement? data,
        CancellationToken cancellationToken = default)
    {
        if (!_pluginsById.TryGetValue(pluginId, out var plugin))
            return (false, null);

        var result = await plugin.HandleActionAsync(action, data, cancellationToken).ConfigureAwait(false);
        return (true, result);
    }

    public IReadOnlyList<NodeTypeDescriptor> GetDescriptors()
        => _entries.Values
            .Select(e => e.Info.Descriptor)
            .OrderBy(d => d.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IReadOnlyList<NodeRegistrationInfo> GetRegistrations()
        => _entries.Values
            .Select(e => e.Info)
            .ToArray();

    public bool TryGetRegistration(string typeId, out NodeRegistrationInfo info)
    {
        if (_entries.TryGetValue(typeId, out var entry))
        {
            info = entry.Info;
            return true;
        }

        info = null!;
        return false;
    }

    public bool TryGetDescriptor(string typeId, out NodeTypeDescriptor descriptor)
    {
        if (_entries.TryGetValue(typeId, out var entry))
        {
            descriptor = entry.Info.Descriptor;
            return true;
        }

        descriptor = null!;
        return false;
    }

    public INode? CreateNode(string typeId)
        => _entries.TryGetValue(typeId, out var entry) ? entry.Factory() : null;
}
