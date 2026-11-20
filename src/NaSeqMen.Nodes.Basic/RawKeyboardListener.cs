using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace NaSeqMen.Nodes.Basic;

public sealed record RawKeyEvent(
    ushort VirtualKey,
    string KeyName,
    string? Character,
    bool IsKeyDown,
    bool Shift,
    bool Ctrl,
    bool Alt);

/// <summary>
/// Passive Windows Raw Input keyboard observer (RIDEV_INPUTSINK).
/// Never suppresses or intercepts input and ignores synthetic SendInput events (hDevice == 0)
/// so Keyboard Output cannot trigger an infinite feedback loop.
/// </summary>
internal sealed class RawKeyboardListener
{
    public static RawKeyboardListener Instance { get; } = new();

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<Guid, Action<RawKeyEvent>> _subscribers = new();
    private Thread? _messageThread;
    private bool _started;
    private volatile bool _registered;
    private volatile int _lastError;

    private RawKeyboardListener() { }

    public bool IsRegistered => _registered;
    public int LastError => _lastError;

    public IDisposable Subscribe(Action<RawKeyEvent> handler)
    {
        var id = Guid.NewGuid();
        _subscribers[id] = handler;
        EnsureStarted();
        return new Subscription(this, id);
    }

    private void Unsubscribe(Guid id)
    {
        _subscribers.TryRemove(id, out _);
        // Keep the message-only sink alive for the lifetime of the process so
        // rapid Deploy stop/start cycles can never race with WM_CLOSE teardown.
    }

    private void EnsureStarted()
    {
        lock (_gate)
        {
            if (_started)
                return;

            _started = true;
            _messageThread = new Thread(RunMessageLoop)
            {
                IsBackground = true,
                Name = "NaSeqMen.RawKeyboardListener",
            };
            _messageThread.Start();
        }
    }

    private void RunMessageLoop()
    {
        const string className = "NaSeqMenRawKeyboardSinkV2";
        WndProcDelegate wndProc = WindowProc;
        var hInstance = GetModuleHandleW(null);

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = hInstance,
            lpszClassName = className,
        };

        RegisterClassExW(ref wc);

        var hwnd = CreateWindowExW(
            0,
            className,
            "NaSeqMenKeyboardSink",
            0,
            0, 0, 0, 0,
            new nint(-3), // HWND_MESSAGE
            0,
            hInstance,
            0);

        if (hwnd == 0)
        {
            _lastError = Marshal.GetLastWin32Error();
            return;
        }

        var rid = new RAWINPUTDEVICE
        {
            usUsagePage = 0x01, // Generic Desktop Controls
            usUsage = 0x06,     // Keyboard
            dwFlags = RIDEV_INPUTSINK,
            hwndTarget = hwnd,
        };

        if (!RegisterRawInputDevices([rid], 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            _lastError = Marshal.GetLastWin32Error();
            return;
        }

        _registered = true;

        while (GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        GC.KeepAlive(wndProc);
    }

    private nint WindowProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        if (msg == WM_INPUT)
        {
            if (!_subscribers.IsEmpty)
            {
                HandleRawInput(lParam);
            }
            return 0;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void HandleRawInput(nint hRawInput)
    {
        uint size = 0;
        var headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        if (GetRawInputData(hRawInput, RID_INPUT, 0, ref size, headerSize) != 0 || size == 0)
            return;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRawInput, RID_INPUT, buffer, ref size, headerSize) != size)
                return;

            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
            if (header.dwType != RIM_TYPEKEYBOARD)
                return;

            // Ignore synthetic software-injected keyboard events (SendInput has hDevice == 0).
            if (header.hDevice == 0)
                return;

            var kb = Marshal.PtrToStructure<RAWKEYBOARD>(buffer + (int)headerSize);

            if (kb.VKey == 0xFF || kb.ExtraInformation == KeyboardSender.ExtraInfoMarker)
                return;

            bool isBreak = (kb.Flags & RI_KEY_BREAK) != 0;
            bool isKeyDown = !isBreak;

            bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
            bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            bool alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;

            var keyName = FormatKeyName(kb.VKey);
            var ch = ctrl || alt ? null : ResolveCharacter(kb.VKey, kb.MakeCode, shift);

            var evt = new RawKeyEvent(kb.VKey, keyName, ch, isKeyDown, shift, ctrl, alt);
            foreach (var sub in _subscribers.Values)
            {
                try { sub(evt); } catch { /* isolate node errors */ }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? ResolveCharacter(ushort vKey, ushort scanCode, bool shift)
    {
        if (vKey == 0x20)
            return " ";

        // Resolve using the foreground window's active keyboard layout (supports EN, RU, etc.)
        var fg = GetForegroundWindow();
        var tid = fg != 0 ? GetWindowThreadProcessId(fg, out _) : 0;
        var hkl = GetKeyboardLayout(tid);

        var keyState = new byte[256];
        if (shift)
            keyState[VK_SHIFT] = 0x80;
        if ((GetKeyState(VK_CAPITAL) & 0x0001) != 0)
            keyState[VK_CAPITAL] = 0x01;

        var sb = new StringBuilder(8);
        var rc = ToUnicodeEx(vKey, scanCode, keyState, sb, sb.Capacity, 0x04, hkl);
        if (rc > 0)
        {
            var s = sb.ToString(0, rc);
            if (!string.IsNullOrEmpty(s) && !char.IsControl(s[0]))
                return s;
        }

        return null;
    }

    private static string FormatKeyName(ushort vk) => vk switch
    {
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x10 or 0xA0 or 0xA1 => "Shift",
        0x11 or 0xA2 or 0xA3 => "Ctrl",
        0x12 or 0xA4 or 0xA5 => "Alt",
        0x14 => "CapsLock",
        0x1B => "Escape",
        0x20 => "Space",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2D => "Insert",
        0x2E => "Delete",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        0x5B or 0x5C => "Win",
        _ => $"VK_{vk:X2}",
    };

    private sealed class Subscription(RawKeyboardListener owner, Guid id) : IDisposable
    {
        public void Dispose() => owner.Unsubscribe(id);
    }

    private const uint WM_INPUT = 0x00FF;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIM_TYPEKEYBOARD = 1;
    private const ushort RI_KEY_BREAK = 0x0001;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_CAPITAL = 0x14;

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public nint hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public nint hDevice;
        public nuint wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(nint hRawInput, uint uiCommand, nint pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hWnd, uint uMsg, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern nint GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(
        uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        [Out] StringBuilder pwszBuff, int cchBuff, uint wFlags, nint dwhkl);
}
