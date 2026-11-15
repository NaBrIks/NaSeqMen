// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Runtime.Loader;
using NaSeqMen.Contracts;
using static NaSeqMen.Contracts.MessageCatalog;

namespace NaSeqMen.Runtime;

public static class ModuleLoader
{
    public static IReadOnlyList<INaModule> Load(string directory, IReadOnlyList<string>? selectedAssemblies = null)
    {
        var contract = typeof(INaModule).Assembly.GetName().Name + ".dll";
        var entries = selectedAssemblies ?? (Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "NaSeqMen.*.dll").Select(Path.GetFileName).OfType<string>()
                .Where(name => !string.Equals(name, contract, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToArray()
            : []);
        var modules = new List<INaModule>();
        foreach (var name in entries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, contract, StringComparison.OrdinalIgnoreCase))
                throw Error<InvalidDataException>(M("executor.pluginName", ("file", name ?? "")));
            var path = Path.GetFullPath(Path.Combine(directory, name));
            if (!File.Exists(path)) throw Error<FileNotFoundException>(M("executor.pluginMissing", ("file", name)));
            var assembly = new PluginLoadContext(path).LoadFromAssemblyPath(path);
            var previousCount = modules.Count;
            foreach (var type in assembly.GetTypes().Where(t => !t.IsAbstract && !t.IsInterface && typeof(INaModule).IsAssignableFrom(t)))
                if (Activator.CreateInstance(type) is INaModule module) modules.Add(module);
            if (selectedAssemblies is not null && previousCount == modules.Count)
                throw Error<InvalidDataException>(M("executor.pluginNoModule", ("file", name)));
        }
        return modules;
    }

    private sealed class PluginLoadContext(string path) : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver = new(path);

        protected override Assembly? Load(AssemblyName name)
        {
            var contract = typeof(INaModule).Assembly;
            if (name.Name == contract.GetName().Name) return contract;
            var resolvedPath = _resolver.ResolveAssemblyToPath(name);
            return resolvedPath is null ? null : LoadFromAssemblyPath(resolvedPath);
        }

        protected override nint LoadUnmanagedDll(string name)
        {
            var resolvedPath = _resolver.ResolveUnmanagedDllToPath(name);
            return resolvedPath is null ? 0 : LoadUnmanagedDllFromPath(resolvedPath);
        }
    }
}
