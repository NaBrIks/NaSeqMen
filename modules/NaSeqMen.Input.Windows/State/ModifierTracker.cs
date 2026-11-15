// SPDX-License-Identifier: MIT
using NaSeqMen.Contracts;

namespace NaSeqMen.Input.Windows;

public sealed class ModifierTracker
{
    private readonly Dictionary<string, (ModifierKeys Held, ModifierKeys Known)> _sources =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<(int Scan, int Prefix)>> _keys =
        new(StringComparer.OrdinalIgnoreCase);

    public void Reset() { _sources.Clear(); _keys.Clear(); }

    public ModifierState Observe(RawSample sample) => Observe(sample, out _);

    public ModifierState Observe(RawSample sample, out bool repeat)
    {
        repeat = false;
        if (sample.Kind == "device")
        {
            _sources.Remove(sample.DeviceId);
            _keys.Remove(sample.DeviceId);
        }
        if (sample.Kind == "keyboard" && sample.Phase is "down" or "up")
        {
            if (!_keys.TryGetValue(sample.DeviceId, out var keys)) _keys[sample.DeviceId] = keys = [];
            var code = (sample.ScanCode, sample.Flags & 6);
            if (sample.Phase == "down") repeat = !keys.Add(code);
            else keys.Remove(code);
        }
        var state = _sources.GetValueOrDefault(sample.DeviceId);
        var key = sample.Kind == "keyboard" ? Identify(sample) : ModifierKeys.None;
        if (key != ModifierKeys.None && sample.Phase is "down" or "up")
        {
            state.Held = sample.Phase == "down" ? state.Held | key : state.Held & ~key;
            state.Known |= key;
            _sources[sample.DeviceId] = state;
        }
        var global = ModifierKeys.None;
        foreach (var device in _sources.Values) global |= device.Held;
        return new(state.Held, state.Known, global);
    }

    private static ModifierKeys Identify(RawSample sample)
    {
        var extended = (sample.Flags & 2) != 0;
        if ((sample.Flags & 4) != 0) return ModifierKeys.None;
        return (sample.ScanCode, extended) switch
        {
            (0x2A, false) => ModifierKeys.LeftShift, (0x36, false) => ModifierKeys.RightShift,
            (0x1D, false) => ModifierKeys.LeftCtrl, (0x1D, true) => ModifierKeys.RightCtrl,
            (0x38, false) => ModifierKeys.LeftAlt, (0x38, true) => ModifierKeys.RightAlt,
            (0x5B, true) => ModifierKeys.LeftWin, (0x5C, true) => ModifierKeys.RightWin,
            _ => ModifierKeys.None
        };
    }
}
