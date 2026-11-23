namespace NaSeqMen.Nodes.Interception;

/// <summary>
/// Bidirectional IBM PC Set 1 scan code and mouse button table.
/// </summary>
public static partial class ScanCodeMap
{
    public readonly record struct ScanEntry(ushort ScanCode, bool IsE0, bool IsE1 = false);

    private readonly record struct MouseButtonEntry(
        string CanonicalName,
        ushort DownState,
        ushort UpState,
        short Rolling = 0);

    private static readonly Dictionary<string, ScanEntry> NameToScan = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<(ushort Code, bool IsE0), string> ScanToName = new();
    private static readonly Dictionary<string, MouseButtonEntry> NameToMouse = new(StringComparer.OrdinalIgnoreCase);

    static ScanCodeMap()
    {
        // Escape and number row
        Register("Esc", 0x01, false, "Escape");
        Register("1", 0x02, false, "D1");
        Register("2", 0x03, false, "D2");
        Register("3", 0x04, false, "D3");
        Register("4", 0x05, false, "D4");
        Register("5", 0x06, false, "D5");
        Register("6", 0x07, false, "D6");
        Register("7", 0x08, false, "D7");
        Register("8", 0x09, false, "D8");
        Register("9", 0x0A, false, "D9");
        Register("0", 0x0B, false, "D0");
        Register("Minus", 0x0C, false, "-", "OemMinus");
        Register("Equal", 0x0D, false, "=", "Equals", "OemPlus");
        Register("Backspace", 0x0E, false, "Back", "BS");
        Register("Tab", 0x0F, false);

        // Q..P row
        Register("Q", 0x10, false);
        Register("W", 0x11, false);
        Register("E", 0x12, false);
        Register("R", 0x13, false);
        Register("T", 0x14, false);
        Register("Y", 0x15, false);
        Register("U", 0x16, false);
        Register("I", 0x17, false);
        Register("O", 0x18, false);
        Register("P", 0x19, false);
        Register("LeftBracket", 0x1A, false, "[", "LBracket", "OemOpenBrackets");
        Register("RightBracket", 0x1B, false, "]", "RBracket", "OemCloseBrackets");
        Register("Enter", 0x1C, false, "Return");
        Register("LCtrl", 0x1D, false, "Ctrl", "Control", "LControl");

        // A..L row
        Register("A", 0x1E, false);
        Register("S", 0x1F, false);
        Register("D", 0x20, false);
        Register("F", 0x21, false);
        Register("G", 0x22, false);
        Register("H", 0x23, false);
        Register("J", 0x24, false);
        Register("K", 0x25, false);
        Register("L", 0x26, false);
        Register("Semicolon", 0x27, false, ";", "OemSemicolon");
        Register("Apostrophe", 0x28, false, "'", "Quote", "OemQuotes");
        Register("Grave", 0x29, false, "`", "Backtick", "Tilde", "OemTilde");
        Register("LShift", 0x2A, false, "Shift");
        Register("Backslash", 0x2B, false, "\\", "OemPipe");

        // Z..M row
        Register("Z", 0x2C, false);
        Register("X", 0x2D, false);
        Register("C", 0x2E, false);
        Register("V", 0x2F, false);
        Register("B", 0x30, false);
        Register("N", 0x31, false);
        Register("M", 0x32, false);
        Register("Comma", 0x33, false, "OemComma");
        Register("Period", 0x34, false, ".", "Dot", "OemPeriod");
        Register("Slash", 0x35, false, "/", "OemQuestion");
        Register("RShift", 0x36, false);
        Register("NumpadMultiply", 0x37, false, "NumMul");
        Register("LAlt", 0x38, false, "Alt", "LMenu");
        Register("Space", 0x39, false, "Spacebar");
        Register("CapsLock", 0x3A, false, "Caps", "Capital");

        // Function keys and numpad
        Register("F1", 0x3B, false);
        Register("F2", 0x3C, false);
        Register("F3", 0x3D, false);
        Register("F4", 0x3E, false);
        Register("F5", 0x3F, false);
        Register("F6", 0x40, false);
        Register("F7", 0x41, false);
        Register("F8", 0x42, false);
        Register("F9", 0x43, false);
        Register("F10", 0x44, false);
        Register("NumLock", 0x45, false);
        Register("ScrollLock", 0x46, false);
        Register("Numpad7", 0x47, false, "Num7");
        Register("Numpad8", 0x48, false, "Num8");
        Register("Numpad9", 0x49, false, "Num9");
        Register("NumpadSubtract", 0x4A, false, "NumSub");
        Register("Numpad4", 0x4B, false, "Num4");
        Register("Numpad5", 0x4C, false, "Num5");
        Register("Numpad6", 0x4D, false, "Num6");
        Register("NumpadAdd", 0x4E, false, "NumAdd");
        Register("Numpad1", 0x4F, false, "Num1");
        Register("Numpad2", 0x50, false, "Num2");
        Register("Numpad3", 0x51, false, "Num3");
        Register("Numpad0", 0x52, false, "Num0");
        Register("NumpadDecimal", 0x53, false, "NumDot");
        Register("F11", 0x57, false);
        Register("F12", 0x58, false);

        // Extended E0 keys
        Register("NumpadEnter", 0x1C, true, "NumEnter");
        Register("RCtrl", 0x1D, true, "RControl");
        Register("NumpadDivide", 0x35, true, "NumDiv");
        Register("PrintScreen", 0x37, true, "PrtSc", "Snapshot");
        Register("RAlt", 0x38, true, "AltGr", "RMenu");
        Register("Pause", 0x46, true, "Break");
        Register("Home", 0x47, true);
        Register("Up", 0x48, true, "UpArrow");
        Register("PageUp", 0x49, true, "PgUp", "Prior");
        Register("Left", 0x4B, true, "LeftArrow");
        Register("Right", 0x4D, true, "RightArrow");
        Register("End", 0x4F, true);
        Register("Down", 0x50, true, "DownArrow");
        Register("PageDown", 0x51, true, "PgDn", "Next");
        Register("Insert", 0x52, true, "Ins");
        Register("Delete", 0x53, true, "Del");
        Register("LWin", 0x5B, true, "Win", "Meta", "Super");
        Register("RWin", 0x5C, true);
        Register("Apps", 0x5D, true, "Menu", "ContextMenu");

        // Mouse buttons and wheel
        RegisterMouse("Left", InterceptionConstants.MouseLeftDown, InterceptionConstants.MouseLeftUp, 0, "LButton", "MouseLeft");
        RegisterMouse("Right", InterceptionConstants.MouseRightDown, InterceptionConstants.MouseRightUp, 0, "RButton", "MouseRight");
        RegisterMouse("Middle", InterceptionConstants.MouseMiddleDown, InterceptionConstants.MouseMiddleUp, 0, "MButton", "MouseMiddle");
        RegisterMouse("X1", InterceptionConstants.MouseButton4Down, InterceptionConstants.MouseButton4Up, 0, "Button4", "Mouse4", "XButton1");
        RegisterMouse("X2", InterceptionConstants.MouseButton5Down, InterceptionConstants.MouseButton5Up, 0, "Button5", "Mouse5", "XButton2");
        RegisterMouse("WheelUp", InterceptionConstants.MouseWheel, 0, 120, "ScrollUp");
        RegisterMouse("WheelDown", InterceptionConstants.MouseWheel, 0, -120, "ScrollDown");
        RegisterMouse("WheelRight", InterceptionConstants.MouseHWheel, 0, 120, "HWheelRight");
        RegisterMouse("WheelLeft", InterceptionConstants.MouseHWheel, 0, -120, "HWheelLeft");
    }

