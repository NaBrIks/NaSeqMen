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
        Application.Run(new DesktopWindow(Path.GetFullPath(root), Port("--port", 17964), Port("--executor-port", 17963)));

        int Port(string name, int fallback)
        {
            var index = Array.IndexOf(args, name);
            if (index < 0) return fallback;
            if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var value) || value is < 1024 or > 65535)
                throw new ArgumentException(name);
            return value;
        }
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) && Directory.Exists(Path.Combine(directory.FullName, "src")))
                return directory.FullName;
        return Environment.CurrentDirectory;
    }
}
