using System.Runtime.InteropServices;

namespace NaSeqMen.Nodes.Interception;

/// <summary>
/// Standard Interception C ABI constants (interception.h).
/// </summary>
public static class InterceptionConstants
{
    public const int MaxKeyboard = 10;
    public const int MaxMouse = 10;
    public const int MaxDevice = MaxKeyboard + MaxMouse;

    // Keyboard stroke state flags
    public const ushort KeyDown = 0x00;
    public const ushort KeyUp = 0x01;
    public const ushort KeyE0 = 0x02;
    public const ushort KeyE1 = 0x04;
    public const ushort KeyTermSrvSetLed = 0x08;
    public const ushort KeyTermSrvShadow = 0x10;
    public const ushort KeyTermSrvVkPacket = 0x20;

    // Keyboard filter flags
    public const ushort FilterKeyNone = 0x0000;
    public const ushort FilterKeyAll = 0xFFFF;
    public const ushort FilterKeyDown = 0x0001;
    public const ushort FilterKeyUp = 0x0002;
    public const ushort FilterKeyE0 = 0x0004;
    public const ushort FilterKeyE1 = 0x0008;

    // Mouse stroke state flags
    public const ushort MouseLeftDown = 0x001;
    public const ushort MouseLeftUp = 0x002;
    public const ushort MouseRightDown = 0x004;
    public const ushort MouseRightUp = 0x008;
    public const ushort MouseMiddleDown = 0x010;
    public const ushort MouseMiddleUp = 0x020;
    public const ushort MouseButton4Down = 0x040;
    public const ushort MouseButton4Up = 0x080;
    public const ushort MouseButton5Down = 0x100;
    public const ushort MouseButton5Up = 0x200;
    public const ushort MouseWheel = 0x400;
    public const ushort MouseHWheel = 0x800;

    // Mouse filter flags
    public const ushort FilterMouseNone = 0x0000;
    public const ushort FilterMouseAll = 0xFFFF;
    public const ushort FilterMouseButtonsAndWheel = 0x0FFF;
    public const ushort FilterMouseMove = 0x1000;

    public static bool IsKeyboardSlot(int device) => device >= 1 && device <= MaxKeyboard;

    public static bool IsMouseSlot(int device) => device > MaxKeyboard && device <= MaxDevice;
}

/// <summary>
/// 8-byte keyboard stroke structure matching the Interception x64 C ABI.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct InterceptionKeyStroke
{
    public ushort Code;
    public ushort State;
    public uint Information;
}

/// <summary>
/// 20-byte mouse stroke structure matching the Interception x64 C ABI.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct InterceptionMouseStroke
{
    public ushort State;
    public ushort Flags;
    public short Rolling;
    public int X;
    public int Y;
    public uint Information;
}

/// <summary>
/// 20-byte union of keyboard and mouse strokes matching InterceptionStroke.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 20)]
public struct InterceptionStroke
{
    [FieldOffset(0)]
    public InterceptionKeyStroke Key;

    [FieldOffset(0)]
    public InterceptionMouseStroke Mouse;
}

/// <summary>
/// Cdecl device filter predicate passed to interception_set_filter.
/// </summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate int InterceptionPredicate(int device);

/// <summary>
/// Abstraction over the Interception C ABI allowing clean mock injection in unit tests.
/// </summary>
public interface IInterceptionBackend : IDisposable
{
    bool IsAvailable { get; }
    string? LoadedLibraryPath { get; }
    string? LastError { get; }

    nint CreateContext();
    void DestroyContext(nint context);
    void SetFilter(nint context, InterceptionPredicate predicate, ushort filter);
    int WaitWithTimeout(nint context, uint milliseconds);
    int Receive(nint context, int device, ref InterceptionStroke stroke);
    int Send(nint context, int device, in InterceptionStroke stroke);
    string GetHardwareId(nint context, int device);
}

