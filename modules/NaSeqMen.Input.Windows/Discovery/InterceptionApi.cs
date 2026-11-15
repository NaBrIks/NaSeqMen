// SPDX-License-Identifier: MIT
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace NaSeqMen.Input.Windows;

internal sealed class InterceptionApi : IDisposable
{
    private const int DeviceCount = 20;
    private readonly nint _library;
    private readonly nint _context;
    private readonly Destroy _destroy;
    private readonly Filter _filter;
    private readonly ReadFilter _readFilter;
    private readonly Wait _wait;
    private readonly Read _receive;
    private readonly Write _send;
    private readonly Read _hardwareId;

    public InterceptionApi(string path)
    {
        _library = NativeLibrary.Load(path);
        try
        {
            _destroy = Bind<Destroy>("destroy_context");
            _filter = Bind<Filter>("set_filter");
            _readFilter = Bind<ReadFilter>("get_filter");
            _wait = Bind<Wait>("wait_with_timeout");
            _receive = Bind<Read>("receive");
            _send = Bind<Write>("send");
            _hardwareId = Bind<Read>("get_hardware_id");
            _context = Bind<Create>("create_context")();
            if (_context == 0) throw new IOException("Interception driver is unavailable.");
        }
        catch { NativeLibrary.Free(_library); throw; }
    }

    public Dictionary<string, int> Resolve(IReadOnlyList<RawDevice> devices)
    {
        var nativeIds = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[4096];
        for (var device = 1; device <= DeviceCount; device++)
        {
            var count = _hardwareId(_context, device, buffer, (uint)buffer.Length);
            if (count is <= 0 or > 4096) continue;
            var hardware = Encoding.Unicode.GetString(buffer, 0, count).Split('\0', 2)[0];
            var key = Normalize(hardware);
            if (!nativeIds.TryGetValue(key, out var candidates)) nativeIds[key] = candidates = [];
            candidates.Add(device);
        }
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var physicalInterfaces = devices.Where(device => device.Kind is "keyboard" or "mouse")
            .Select(device => (Device: device, Key: InterfaceKey(device.Id)))
            .Where(item => item.Key is not null)
            .GroupBy(item => item.Key!, StringComparer.OrdinalIgnoreCase);
        foreach (var group in physicalInterfaces)
        {
            if (group.Count() != 1 || !nativeIds.TryGetValue(group.Key, out var candidates) || candidates.Count != 1) continue;
            var device = group.First().Device;
            var index = candidates[0];
            if ((index <= 10) == (device.Kind == "keyboard")) result.Add(device.Id, index);
        }
        return result;
    }

    public bool SetFilter(int device, ushort flags)
    {
        _filter(_context, candidate => candidate == device ? 1 : 0, flags);
        return _readFilter(_context, device) == flags;
    }

    public int WaitForDevice() => _wait(_context, 60);
    public bool Receive(int device, byte[] stroke) => _receive(_context, device, stroke, 1) == 1;
    public void Send(int device, byte[] stroke) => _send(_context, device, stroke, 1);

    public void Dispose()
    {
        _destroy(_context);
        NativeLibrary.Free(_library);
    }

    private static string? InterfaceKey(string path)
    {
        if (!path.StartsWith(@"\\?\", StringComparison.Ordinal)) return null;
        var parts = path[4..].Split('#', 3);
        return parts.Length == 3 ? Normalize(parts[0] + "\\" + parts[1]) : null;
    }

    private static string Normalize(string hardware) => Regex.Replace(hardware, "&REV_[0-9A-F]+", "",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private T Bind<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, "interception_" + name));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint Create();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Destroy(nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Predicate(int device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Filter(nint context, Predicate predicate, ushort flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ushort ReadFilter(nint context, int device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Wait(nint context, uint milliseconds);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Read(nint context, int device, [Out] byte[] buffer, uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Write(nint context, int device, [In] byte[] buffer, uint length);
}
