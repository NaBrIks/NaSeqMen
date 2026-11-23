using System.Globalization;
using System.Text.Json;

namespace NaSeqMen.Nodes.Controls;

internal static class NumericPayloadHelper
{
    public static bool TryExtractDouble(object? value, out double result)
    {
        switch (value)
        {
            case double d when double.IsFinite(d):
                result = d;
                return true;
            case float f when float.IsFinite(f):
                result = f;
                return true;
            case int i:
                result = i;
                return true;
            case long l:
                result = l;
                return true;
            case short s:
                result = s;
                return true;
            case byte b:
                result = b;
                return true;
            case decimal m:
                result = (double)m;
                return true;
            case JsonElement je:
                return TryExtractFromJson(je, out result);
            case string str:
                return double.TryParse(
                    str.Trim(),
                    NumberStyles.Float | NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out result) && double.IsFinite(result);
            default:
                result = 0;
                return false;
        }
    }

    private static bool TryExtractFromJson(JsonElement element, out double result)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var n) && double.IsFinite(n))
        {
            result = n;
            return true;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var str = element.GetString();
            if (!string.IsNullOrWhiteSpace(str)
                && double.TryParse(
                    str.Trim(),
                    NumberStyles.Float | NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out var parsed)
                && double.IsFinite(parsed))
            {
                result = parsed;
                return true;
            }
        }

        result = 0;
        return false;
    }
}
