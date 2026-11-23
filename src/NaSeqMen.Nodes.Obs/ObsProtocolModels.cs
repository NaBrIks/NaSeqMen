using System.Globalization;
using System.Text.Json;

namespace NaSeqMen.Nodes.Obs;

public enum ObsConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    AuthFailed,
}

public sealed record ObsEvent(
    string EventType,
    int EventIntent,
    JsonElement? EventData);

public sealed record ObsRequestResponse(
    string RequestType,
    string RequestId,
    bool Result,
    int Code,
    string? Comment,
    JsonElement? ResponseData);

internal static class ObsJsonHelpers
{
    public static bool TryGetInt32(JsonElement obj, string key, out int value)
    {
        value = 0;
        return obj.ValueKind == JsonValueKind.Object &&
               obj.TryGetProperty(key, out var prop) &&
               prop.ValueKind == JsonValueKind.Number &&
               prop.TryGetInt32(out value);
    }

    public static bool TryGetDouble(JsonElement obj, string key, out double value)
    {
        value = 0;
        return obj.ValueKind == JsonValueKind.Object &&
               obj.TryGetProperty(key, out var prop) &&
               prop.ValueKind == JsonValueKind.Number &&
               prop.TryGetDouble(out value);
    }

    public static bool TryGetBool(JsonElement obj, string key, out bool value)
    {
        value = false;
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.True) { value = true; return true; }
            if (prop.ValueKind == JsonValueKind.False) { value = false; return true; }
        }
        return false;
    }

    public static bool TryGetString(JsonElement obj, string key, out string value)
    {
        value = string.Empty;
        if (obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty(key, out var prop) &&
            prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString() ?? string.Empty;
            return true;
        }
        return false;
    }

    public static string? GetStringOrNull(JsonElement obj, string key)
        => TryGetString(obj, key, out var value) ? value : null;

    public static double ExtractChannelPeak(JsonElement channel)
    {
        if (channel.ValueKind != JsonValueKind.Array)
            return 0.0;

        var len = channel.GetArrayLength();
        if (len >= 2 && channel[1].ValueKind == JsonValueKind.Number && channel[1].TryGetDouble(out var peak))
            return Math.Max(0.0, peak);
        if (len >= 1 && channel[0].ValueKind == JsonValueKind.Number && channel[0].TryGetDouble(out var mag))
            return Math.Max(0.0, mag);
        return 0.0;
    }

    public static bool TryParseDouble(object? value, out double result)
    {
        switch (value)
        {
            case double d: result = d; return true;
            case float f: result = f; return true;
            case int i: result = i; return true;
            case long l: result = l; return true;
            case decimal m: result = (double)m; return true;
            case JsonElement je when je.ValueKind == JsonValueKind.Number && je.TryGetDouble(out result): return true;
            case JsonElement je when je.ValueKind == JsonValueKind.String:
                return double.TryParse(je.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
            case string s:
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
            default:
                result = 0;
                return false;
        }
    }

    public static bool TryParseExplicitBool(object? value, out bool result)
    {
        switch (value)
        {
            case bool b: result = b; return true;
            case JsonElement je when je.ValueKind == JsonValueKind.True: result = true; return true;
            case JsonElement je when je.ValueKind == JsonValueKind.False: result = false; return true;
            case JsonElement je when je.ValueKind == JsonValueKind.String:
                return bool.TryParse(je.GetString()?.Trim(), out result);
            case string s:
                return bool.TryParse(s.Trim(), out result);
            default:
                result = false;
                return false;
        }
    }

    public static string ResolveStartStopToggle(object? payload, string startReq, string stopReq, string toggleReq)
    {
        if (TryParseExplicitBool(payload, out var b))
            return b ? startReq : stopReq;

        var text = payload switch
        {
            string s => s.Trim(),
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString()?.Trim() ?? string.Empty,
            _ => string.Empty,
        };

        if (string.Equals(text, "start", StringComparison.OrdinalIgnoreCase))
            return startReq;
        if (string.Equals(text, "stop", StringComparison.OrdinalIgnoreCase))
            return stopReq;
        return toggleReq;
    }

    public static bool TryExtractRawCommand(object? payload, out string requestType, out object? requestData)
    {
        requestType = string.Empty;
        requestData = null;

        switch (payload)
        {
            case string s when !string.IsNullOrWhiteSpace(s):
                requestType = s.Trim();
                return true;

            case JsonElement je when je.ValueKind == JsonValueKind.String:
                requestType = je.GetString()?.Trim() ?? string.Empty;
                return !string.IsNullOrEmpty(requestType);

            case JsonElement je when je.ValueKind == JsonValueKind.Object:
                if (TryGetString(je, "requestType", out var rt) && !string.IsNullOrWhiteSpace(rt))
                {
                    requestType = rt.Trim();
                    if (je.TryGetProperty("requestData", out var rd))
                        requestData = rd;
                    return true;
                }
                return false;

            case IReadOnlyDictionary<string, object?> dict:
                if (dict.TryGetValue("requestType", out var rto) && rto is not null)
                {
                    requestType = rto.ToString()?.Trim() ?? string.Empty;
                    dict.TryGetValue("requestData", out requestData);
                    return !string.IsNullOrEmpty(requestType);
                }
                return false;

            case IDictionary<string, object?> dict:
                if (dict.TryGetValue("requestType", out var rto2) && rto2 is not null)
                {
                    requestType = rto2.ToString()?.Trim() ?? string.Empty;
                    dict.TryGetValue("requestData", out requestData);
                    return !string.IsNullOrEmpty(requestType);
                }
                return false;

            default:
                return false;
        }
    }
}
