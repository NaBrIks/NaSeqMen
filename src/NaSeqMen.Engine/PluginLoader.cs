using System.Reflection;
using System.Runtime.Loader;
using NaSeqMen.Core.Nodes;

namespace NaSeqMen.Engine;

/// <summary>
/// Dynamically discovers and loads external <see cref="INodePlugin"/> assemblies from a plugins folder
/// using isolated <see cref="AssemblyLoadContext"/> instances sharing <c>NaSeqMen.Core</c>.
/// </summary>
public static class PluginLoader
{
    private static readonly string CoreAssemblyName = typeof(INodePlugin).Assembly.GetName().Name ?? "NaSeqMen.Core";
    private static readonly List<PluginLoadContext> Contexts = [];

    public static int LoadFromDirectory(string pluginsDirectory, NodeRegistry registry)
    {
        if (string.IsNullOrWhiteSpace(pluginsDirectory) || !Directory.Exists(pluginsDirectory))
            return 0;

        var candidateDlls = DiscoverPluginDlls(pluginsDirectory);
        var loadedCount = 0;

        foreach (var dllPath in candidateDlls)
        {
            var fullPath = Path.GetFullPath(dllPath);
            var pluginName = Path.GetFileNameWithoutExtension(fullPath);

            try
            {
                var loadCtx = new PluginLoadContext(fullPath);
                var assembly = loadCtx.LoadFromAssemblyPath(fullPath);
                var before = registry.GetLoadedPlugins().Count;
                registry.RegisterFromAssembly(assembly, source: "plugin", assemblyPath: fullPath);
                var added = registry.GetLoadedPlugins().Count - before;

                if (added > 0)
                {
                    Contexts.Add(loadCtx);
                    loadedCount += added;
                }
            }
            catch (Exception ex)
            {
                registry.RecordPluginLoadError(pluginName, fullPath, ex.Message);
            }
        }

        return loadedCount;
    }

    private static IReadOnlyList<string> DiscoverPluginDlls(string pluginsDirectory)
    {
        var results = new List<string>();

        // 1. Subfolder convention: plugins/<PluginName>/<PluginName>.dll (preferred, isolates deps)
        foreach (var dir in Directory.GetDirectories(pluginsDirectory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var folderName = Path.GetFileName(dir);
            var matchingDll = Path.Combine(dir, folderName + ".dll");
            if (File.Exists(matchingDll))
            {
                results.Add(matchingDll);
                continue;
            }

            // Fallback: any NaSeqMen.*.dll inside the subfolder
            var fallback = Directory.GetFiles(dir, "*.dll")
                .Where(f => !Path.GetFileNameWithoutExtension(f).Equals(CoreAssemblyName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (fallback is not null)
                results.Add(fallback);
        }

        // 2. Flat DLLs directly inside plugins/
        foreach (var dll in Directory.GetFiles(pluginsDirectory, "*.dll").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(dll);
            if (!name.Equals(CoreAssemblyName, StringComparison.OrdinalIgnoreCase))
                results.Add(dll);
        }

        return results;
    }

    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string pluginPath)
            : base(name: Path.GetFileNameWithoutExtension(pluginPath), isCollectible: false)
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Share NaSeqMen.Core from the default host context so INodePlugin/INode/FlowMessage types match.
            if (string.Equals(assemblyName.Name, CoreAssemblyName, StringComparison.OrdinalIgnoreCase))
                return null;

            var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            return assemblyPath is not null ? LoadFromAssemblyPath(assemblyPath) : null;
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return libraryPath is not null ? LoadUnmanagedDllFromPath(libraryPath) : nint.Zero;
        }
    }
}
