// SPDX-License-Identifier: MIT
using NaSeqMen.Contracts;
using static NaSeqMen.Contracts.MessageCatalog;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NaSeqMen.Input.Windows;

public sealed record RawDevice(string Id, string Kind, string Name);

public sealed record RawSample(string DeviceId, string Kind, string Control, string Phase,
    int Value = 0, int ScanCode = 0, int VirtualKey = 0, int Flags = 0, int X = 0, int Y = 0);

/// <summary>Passive Windows raw-input observation. Never suppresses original input.</summary>
public sealed class RawInputMonitor : IAsyncDisposable
{
    private Thread? _thread;
    private InputWindow? _window;
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<RawSample> _received;
    private readonly Action<Exception> _failed;

    public RawInputMonitor(Action<RawSample> received, Action<Exception> failed)
    {
        _received = received;
        _failed = failed;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_thread is not null) throw Error<InvalidOperationException>(M("input.alreadyRunning"));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            try
            {
                using var context = new ApplicationContext();
                _window = new InputWindow(_received, _failed, context);
                ready.TrySetResult();
                Application.Run(context);
            }
            catch (Exception error)
            {
                ready.TrySetException(error);
                _failed(error);
            }
            finally
            {
                _window?.Close();
                _window = null;
                _finished.TrySetResult();
            }
        }) { IsBackground = true, Name = "NaSeqMen Raw Input" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        try { await ready.Task.WaitAsync(cancellationToken); }
        catch { await DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_thread is null) return;
        var handle = _window?.Handle ?? 0;
        if (handle != 0) Native.PostMessage(handle, Native.StopMessage, 0, 0);
        await _finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    public static IReadOnlyList<RawDevice> EnumerateDevices()
    {
        uint count = 0;
        var stride = (uint)Marshal.SizeOf<Native.DeviceListItem>();
        if (Native.GetRawInputDeviceList(null, ref count, stride) == uint.MaxValue)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var devices = new Native.DeviceListItem[count];
            var actual = Native.GetRawInputDeviceList(devices, ref count, stride);
            if (actual == uint.MaxValue)
            {
                if (Marshal.GetLastWin32Error() == 122) continue;
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return devices.Take((int)actual).Select(d =>
            {
                var name = Native.DeviceName(d.Device);
                var kind = d.Type switch { 0 => "mouse", 1 => "keyboard", _ => "hid" };
                return new RawDevice(name, kind, name);
            }).ToArray();
        }
        throw Error<IOException>(M("input.devicesChanging"));
    }

    private sealed class InputWindow : NativeWindow
    {
        private readonly Action<RawSample> _received;
        private readonly Action<Exception> _failed;
        private readonly ApplicationContext _context;
        private bool _registered;
        private readonly Dictionary<nint, string> _deviceNames = [];

        public InputWindow(Action<RawSample> received, Action<Exception> failed, ApplicationContext context)
        {
            _received = received;
            _failed = failed;
            _context = context;
            CreateHandle(new CreateParams { Caption = "NaSeqMen input", Parent = new IntPtr(-3) });
            var devices = Native.Registrations(Handle, 0x100 | 0x2000);
            if (!Native.RegisterRawInputDevices(devices, (uint)devices.Length,
                    (uint)Marshal.SizeOf<Native.Registration>()))
            {
                var error = new Win32Exception(Marshal.GetLastWin32Error());
                DestroyHandle();
                throw error;
            }
            _registered = true;
        }

        protected override void WndProc(ref Message message)
        {
            try
            {
                if (message.Msg == 0x00FF) Read(message.LParam);
                else if (message.Msg == 0x00FE)
                {
                    var name = DeviceName(message.LParam);
                    _deviceNames.Remove(message.LParam);
                    _received(new RawSample(name, "device", "connection",
                        message.WParam == 1 ? "arrived" : "removed"));
                }
                else if (message.Msg == Native.StopMessage)
                {
                    Close();
                    _context.ExitThread();
                    return;
                }
            }
            catch (Exception error) { _failed(error); }
            base.WndProc(ref message);
        }

        private void Read(nint rawHandle)
        {
            uint size = 0;
            var headerSize = (uint)Marshal.SizeOf<Native.Header>();
            if (Native.GetRawInputData(rawHandle, 0x10000003, 0, ref size, headerSize) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (size < headerSize || size > 1_048_576) return;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (Native.GetRawInputData(rawHandle, 0x10000003, buffer, ref size, headerSize) != size) return;
                var header = Marshal.PtrToStructure<Native.Header>(buffer);
                var data = buffer + (int)headerSize;
                var name = DeviceName(header.Device);
                if (header.Type == 1 && size >= headerSize + 16)
                {
                    // Own synthesized events must not become new physical triggers.
                    if (Marshal.ReadInt32(data, 12) == Native.OwnInputMarker) return;
                    var scan = (ushort)Marshal.ReadInt16(data);
                    var flags = (ushort)Marshal.ReadInt16(data, 2);
                    var key = (ushort)Marshal.ReadInt16(data, 6);
                    if (scan == 0xFF || key == 0xFF) return;
                    var extended = (flags & 2) != 0 ? "E0:" : (flags & 4) != 0 ? "E1:" : "";
                    _received(new RawSample(name, "keyboard", $"{extended}SC:{scan:X2}",
                        (flags & 1) != 0 ? "up" : "down", ScanCode: scan, VirtualKey: key, Flags: flags));
                }
                else if (header.Type == 0 && size >= headerSize + 24)
                {
                    if (Marshal.ReadInt32(data, 20) == Native.OwnInputMarker) return;
                    var mode = (ushort)Marshal.ReadInt16(data);
                    var buttons = (ushort)Marshal.ReadInt16(data, 4);
                    var wheel = Marshal.ReadInt16(data, 6);
                    for (var button = 0; button < 5; button++)
                    {
                        if ((buttons & (1 << (button * 2))) != 0)
                            _received(new RawSample(name, "mouse", $"button{button + 1}", "down"));
                        if ((buttons & (2 << (button * 2))) != 0)
                            _received(new RawSample(name, "mouse", $"button{button + 1}", "up"));
                    }
                    if ((buttons & 0x400) != 0) _received(new RawSample(name, "mouse", "wheel", "change", wheel));
                    if ((buttons & 0x800) != 0) _received(new RawSample(name, "mouse", "horizontalWheel", "change", wheel));
                    var x = Marshal.ReadInt32(data, 12);
                    var y = Marshal.ReadInt32(data, 16);
                    if (x != 0 || y != 0)
                        _received(new RawSample(name, "mouse", "move", "change", Flags: mode, X: x, Y: y));
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private string DeviceName(nint handle)
        {
            if (_deviceNames.TryGetValue(handle, out var name)) return name;
            name = Native.DeviceName(handle);
            if (!name.StartsWith("handle:", StringComparison.Ordinal)) _deviceNames[handle] = name;
            return name;
        }

        public void Close()
        {
            if (_registered)
            {
                var devices = Native.Registrations(0, 1);
                Native.RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<Native.Registration>());
                _registered = false;
            }
            if (Handle != 0) DestroyHandle();
        }
    }

    private static class Native
    {
        public const int StopMessage = 0x8001;
        public const int OwnInputMarker = 0x4E53514D;
        [StructLayout(LayoutKind.Sequential)]
        public struct DeviceListItem { public nint Device; public uint Type; }
        [StructLayout(LayoutKind.Sequential)]
        public struct Header { public uint Type, Size; public nint Device, WParam; }
        [StructLayout(LayoutKind.Sequential)]
        public struct Registration { public ushort UsagePage, Usage; public uint Flags; public nint Target; }
        public static Registration[] Registrations(nint target, uint flags) =>
        [ new() { UsagePage = 1, Usage = 2, Flags = flags, Target = target },
          new() { UsagePage = 1, Usage = 6, Flags = flags, Target = target } ];

        public static string DeviceName(nint device)
        {
            uint count = 0;
            if (GetRawInputDeviceInfo(device, 0x20000007, 0, ref count) == uint.MaxValue || count == 0)
                return $"handle:{device:X}";
            var buffer = Marshal.AllocHGlobal(checked((int)(count + 1) * 2));
            try
            {
                return GetRawInputDeviceInfo(device, 0x20000007, buffer, ref count) == uint.MaxValue
                    ? $"handle:{device:X}" : Marshal.PtrToStringUni(buffer) ?? $"handle:{device:X}";
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterRawInputDevices([In] Registration[] devices, uint count, uint size);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetRawInputData(nint input, uint command, nint data, ref uint size, uint headerSize);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetRawInputDeviceList([Out] DeviceListItem[]? list, ref uint count, uint size);
        [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
        public static extern uint GetRawInputDeviceInfo(nint device, uint command, nint data, ref uint size);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(nint hwnd, uint message, nint wparam, nint lparam);
    }
}
