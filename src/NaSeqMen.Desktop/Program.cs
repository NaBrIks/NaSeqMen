// SPDX-License-Identifier: MIT
namespace NaSeqMen.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var rootIndex = Array.IndexOf(args, "--root");
        var root = rootIndex >= 0 && rootIndex + 1 < args.Length ? args[rootIndex + 1] : FindRoot();
        Application.Run(new DesktopWindow(Path.GetFullPath(root)));
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) && Directory.Exists(Path.Combine(directory.FullName, "src")))
                return directory.FullName;
        return Environment.CurrentDirectory;
    }
}
