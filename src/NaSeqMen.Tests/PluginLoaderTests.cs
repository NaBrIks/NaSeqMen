using NaSeqMen.Engine;

namespace NaSeqMen.Tests;

internal static class PluginLoaderTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> GetTests() =>
    [
        ("1. Dynamic PluginLoader discovers Obs, Interception & Windows from Host plugins/ folder", TestDynamicPluginDiscoveryAsync),
    ];

    private static async Task TestDynamicPluginDiscoveryAsync()
    {
        // Locate NaSeqMen.Host build output next to NaSeqMen.Tests
        var hostBin = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "NaSeqMen.Host", "bin", "Debug", "net11.0-windows"));

        Assert.True(Directory.Exists(hostBin), $"Expected Host output directory at {hostBin}");

        // Verify external plugin DLLs are NOT in Host root folder
        foreach (var externalDll in new[] { "NaSeqMen.Nodes.Obs.dll", "NaSeqMen.Nodes.Interception.dll", "NaSeqMen.Nodes.Windows.dll" })
        {
            var rootPath = Path.Combine(hostBin, externalDll);
            Assert.True(!File.Exists(rootPath), $"External plugin DLL must NOT be copied to Host root: {rootPath}");
        }

        var pluginsDir = Path.Combine(hostBin, "plugins");
        Assert.True(Directory.Exists(pluginsDir), $"Expected plugins/ directory at {pluginsDir}");

        // Verify NaSeqMen.Core.dll is NOT duplicated inside plugin subfolders
        foreach (var subDir in Directory.GetDirectories(pluginsDir))
        {
            Assert.True(!File.Exists(Path.Combine(subDir, "NaSeqMen.Core.dll")),
                $"NaSeqMen.Core.dll should not be duplicated inside {subDir}");
        }

        var registry = new NodeRegistry();
        var loaded = PluginLoader.LoadFromDirectory(pluginsDir, registry);
        Assert.True(loaded >= 3, $"Expected at least 3 dynamically loaded plugins, got {loaded}");

        var plugins = registry.GetLoadedPlugins();
        Assert.True(plugins.All(p => p.Error is null),
            "Unexpected plugin load error: " + string.Join("; ", plugins.Where(p => p.Error is not null).Select(p => p.Error)));

        Assert.True(registry.TryGetDescriptor("obs.studio", out _), "obs.studio must load dynamically");
        Assert.True(registry.TryGetDescriptor("interception.hub", out _), "interception.hub must load dynamically");
        Assert.True(registry.TryGetDescriptor("interception.hotkey-switch", out _), "interception.hotkey-switch must load dynamically");
        Assert.True(registry.TryGetDescriptor("interception.probe", out _), "interception.probe must load dynamically");
        Assert.True(registry.TryGetDescriptor("win.window-filter", out _), "win.window-filter must load dynamically");

        // Verify generic plugin action invocation (no compile-time reference needed!)
        var (found, result) = await registry.InvokePluginActionAsync("naseqmen.interception", "devices", null);
        Assert.True(found, "naseqmen.interception plugin must handle generic 'devices' action");
        Assert.NotNull(result, "devices action should return a snapshot object");
    }
}
