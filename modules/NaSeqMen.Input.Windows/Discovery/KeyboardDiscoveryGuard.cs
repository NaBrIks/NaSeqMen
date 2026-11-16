// SPDX-License-Identifier: MIT
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using NaSeqMen.Contracts;

namespace NaSeqMen.Input.Windows;

internal sealed class KeyboardDiscoveryGuard(nint foreground, Action<RawSample> capture) : IAsyncDisposable
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _expires;
    private bool _started;
    private bool _stopping;
    public bool Filtering => !Volatile.Read(ref _stopping) && Environment.TickCount64 < Interlocked.Read(ref _expires) &&
        HasForeground && !_finished.Task.IsCompleted;
    public bool HasForeground => foreground != 0 && GetForegroundWindow() == foreground;

    public bool Renew()
    {
        if (!HasForeground || _finished.Task.IsCompleted) return false;
        Interlocked.Exchange(ref _expires, Environment.TickCount64 + DiscoveryCaptureRequest.LeaseMilliseconds);
        return true;
    }

    public async Task StartAsync()
    {
        if (!Renew()) throw new InvalidOperationException();
        var thread = new Thread(Run) { IsBackground = true, Name = "NaSeqMen discovery keyboard" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        _started = true;
        await _ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private void Run()
    {
        nint hook = 0;
        var held = new HashSet<(int Scan, bool Extended)>();
        HookCallback callback = HandleKey;
        try
        {
            using var context = new ApplicationContext();
            hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
            if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            using var timer = new System.Windows.Forms.Timer { Interval = 100 };
            timer.Tick += (_, _) =>
            {
                if (Volatile.Read(ref _stopping) || Environment.TickCount64 >= Interlocked.Read(ref _expires))
                    context.ExitThread();
            };
            timer.Start();
            _ready.TrySetResult();
            Application.Run(context);
        }
        catch (Exception error) { _ready.TrySetException(error); }
        finally
        {
            if (hook != 0) UnhookWindowsHookEx(hook);
            GC.KeepAlive(callback);
            _finished.TrySetResult();
        }

        nint HandleKey(int code, nint message, nint data)
        {
            if (code < 0 || !Filtering)
                return CallNextHookEx(0, code, message, data);

            var scan = Marshal.ReadInt32(data, 4);
            var flags = Marshal.ReadInt32(data, 8);
            var key = (scan, (flags & 1) != 0);
            var up = (flags & 0x80) != 0;
            if (up)
            {
                // Release keys that were already pressed before Discover to avoid stuck modifiers.
                if (!held.Remove(key)) return CallNextHookEx(0, code, message, data);
            }
            else held.Add(key);

            if (scan is > 0 and < 256)
                capture(new(DiscoveryCaptureRequest.Keyboard, "keyboard", "", up ? "up" : "down",
                    ScanCode: scan, Flags: (up ? 1 : 0) | (key.Item2 ? 2 : 0)));
            return 1;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_started) return;
        Volatile.Write(ref _stopping, true);
        await _finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    public static nint ForegroundEditor()
    {
        var window = GetForegroundWindow();
        if (window == 0 || GetWindowThreadProcessId(window, out var pid) == 0) return 0;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName.ToLowerInvariant() is "naseqmen.desktop" or "chrome" or "msedge" or "firefox" ? window : 0;
        }
        catch (ArgumentException) { return 0; }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);
    private delegate nint HookCallback(int code, nint message, nint data);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int type, HookCallback callback, nint module, uint thread);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
}
