using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NaSeqMen.Nodes.Windows;

internal sealed record WindowInfo(
    nint Hwnd,
    uint ProcessId,
    string ProcessName,
    string Title,
    string ClassName);

internal static class WindowHelper
{
    private const int SW_RESTORE = 9;
    private const uint WM_CHAR = 0x0102;
    private static readonly string[] DefaultEditClasses =
    [
        "RichEditD2DPT",
        "Edit",
        "Scintilla",
        "RichEdit20W",
        "RICHEDIT50W",
    ];

    public static WindowInfo? GetForegroundWindowInfo()
    {
        var hwnd = GetForegroundWindow();
        return hwnd == 0 ? null : ReadWindowInfo(hwnd);
    }

    public static WindowInfo? FindWindow(string? processFilter, string? titleFilter)
    {
        var procNorm = NormalizeProcessName(processFilter);
        var titleNorm = string.IsNullOrWhiteSpace(titleFilter) ? null : titleFilter.Trim();

        if (procNorm is null && titleNorm is null)
            return null;

        WindowInfo? match = null;
        var currentPid = (uint)Environment.ProcessId;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
                return true;

            var info = ReadWindowInfo(hwnd);
            if (info is null || info.ProcessId == currentPid)
                return true;

            if (Matches(info, procNorm, titleNorm))
            {
                match = info;
                return false; // stop enumeration
            }

            return true;
        }, 0);

        return match;
    }

    public static bool Matches(WindowInfo info, string? processFilter, string? titleFilter)
    {
        var procNorm = NormalizeProcessName(processFilter);
        var titleNorm = string.IsNullOrWhiteSpace(titleFilter) ? null : titleFilter.Trim();

        if (procNorm is not null)
        {
            var actualProc = NormalizeProcessName(info.ProcessName) ?? string.Empty;
            if (!actualProc.Equals(procNorm, StringComparison.OrdinalIgnoreCase) &&
                !actualProc.Contains(procNorm, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (titleNorm is not null)
        {
            if (!info.Title.Contains(titleNorm, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return procNorm is not null || titleNorm is not null;
    }

    public static bool FocusWindow(nint hwnd)
    {
        if (hwnd == 0 || !IsWindow(hwnd))
            return false;

        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SW_RESTORE);
        }

        var fg = GetForegroundWindow();
        if (fg == hwnd)
            return true;

        var fgThread = fg != 0 ? GetWindowThreadProcessId(fg, out _) : 0;
        var targetThread = GetWindowThreadProcessId(hwnd, out _);
        var currentThread = GetCurrentThreadId();

        bool attachedFg = false;
        bool attachedTarget = false;
        try
        {
            if (fgThread != 0 && fgThread != currentThread)
                attachedFg = AttachThreadInput(currentThread, fgThread, true);
            if (targetThread != 0 && targetThread != currentThread)
                attachedTarget = AttachThreadInput(currentThread, targetThread, true);

            BringWindowToTop(hwnd);
            return SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attachedTarget)
                AttachThreadInput(currentThread, targetThread, false);
            if (attachedFg)
                AttachThreadInput(currentThread, fgThread, false);
        }
    }

    public static bool PostText(nint topLevelHwnd, string text, string? childClassFilter)
    {
        if (topLevelHwnd == 0 || !IsWindow(topLevelHwnd) || string.IsNullOrEmpty(text))
            return false;

        var targetHwnd = ResolveTextTargetHwnd(topLevelHwnd, childClassFilter);
        foreach (var ch in text)
        {
            if (ch == '\r')
                continue;
            PostMessageW(targetHwnd, WM_CHAR, (nuint)ch, 0);
        }

        return true;
    }

    private static nint ResolveTextTargetHwnd(nint parentHwnd, string? childClassFilter)
    {
        var requestedClass = string.IsNullOrWhiteSpace(childClassFilter) ? null : childClassFilter.Trim();
        nint found = 0;

        EnumChildWindows(parentHwnd, (child, _) =>
        {
            var cls = GetClassNameString(child);
            if (requestedClass is not null)
            {
                if (cls.Equals(requestedClass, StringComparison.OrdinalIgnoreCase) ||
                    cls.Contains(requestedClass, StringComparison.OrdinalIgnoreCase))
                {
                    found = child;
                    return false;
                }
                return true;
            }

            foreach (var known in DefaultEditClasses)
            {
                if (cls.Equals(known, StringComparison.OrdinalIgnoreCase))
                {
                    found = child;
                    return false;
                }
            }

            return true;
        }, 0);

        return found != 0 ? found : parentHwnd;
    }

    private static WindowInfo? ReadWindowInfo(nint hwnd)
    {
        if (hwnd == 0)
            return null;

        _ = GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0)
            return null;

        string procName = string.Empty;
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            procName = proc.ProcessName;
        }
        catch
        {
            // Process may have exited or denied access
        }

        var title = GetWindowTitleString(hwnd);
        var cls = GetClassNameString(hwnd);
        return new WindowInfo(hwnd, pid, procName, title, cls);
    }

    private static string? NormalizeProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }

    private static string GetWindowTitleString(nint hwnd)
    {
        var len = GetWindowTextLengthW(hwnd);
        if (len <= 0)
            return string.Empty;

        var sb = new StringBuilder(len + 1);
        _ = GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetClassNameString(nint hwnd)
    {
        var sb = new StringBuilder(128);
        var len = GetClassNameW(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString(0, len) : string.Empty;
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint hWndParent, EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint hWnd, [Out] StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint hWnd, [Out] StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessageW(nint hWnd, uint msg, nuint wParam, nint lParam);
}
