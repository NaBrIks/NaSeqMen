using System.Text.Json;
using NaSeqMen.Core.Messages;

namespace NaSeqMen.Nodes.Interception;

/// <summary>
/// Extracts Interception stroke decisions and injection actions from <see cref="FlowMessage"/> baskets.
/// </summary>
internal static class DecisionParser
{
    public static string? GetCorrelationId(FlowMessage message)
    {
        if (!message.Properties.TryGetValue("correlationId", out var raw) || raw is null)
            return null;

        var value = ToStringValue(raw)?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public static StrokeDecision ParseDecision(FlowMessage message, string defaultAction, bool allowPayloadFallback)
    {
        var defaultSuppress = string.Equals(defaultAction, "suppress", StringComparison.OrdinalIgnoreCase);
        var suppress = TryReadBool(message.Properties, "suppress") ?? defaultSuppress;

        var sendKeys = ReadKeySteps(message.Properties, "sendKeys");
        var sendMouse = ReadMouseSteps(message.Properties, "sendMouse");

        // Optional fallback for direct injection when user feeds a plain key/mouse string payload into `return`.
        if (allowPayloadFallback && sendKeys is null && sendMouse is null)
        {
            var payloadExpr = message.PayloadAsString().Trim();
            if (!string.IsNullOrEmpty(payloadExpr))
            {
                if (ScanCodeMap.IsMouseActionExpression(payloadExpr))
                {
                    var parsedMouse = ScanCodeMap.ParseMouseSequence(payloadExpr);
                    if (parsedMouse.Count > 0)
                        sendMouse = parsedMouse;
                }
                else
                {
                    var parsedKeys = ScanCodeMap.ParseKeySequence(payloadExpr);
                    if (parsedKeys.Count > 0)
                        sendKeys = parsedKeys;
                }
            }
        }

        var targetSlot = TryReadInt(message.Properties, "sendDeviceSlot") ?? 0;
        var targetHwId = TryReadString(message.Properties, "sendHardwareId");
        var blockActions = TryReadBool(message.Properties, "blockActions") == true;

        return new StrokeDecision(suppress, sendKeys, sendMouse, targetSlot, targetHwId, blockActions);
    }

    public static bool? TryReadBool(IReadOnlyDictionary<string, object?> props, string key)
    {
        if (!props.TryGetValue(key, out var raw) || raw is null)
            return null;

        return raw switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.String } je => ParseBoolText(je.GetString()),
            string s => ParseBoolText(s),
            _ => null,
        };
    }

    public static int? TryReadInt(IReadOnlyDictionary<string, object?> props, string key)
    {
        if (!props.TryGetValue(key, out var raw) || raw is null)
            return null;

        return raw switch
        {
            int i => i,
            long l => (int)l,
            short s => s,
            ushort u => u,
            double d => (int)d,
            JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetInt32(out var ji) => ji,
            JsonElement { ValueKind: JsonValueKind.String } je when int.TryParse(je.GetString(), out var js) => js,
            string str when int.TryParse(str, out var parsed) => parsed,
            _ => null,
        };
    }

    public static string? TryReadString(IReadOnlyDictionary<string, object?> props, string key)
    {
        if (!props.TryGetValue(key, out var raw) || raw is null)
            return null;

        var s = ToStringValue(raw)?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static IReadOnlyList<KeyActionStep>? ReadKeySteps(IReadOnlyDictionary<string, object?> props, string key)
    {
        if (!props.TryGetValue(key, out var raw) || raw is null)
            return null;

        if (raw is IReadOnlyList<KeyActionStep> list)
            return list.Count > 0 ? list : null;

        if (raw is IEnumerable<KeyActionStep> seq)
        {
            var materialized = seq.ToList();
            return materialized.Count > 0 ? materialized : null;
        }

        var expr = ToStringValue(raw);
        if (string.IsNullOrWhiteSpace(expr))
            return null;

        var steps = ScanCodeMap.ParseKeySequence(expr);
        return steps.Count > 0 ? steps : null;
    }

    private static IReadOnlyList<MouseActionStep>? ReadMouseSteps(IReadOnlyDictionary<string, object?> props, string key)
    {
        if (!props.TryGetValue(key, out var raw) || raw is null)
            return null;

        if (raw is IReadOnlyList<MouseActionStep> list)
            return list.Count > 0 ? list : null;

        if (raw is IEnumerable<MouseActionStep> seq)
        {
            var materialized = seq.ToList();
            return materialized.Count > 0 ? materialized : null;
        }

        var expr = ToStringValue(raw);
        if (string.IsNullOrWhiteSpace(expr))
            return null;

        var steps = ScanCodeMap.ParseMouseSequence(expr);
        return steps.Count > 0 ? steps : null;
    }

    private static bool? ParseBoolText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();
        if (bool.TryParse(trimmed, out var b))
            return b;

        if (trimmed.Equals("suppress", StringComparison.OrdinalIgnoreCase))
            return true;

        if (trimmed.Equals("pass", StringComparison.OrdinalIgnoreCase))
            return false;

        return null;
    }

    private static string? ToStringValue(object raw)
    {
        return raw switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
            JsonElement je => je.GetRawText(),
            _ => raw.ToString(),
        };
    }
}
