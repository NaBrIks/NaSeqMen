// SPDX-License-Identifier: MIT
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static NaSeqMen.Contracts.MessageCatalog;

internal sealed class EditorWindowLauncher(string root, ILogger logger)
{
    private readonly string _executable = Path.Combine(root, "artifacts", "alpha", "desktop", "NaSeqMen.Desktop.exe");
    private readonly int _sessionId = GetSessionId();

    public void OpenOrActivate()
    {
        var processes = Process.GetProcessesByName("NaSeqMen.Desktop");
        try
        {
            var editorStarting = false;
            foreach (var process in processes)
            {
                if (!IsEditorInThisSession(process)) continue;
                var window = process.MainWindowHandle;
                if (window == 0)
                {
                    editorStarting = true;
                    continue;
                }

                if (IsIconic(window)) ShowWindowAsync(window, RestoreWindow);
                SetForegroundWindow(window);
                return;
            }

            // While the first window is starting, further clicks can arrive before it has a HWND.
            if (editorStarting) return;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }

        try
        {
            using var started = Process.Start(new ProcessStartInfo(_executable)
            {
                UseShellExecute = false,
                WorkingDirectory = root,
                ArgumentList = { "--root", root }
            });
        }
        catch (Win32Exception error)
        {
            logger.LogWarning(error, "{Message}", M("executor.editorStartFailed", ("path", _executable)).Message);
        }
    }

    private bool IsEditorInThisSession(Process process)
    {
        try
        {
            return process.SessionId == _sessionId &&
                string.Equals(process.MainModule?.FileName, _executable, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            // The process may have exited or may belong to another account.
            return false;
        }
    }

    private static int GetSessionId()
    {
        using var current = Process.GetCurrentProcess();
        return current.SessionId;
    }

    private const int RestoreWindow = 9;

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint window, int command);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
}