    private static void Register(string canonical, ushort scanCode, bool isE0, params string[] aliases)
    {
        var entry = new ScanEntry(scanCode, isE0);
        NameToScan[canonical] = entry;
        ScanToName.TryAdd((scanCode, isE0), canonical);
        foreach (var alias in aliases)
            NameToScan[alias] = entry;
    }

    private static void RegisterMouse(string canonical, ushort downState, ushort upState, short rolling, params string[] aliases)
    {
        var entry = new MouseButtonEntry(canonical, downState, upState, rolling);
        NameToMouse[canonical] = entry;
        foreach (var alias in aliases)
            NameToMouse[alias] = entry;
    }

    public static bool TryGetScanCode(string keyName, out ushort scanCode, out bool isE0)
    {
        if (!string.IsNullOrWhiteSpace(keyName) && NameToScan.TryGetValue(keyName.Trim(), out var entry))
        {
            scanCode = entry.ScanCode;
            isE0 = entry.IsE0;
            return true;
        }

        scanCode = 0;
        isE0 = false;
        return false;
    }

    public static bool TryGetMouseButton(string buttonName, out string canonicalName, out ushort downState, out ushort upState, out short rolling)
    {
        if (!string.IsNullOrWhiteSpace(buttonName) && NameToMouse.TryGetValue(buttonName.Trim(), out var entry))
        {
            canonicalName = entry.CanonicalName;
            downState = entry.DownState;
            upState = entry.UpState;
            rolling = entry.Rolling;
            return true;
        }

        canonicalName = string.Empty;
        downState = 0;
        upState = 0;
        rolling = 0;
        return false;
    }

    public static string GetKeyName(ushort scanCode, bool isE0, bool isE1 = false)
    {
        if (isE1)
            return "Pause";

        if (ScanToName.TryGetValue((scanCode, isE0), out var name))
            return name;

        return isE0 ? $"E0_{scanCode:X2}" : $"SC_{scanCode:X2}";
    }

    public static string GetKeyNameFromState(ushort scanCode, ushort rawState)
    {
        var isE0 = (rawState & InterceptionConstants.KeyE0) != 0;
        var isE1 = (rawState & InterceptionConstants.KeyE1) != 0;
        return GetKeyName(scanCode, isE0, isE1);
    }

    public static (string Button, string Phase, bool IsDown) DecodeMouseStroke(in InterceptionMouseStroke stroke)
    {
        var s = stroke.State;
        if ((s & InterceptionConstants.MouseLeftDown) != 0) return ("Left", "down", true);
        if ((s & InterceptionConstants.MouseLeftUp) != 0) return ("Left", "up", false);
        if ((s & InterceptionConstants.MouseRightDown) != 0) return ("Right", "down", true);
        if ((s & InterceptionConstants.MouseRightUp) != 0) return ("Right", "up", false);
        if ((s & InterceptionConstants.MouseMiddleDown) != 0) return ("Middle", "down", true);
        if ((s & InterceptionConstants.MouseMiddleUp) != 0) return ("Middle", "up", false);
        if ((s & InterceptionConstants.MouseButton4Down) != 0) return ("X1", "down", true);
        if ((s & InterceptionConstants.MouseButton4Up) != 0) return ("X1", "up", false);
        if ((s & InterceptionConstants.MouseButton5Down) != 0) return ("X2", "down", true);
        if ((s & InterceptionConstants.MouseButton5Up) != 0) return ("X2", "up", false);
        if ((s & InterceptionConstants.MouseWheel) != 0)
            return (stroke.Rolling >= 0 ? "WheelUp" : "WheelDown", "wheel", true);
        if ((s & InterceptionConstants.MouseHWheel) != 0)
            return (stroke.Rolling >= 0 ? "WheelRight" : "WheelLeft", "wheel", true);

        return ("Move", "move", false);
    }
}
