// SPDX-License-Identifier: MIT
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using NaSeqMen.Contracts;
using static NaSeqMen.Contracts.MessageCatalog;

namespace NaSeqMen.Desktop.Windows;

public readonly record struct KeyboardKey(ushort Code, bool Scan = false, bool Extended = false);

/// <summary>Separates ownership tests from operating-system input injection.</summary>
public interface IKeyboardOutput
{
    bool IsDown(KeyboardKey key);
    void Send(KeyboardKey key, bool up);
}

public sealed class DesktopModule : NaModule
{
    private readonly SemaphoreSlim _inputGate = new(1);
    private readonly Dictionary<KeyboardKey, KeyLease> _held = [];
    private readonly IKeyboardOutput _output;
    private bool _disposed;

    public DesktopModule() : this(new WindowsKeyboardOutput()) { }
    public DesktopModule(IKeyboardOutput output) => _output = output;
    public override ModuleDescriptor Descriptor => new("windows.desktop", M("module.windows.desktop"), "0.1.0", false, false,
        [new("key", M("operation.windows.desktop.key"), ["key", "phase"]) { DestinationNameKey = "destination.activeWindow" },
         new("chord", M("operation.windows.desktop.chord"), ["keys"]) { DestinationNameKey = "destination.activeWindow" },
         new("launch", M("operation.windows.desktop.launch"), ["file", "arguments"])],
        [new("foreground", M("operation.windows.desktop.foreground"), ["process"]), new("processRunning", M("operation.windows.desktop.processRunning"), ["process"])]);

