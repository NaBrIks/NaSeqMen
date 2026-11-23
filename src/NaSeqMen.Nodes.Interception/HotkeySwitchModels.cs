using System.Text.Json;

namespace NaSeqMen.Nodes.Interception;

public sealed record HotkeyRuleDto
{
    public string From { get; init; } = string.Empty;
    public string To { get; init; } = string.Empty;
    public string Mode { get; init; } = "tapOnDown";
    public bool Enabled { get; init; } = true;
}

internal sealed class CompiledHotkeyRule
{
    public required string From { get; init; }
    public required string To { get; init; }
    public required string Mode { get; init; }
    public required IReadOnlyList<string> Modifiers { get; init; }
    public required string TriggerKey { get; init; }

    public bool HasModifiers => Modifiers.Count > 0;
}

internal static class HotkeyRuleParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IReadOnlyList<CompiledHotkeyRule> ParseRules(IReadOnlyDictionary<string, JsonElement> parameters)
    {
        if (!parameters.TryGetValue("rules", out var elem))
            return Array.Empty<CompiledHotkeyRule>();

        var dtos = new List<HotkeyRuleDto>();

        if (elem.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in elem.EnumerateArray())
            {
                try
                {
                    var dto = item.Deserialize<HotkeyRuleDto>(JsonOptions);
                    if (dto is not null)
                        dtos.Add(dto);
                }
                catch
                {
                    // Skip malformed rule entry
                }
            }
        }
        else if (elem.ValueKind == JsonValueKind.String)
        {
            var raw = elem.GetString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var trimmed = raw.Trim();
                if (trimmed.StartsWith('['))
                {
                    try
                    {
                        var parsed = JsonSerializer.Deserialize<List<HotkeyRuleDto>>(trimmed, JsonOptions);
                        if (parsed is not null)
                            dtos.AddRange(parsed);
                    }
                    catch { }
                }
                else
                {
                    // Simple fallback format: "LWin+D => LCtrl+X" per line
                    foreach (var line in trimmed.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var sep = line.IndexOf("=>", StringComparison.Ordinal);
                        if (sep <= 0)
                            continue;
                        dtos.Add(new HotkeyRuleDto
                        {
                            From = line[..sep].Trim(),
                            To = line[(sep + 2)..].Trim(),
                            Mode = "tapOnDown",
                            Enabled = true,
                        });
                    }
                }
            }
        }

        var compiled = new List<CompiledHotkeyRule>(dtos.Count);
        foreach (var dto in dtos)
        {
            if (!dto.Enabled)
                continue;

            var rule = Compile(dto);
            if (rule is not null)
                compiled.Add(rule);
        }

        // Most-specific rules (more modifiers) checked first
        compiled.Sort((a, b) => b.Modifiers.Count.CompareTo(a.Modifiers.Count));
        return compiled;
    }

    public static CompiledHotkeyRule? Compile(HotkeyRuleDto dto)
    {
        var from = dto.From?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(from))
            return null;

        var parts = from
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CanonicalizeToken)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        if (parts.Count == 0)
            return null;

        var trigger = parts[^1];
        var mods = parts.Count > 1 ? parts.GetRange(0, parts.Count - 1) : [];

        return new CompiledHotkeyRule
        {
            From = from,
            To = dto.To?.Trim() ?? string.Empty,
            Mode = string.IsNullOrWhiteSpace(dto.Mode) ? "tapOnDown" : dto.Mode.Trim(),
            Modifiers = mods,
            TriggerKey = trigger,
        };
    }

    public static string CanonicalizeToken(string raw)
    {
        var token = raw.Trim();
        if (ScanCodeMap.TryGetScanCode(token, out var sc, out var isE0))
            return ScanCodeMap.GetKeyName(sc, isE0);

        if (ScanCodeMap.TryGetMouseButton(token, out var canonicalMouse, out _, out _, out _))
            return canonicalMouse;

        return token;
    }
}