/// <summary>
/// Dynamically loads interception.dll at runtime via NativeLibrary.TryLoad without bundling any driver binaries.
/// </summary>
public sealed unsafe class NativeInterceptionBackend : IInterceptionBackend
{
    private const string EnvVarName = "NASEQMEN_INTERCEPTION_DLL";

    private nint _libraryHandle;
    private delegate* unmanaged[Cdecl]<nint> _createContext;
    private delegate* unmanaged[Cdecl]<nint, void> _destroyContext;
    private delegate* unmanaged[Cdecl]<nint, nint, ushort, void> _setFilter;
    private delegate* unmanaged[Cdecl]<nint, uint, int> _waitWithTimeout;
    private delegate* unmanaged[Cdecl]<nint, int, void*, uint, int> _receive;
    private delegate* unmanaged[Cdecl]<nint, int, void*, uint, int> _send;
    private delegate* unmanaged[Cdecl]<nint, int, void*, uint, uint> _getHardwareId;

    // Keep the most recent predicate delegate rooted to prevent premature GC collection.
    private InterceptionPredicate? _lastPredicate;

    private NativeInterceptionBackend()
    {
    }

    public bool IsAvailable => _libraryHandle != nint.Zero;
    public string? LoadedLibraryPath { get; private set; }
    public string? LastError { get; private set; }

    public static NativeInterceptionBackend Load(string? userDllPath = null)
    {
        var backend = new NativeInterceptionBackend();
        backend.TryLoadLibrary(userDllPath);
        return backend;
    }

    private void TryLoadLibrary(string? userDllPath)
    {
        var candidates = BuildCandidatePaths(userDllPath);
        var errors = new List<string>();

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            var resolved = candidate.Trim();
            if (Directory.Exists(resolved))
                resolved = Path.Combine(resolved, "interception.dll");

            // For rooted paths, skip non-existent files to avoid noisy load failures.
            if (Path.IsPathRooted(resolved) && !File.Exists(resolved))
                continue;

            if (!NativeLibrary.TryLoad(resolved, out var handle))
            {
                errors.Add($"Failed to load '{resolved}'.");
                continue;
            }

            if (BindExports(handle, out var bindError))
            {
                _libraryHandle = handle;
                LoadedLibraryPath = resolved;
                LastError = null;
                return;
            }

            NativeLibrary.Free(handle);
            errors.Add($"Loaded '{resolved}' but failed to bind ABI: {bindError}");
        }