    public override async ValueTask<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Operation == "launch")
        {
            var file = Required(request.Arguments, "file");
            cancellationToken.ThrowIfCancellationRequested();
            using var process = Process.Start(new ProcessStartInfo(file, request.Arguments.GetValueOrDefault("arguments", "")) { UseShellExecute = true });
            return new(true, M("desktop.launched"));
        }
        await _inputGate.WaitAsync(cancellationToken);
        try
        {
            if (request.Operation == "chord")
            {
                var keys = Required(request.Arguments, "keys").Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(ParseKey).Distinct().ToArray();
                if (keys.Length is 0 or > 8) throw Error<ArgumentException>(M("desktop.chordLength"));
                var owner = new KeyOwner(request.ConnectionId, "macro", Guid.NewGuid().ToString("N"));
                try
                {
                    foreach (var key in keys) { cancellationToken.ThrowIfCancellationRequested(); Acquire(key, owner); }
                }
                finally { ReleaseOwners(candidate => candidate == owner); }
                return new(true, M("desktop.chordSent"));
            }
            if (request.Operation != "key") return new(false, M("desktop.actionUnknown"));
            var keySpec = ParseKey(Required(request.Arguments, "key"));
            var phase = request.Arguments.GetValueOrDefault("phase", "press");
            if (phase is not ("down" or "up" or "press")) throw Error<ArgumentException>(M("desktop.phase"));
            var keyOwner = new KeyOwner(request.ConnectionId, request.Input.Source, request.Input.Control);
            if (phase == "down") Acquire(keySpec, keyOwner);
            else if (phase == "up") Release(keySpec, keyOwner);
            else
            {
                var temporary = new KeyOwner(request.ConnectionId, "press", Guid.NewGuid().ToString("N"));
                try { Acquire(keySpec, temporary); }
                finally { Release(keySpec, temporary); }
            }
            return new(true, M("desktop.keySent"));
        }
        catch (Exception error) when (error is not OperationCanceledException) { return new(false, FromException(error)); }
        finally { _inputGate.Release(); }
    }

    public override ValueTask<bool> EvaluateAsync(ConditionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expected = Path.GetFileName(Required(request.Arguments, "process"));
        if (expected.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) expected = expected[..^4];
        if (request.Operation == "processRunning")
        {
            var processes = Process.GetProcessesByName(expected);
            try { return ValueTask.FromResult(processes.Length != 0); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        if (request.Operation == "foreground")
        {
            var window = Native.GetForegroundWindow();
            if (window == 0) return ValueTask.FromResult(false);
            Native.GetWindowThreadProcessId(window, out var id);
            try
            {
                using var process = Process.GetProcessById((int)id);
                return ValueTask.FromResult(string.Equals(process.ProcessName, expected, StringComparison.OrdinalIgnoreCase));
            }
            catch (ArgumentException) { return ValueTask.FromResult(false); }
        }
        throw Error<ArgumentException>(M("desktop.conditionUnknown"));
    }

    public override async ValueTask DisconnectAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        await _inputGate.WaitAsync(cancellationToken);
        try
        {
            ReleaseOwners(owner => owner.ConnectionId == connectionId);
        }
        finally { _inputGate.Release(); }
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await _inputGate.WaitAsync();
        try
        {
            ReleaseOwners(_ => true);
            _disposed = true;
        }
        finally { _inputGate.Release(); }
    }

    private readonly record struct KeyOwner(string ConnectionId, string Source, string Control);
    private sealed class KeyLease(bool owned)
    {
        public bool Owned { get; } = owned;
        public HashSet<KeyOwner> Owners { get; } = [];
    }

    private void Acquire(KeyboardKey key, KeyOwner owner)
    {
        if (_held.TryGetValue(key, out var lease))
        {
            // Repeated down is one ownership claim, not a growing reference count.
            lease.Owners.Add(owner);
            return;
        }
        var alreadyDown = _output.IsDown(key);
        if (!alreadyDown) _output.Send(key, false);
        lease = new KeyLease(!alreadyDown);
        lease.Owners.Add(owner);
        _held.Add(key, lease);
    }

    private void Release(KeyboardKey key, KeyOwner owner)
    {
        if (!_held.TryGetValue(key, out var lease) || !lease.Owners.Contains(owner)) return;
        if (lease.Owners.Count == 1)
        {
            // Preserve the lease if release fails so later cleanup can retry it.
            if (lease.Owned) _output.Send(key, true);
            _held.Remove(key);
        }
        else lease.Owners.Remove(owner);
    }

    private void ReleaseOwners(Func<KeyOwner, bool> match)
    {
        List<Exception> failures = [];
        foreach (var pair in _held.Reverse().ToArray())
            foreach (var owner in pair.Value.Owners.Where(match).ToArray())
                try { Release(pair.Key, owner); }
                catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) throw Aggregate(M("desktop.releaseFailed"), failures);
    }

    private static string Required(Dictionary<string, string> args, string name) =>
        args.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value :
            throw Error<ArgumentException>(M("desktop.parameterMissing", ("parameter", name)));

    private sealed class WindowsKeyboardOutput : IKeyboardOutput
    {
        public bool IsDown(KeyboardKey key)
        {
            var vk = key.Scan ? Native.MapVirtualKey((uint)(key.Code | (key.Extended ? 0xE000 : 0)), 3) : key.Code;
            return vk != 0 && (Native.GetAsyncKeyState((int)vk) & 0x8000) != 0;
        }

        public void Send(KeyboardKey key, bool up)
        {
            var input = new Native.Input { Type = 1, Union = new Native.InputUnion { Keyboard = new Native.KeyboardInput
            { VirtualKey = key.Scan ? (ushort)0 : key.Code, ScanCode = key.Scan ? key.Code : (ushort)0,
              Flags = (key.Scan ? 8u : 0u) | (key.Extended ? 1u : 0u) | (up ? 2u : 0u), ExtraInfo = 0x4E53514D } } };
            if (Native.SendInput(1, [input], Marshal.SizeOf<Native.Input>()) != 1)
                throw Win32(Marshal.GetLastWin32Error(), M("desktop.sendInputFailed"));
        }
    }

    private static KeyboardKey ParseKey(string name)
    {
        var key = name.Trim().ToUpperInvariant();
        if (key.StartsWith("SC:"))
        {
            var value = key[3..];
            var extended = value.StartsWith("E0:");
            if (extended) value = value[3..];
            if (ushort.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var scan) && scan is > 0 and < 256)
                return new(scan, true, extended);
            throw Error<ArgumentException>(M("desktop.scanCode"));
        }
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) return new(key[0]);
        if (key.StartsWith('F') && int.TryParse(key[1..], out var f) && f is >= 1 and <= 24) return new((ushort)(0x70 + f - 1));
        return key switch
        {
            "CTRL" or "CONTROL" or "LCTRL" => new(0xA2), "RCTRL" => new(0xA3, Extended: true),
            "SHIFT" or "LSHIFT" => new(0xA0), "RSHIFT" => new(0xA1),
            "ALT" or "LALT" => new(0xA4), "RALT" => new(0xA5, Extended: true),
            "WIN" or "LWIN" => new(0x5B, Extended: true), "RWIN" => new(0x5C, Extended: true),
            "ENTER" or "RETURN" => new(0x0D), "ESC" or "ESCAPE" => new(0x1B),
            "TAB" => new(9), "SPACE" => new(0x20), "BACKSPACE" => new(8),
            "DELETE" or "DEL" => new(0x2E, Extended: true), "INSERT" => new(0x2D, Extended: true),
            "HOME" => new(0x24, Extended: true), "END" => new(0x23, Extended: true),
            "PAGEUP" => new(0x21, Extended: true), "PAGEDOWN" => new(0x22, Extended: true),
            "LEFT" => new(0x25, Extended: true), "UP" => new(0x26, Extended: true),
            "RIGHT" => new(0x27, Extended: true), "DOWN" => new(0x28, Extended: true),
            "VOLUMEMUTE" => new(0xAD, Extended: true), "VOLUMEDOWN" => new(0xAE, Extended: true),
            "VOLUMEUP" => new(0xAF, Extended: true), "MEDIAPLAYPAUSE" => new(0xB3, Extended: true),
            _ => throw Error<ArgumentException>(M("desktop.keyUnknown", ("key", name)))
        };
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct Input { public uint Type; public InputUnion Union; }
        [StructLayout(LayoutKind.Explicit)] public struct InputUnion
        { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
        [StructLayout(LayoutKind.Sequential)] public struct KeyboardInput
        { public ushort VirtualKey, ScanCode; public uint Flags, Time; public nuint ExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] public struct MouseInput
        { public int X, Y; public uint MouseData, Flags, Time; public nuint ExtraInfo; }
        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint window, out uint processId);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int virtualKey);
        [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")] public static extern uint MapVirtualKey(uint code, uint type);
    }
}
