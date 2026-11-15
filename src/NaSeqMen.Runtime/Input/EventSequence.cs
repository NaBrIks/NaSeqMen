// SPDX-License-Identifier: MIT
using NaSeqMen.Contracts;

namespace NaSeqMen.Runtime;

internal sealed class EventSequence
{
    private readonly Step[] _steps;
    private readonly TimeSpan _timeout;
    private DateTimeOffset _started;
    private long _generation;
    private int _next;
    private string? _lastSignal;

    private EventSequence(Step[] steps, int timeoutMs)
    {
        _steps = steps;
        _timeout = TimeSpan.FromMilliseconds(timeoutMs);
    }

    public static EventSequence? Parse(Dictionary<string, string> settings)
    {
        var lines = settings.GetValueOrDefault("steps", "").Split('\n',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length is < 2 or > 16 ||
            !int.TryParse(settings.GetValueOrDefault("timeoutMs", "1500"), out var timeout) ||
            timeout is < 100 or > 5000) return null;

        var steps = new List<Step>();
        var held = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !parts[0].StartsWith("key:", StringComparison.Ordinal) ||
                parts[0].Length is < 5 or > 16) return null;
            var valid = parts[1] switch
            {
                "down" => held.Add(parts[0]),
                "up" => held.Remove(parts[0]),
                _ => false
            };
            if (!valid) return null;
            steps.Add(new(parts[0], parts[1]));
        }
        return held.Count == 0 ? new(steps.ToArray(), timeout) : null;
    }

    public void Reset() { _next = 0; _lastSignal = null; }

    public bool Accept(Signal signal)
    {
        if (_lastSignal == signal.Id) return false;
        _lastSignal = signal.Id;
        var control = signal.OriginalControl ?? signal.Control;
        if (!control.StartsWith("key:", StringComparison.Ordinal)) return false;
        if (_next != 0 && (_generation != signal.InputGeneration ||
            signal.Time < _started || signal.Time - _started > _timeout))
            _next = 0;

        var step = new Step(control, signal.Phase);
        // A repeat is an extra down for a key already pressed by this match.
        if (signal.Phase == "down" && LastPhase(control) == "down") return false;
        if (step != _steps[_next]) _next = 0;
        if (step != _steps[_next]) return false;

        if (_next == 0)
        {
            _started = signal.Time;
            _generation = signal.InputGeneration;
        }
        if (++_next < _steps.Length) return false;
        _next = 0;
        return true;
    }

    private string? LastPhase(string control)
    {
        for (var index = _next - 1; index >= 0; index--)
            if (_steps[index].Control == control) return _steps[index].Phase;
        return null;
    }

    private sealed record Step(string Control, string Phase);
}