        LastError = errors.Count > 0
            ? string.Join(" ", errors)
            : "interception.dll not found. Place x64 interception.dll into plugins\\NaSeqMen.Nodes.Interception\\, install to C:\\Interception, or set NASEQMEN_INTERCEPTION_DLL / DLL Path.";
    }

    private static IReadOnlyList<string> BuildCandidatePaths(string? userDllPath)
    {
        var list = new List<string>(8);

        // 1. Optional user-provided dllPath parameter
        if (!string.IsNullOrWhiteSpace(userDllPath))
            list.Add(userDllPath);

        // 2. NASEQMEN_INTERCEPTION_DLL environment variable
        var envPath = Environment.GetEnvironmentVariable(EnvVarName);
        if (!string.IsNullOrWhiteSpace(envPath))
            list.Add(envPath);

        // 3. Plugin directory (plugins/NaSeqMen.Nodes.Interception/interception.dll or x64/interception.dll)
        var pluginDir = Path.GetDirectoryName(typeof(NativeInterceptionBackend).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(pluginDir))
        {
            list.Add(Path.Combine(pluginDir, "interception.dll"));
            list.Add(Path.Combine(pluginDir, "x64", "interception.dll"));
        }

        // 4. Host app base directory (interception.dll)
        list.Add(Path.Combine(AppContext.BaseDirectory, "interception.dll"));

        // 5. Standard OS search (PATH / System32)
        list.Add("interception.dll");

        // 6. Standard unpacked archive paths
        list.Add(@"C:\Interception\library\x64\interception.dll");
        var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(progFiles))
            list.Add(Path.Combine(progFiles, "Interception", "library", "x64", "interception.dll"));

        return list;
    }

    private bool BindExports(nint handle, out string? error)
    {
        error = null;

        if (!TryGetExport(handle, "interception_create_context", out var pCreate) ||
            !TryGetExport(handle, "interception_destroy_context", out var pDestroy) ||
            !TryGetExport(handle, "interception_set_filter", out var pSetFilter) ||
            !TryGetExport(handle, "interception_wait_with_timeout", out var pWait) ||
            !TryGetExport(handle, "interception_receive", out var pReceive) ||
            !TryGetExport(handle, "interception_send", out var pSend) ||
            !TryGetExport(handle, "interception_get_hardware_id", out var pGetHwId))
        {
            error = "Missing required Interception C ABI exports.";
            return false;
        }

        _createContext = (delegate* unmanaged[Cdecl]<nint>)pCreate;
        _destroyContext = (delegate* unmanaged[Cdecl]<nint, void>)pDestroy;
        _setFilter = (delegate* unmanaged[Cdecl]<nint, nint, ushort, void>)pSetFilter;
        _waitWithTimeout = (delegate* unmanaged[Cdecl]<nint, uint, int>)pWait;
        _receive = (delegate* unmanaged[Cdecl]<nint, int, void*, uint, int>)pReceive;
        _send = (delegate* unmanaged[Cdecl]<nint, int, void*, uint, int>)pSend;
        _getHardwareId = (delegate* unmanaged[Cdecl]<nint, int, void*, uint, uint>)pGetHwId;
        return true;
    }

    private static bool TryGetExport(nint handle, string name, out nint address)
        => NativeLibrary.TryGetExport(handle, name, out address);

    public nint CreateContext()
        => IsAvailable ? _createContext() : nint.Zero;

    public void DestroyContext(nint context)
    {
        if (IsAvailable && context != nint.Zero)
            _destroyContext(context);
    }

    public void SetFilter(nint context, InterceptionPredicate predicate, ushort filter)
    {
        if (!IsAvailable || context == nint.Zero)
            return;

        _lastPredicate = predicate;
        var ptr = Marshal.GetFunctionPointerForDelegate(predicate);
        _setFilter(context, ptr, filter);
        GC.KeepAlive(predicate);
    }

    public int WaitWithTimeout(nint context, uint milliseconds)
        => IsAvailable && context != nint.Zero ? _waitWithTimeout(context, milliseconds) : 0;

    public int Receive(nint context, int device, ref InterceptionStroke stroke)
    {
        if (!IsAvailable || context == nint.Zero)
            return 0;

        fixed (InterceptionStroke* ptr = &stroke)
        {
            return _receive(context, device, ptr, 1);
        }
    }

    public int Send(nint context, int device, in InterceptionStroke stroke)
    {
        if (!IsAvailable || context == nint.Zero)
            return 0;

        var copy = stroke;
        return _send(context, device, &copy, 1);
    }

    public string GetHardwareId(nint context, int device)
    {
        if (!IsAvailable || context == nint.Zero)
            return string.Empty;

        const int charCapacity = 512;
        Span<char> buffer = stackalloc char[charCapacity];
        fixed (char* ptr = buffer)
        {
            var bytesWritten = _getHardwareId(context, device, ptr, (uint)(charCapacity * sizeof(char)));
            if (bytesWritten == 0)
                return string.Empty;

            var charCount = Math.Min((int)(bytesWritten / sizeof(char)), charCapacity);
            var span = buffer[..charCount];
            var nullIndex = span.IndexOf('\0');
            if (nullIndex >= 0)
                span = span[..nullIndex];

            return new string(span);
        }
    }

    public void Dispose()
    {
        if (_libraryHandle != nint.Zero)
        {
            NativeLibrary.Free(_libraryHandle);
            _libraryHandle = nint.Zero;
        }
        _lastPredicate = null;
    }
}
