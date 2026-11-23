namespace NaSeqMen.Nodes.Interception;

/// <summary>
/// Chord/sequence parsing, hold-mirroring splits, and key/button pattern matching for <see cref="ScanCodeMap"/>.
/// </summary>
public static partial class ScanCodeMap
{
    public static bool IsMouseActionExpression(string? expr)
    {
        if (string.IsNullOrWhiteSpace(expr))
            return false;

        var parts = expr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        foreach (var part in parts)
        {
            if (!NameToMouse.ContainsKey(part))
                return false;
        }

        return true;
    }

    public static IReadOnlyList<MouseActionStep> ParseMouseSequence(string? expr)
    {
        if (string.IsNullOrWhiteSpace(expr))
            return Array.Empty<MouseActionStep>();

        var steps = new List<MouseActionStep>();
        var parts = expr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            if (!NameToMouse.TryGetValue(part, out var entry))
                continue;

            steps.Add(new MouseActionStep(entry.DownState, entry.Rolling));
            if (entry.UpState != 0)
                steps.Add(new MouseActionStep(entry.UpState, 0));
        }

        return steps;
    }

    public static IReadOnlyList<MouseActionStep> ParseMouseHoldPress(string? expr)
    {
        if (string.IsNullOrWhiteSpace(expr))
            return Array.Empty<MouseActionStep>();

        var parts = expr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var steps = new List<MouseActionStep>();
        for (var i = 0; i < parts.Length; i++)
        {
            if (!NameToMouse.TryGetValue(parts[i], out var entry))
                continue;

            steps.Add(new MouseActionStep(entry.DownState, entry.Rolling));
            if (i < parts.Length - 1 && entry.UpState != 0)
                steps.Add(new MouseActionStep(entry.UpState, 0));
        }

        return steps;
    }

    public static IReadOnlyList<MouseActionStep> ParseMouseHoldRelease(string? expr)
    {
        if (string.IsNullOrWhiteSpace(expr))
            return Array.Empty<MouseActionStep>();

        var parts = expr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return Array.Empty<MouseActionStep>();

        if (NameToMouse.TryGetValue(parts[^1], out var entry) && entry.UpState != 0)
            return [new MouseActionStep(entry.UpState, 0)];

        return Array.Empty<MouseActionStep>();
    }

    /// <summary>
    /// Parses chord/sequence expressions such as "LCtrl+X", "LCtrl+K, LCtrl+D", or "LWin"
    /// into ordered key-down / key-up steps.
    /// </summary>
    public static IReadOnlyList<KeyActionStep> ParseKeySequence(string? expr)
    {
        var chords = ParseChords(expr);
        if (chords.Count == 0)
            return Array.Empty<KeyActionStep>();

        var steps = new List<KeyActionStep>();
        foreach (var chord in chords)
        {
            foreach (var key in chord)
                steps.Add(new KeyActionStep(key.ScanCode, key.IsE0, IsDown: true, key.IsE1));

            for (var i = chord.Count - 1; i >= 0; i--)
                steps.Add(new KeyActionStep(chord[i].ScanCode, chord[i].IsE0, IsDown: false, chord[i].IsE1));
        }

        return steps;
    }

    /// <summary>
    /// Parses press-side steps for hold-mirroring (runs preceding chords completely, then presses the final chord down).
    /// </summary>
    public static IReadOnlyList<KeyActionStep> ParseHoldPress(string? expr)
    {
        var chords = ParseChords(expr);
        if (chords.Count == 0)
            return Array.Empty<KeyActionStep>();

        var steps = new List<KeyActionStep>();
        for (var c = 0; c < chords.Count; c++)
        {
            var chord = chords[c];
            foreach (var key in chord)
                steps.Add(new KeyActionStep(key.ScanCode, key.IsE0, IsDown: true, key.IsE1));

            if (c < chords.Count - 1)
            {
                for (var i = chord.Count - 1; i >= 0; i--)
                    steps.Add(new KeyActionStep(chord[i].ScanCode, chord[i].IsE0, IsDown: false, chord[i].IsE1));
            }
        }

        return steps;
    }

    /// <summary>
    /// Parses release-side steps for hold-mirroring (releases the final chord in reverse order).
    /// </summary>
    public static IReadOnlyList<KeyActionStep> ParseHoldRelease(string? expr)
    {
        var chords = ParseChords(expr);
        if (chords.Count == 0)
            return Array.Empty<KeyActionStep>();

        var last = chords[^1];
        var steps = new List<KeyActionStep>(last.Count);
        for (var i = last.Count - 1; i >= 0; i--)
            steps.Add(new KeyActionStep(last[i].ScanCode, last[i].IsE0, IsDown: false, last[i].IsE1));

        return steps;
    }

    /// <summary>
    /// Checks whether an actual canonical key/button name matches a user-configured pattern
    /// (supports "*", comma-separated names, generic modifiers like "Ctrl", and mouse aliases like "Button4").
    /// </summary>
    public static bool KeyMatches(string? pattern, string? actualKey)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(actualKey))
            return false;

        var trimmedActual = actualKey.Trim();
        var tokens = pattern.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            if (token == "*" || SingleKeyMatches(token, trimmedActual))
                return true;
        }

        return false;
    }

    private static bool SingleKeyMatches(string pattern, string actual)
    {
        if (string.Equals(pattern, actual, StringComparison.OrdinalIgnoreCase))
            return true;

        if (pattern.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
            pattern.Equals("Control", StringComparison.OrdinalIgnoreCase))
            return actual.Equals("LCtrl", StringComparison.OrdinalIgnoreCase) ||
                   actual.Equals("RCtrl", StringComparison.OrdinalIgnoreCase);

        if (pattern.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            return actual.Equals("LShift", StringComparison.OrdinalIgnoreCase) ||
                   actual.Equals("RShift", StringComparison.OrdinalIgnoreCase);

        if (pattern.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            return actual.Equals("LAlt", StringComparison.OrdinalIgnoreCase) ||
                   actual.Equals("RAlt", StringComparison.OrdinalIgnoreCase);

        if (pattern.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
            pattern.Equals("Meta", StringComparison.OrdinalIgnoreCase) ||
            pattern.Equals("Super", StringComparison.OrdinalIgnoreCase))
            return actual.Equals("LWin", StringComparison.OrdinalIgnoreCase) ||
                   actual.Equals("RWin", StringComparison.OrdinalIgnoreCase);

        if (NameToMouse.TryGetValue(pattern, out var mouseEntry) &&
            mouseEntry.CanonicalName.Equals(actual, StringComparison.OrdinalIgnoreCase))
            return true;

        if (NameToScan.TryGetValue(pattern, out var scanEntry) &&
            ScanToName.TryGetValue((scanEntry.ScanCode, scanEntry.IsE0), out var canonical) &&
            canonical.Equals(actual, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static List<List<ScanEntry>> ParseChords(string? expr)
    {
        var result = new List<List<ScanEntry>>();
        if (string.IsNullOrWhiteSpace(expr))
            return result;

        var chordParts = expr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var chordPart in chordParts)
        {
            var keys = new List<ScanEntry>();
            var keyTokens = chordPart.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var token in keyTokens)
            {
                if (NameToScan.TryGetValue(token, out var entry))
                    keys.Add(entry);
            }

            if (keys.Count > 0)
                result.Add(keys);
        }

        return result;
    }
}
