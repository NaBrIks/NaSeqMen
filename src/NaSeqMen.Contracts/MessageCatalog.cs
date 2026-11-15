// SPDX-License-Identifier: MIT
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NaSeqMen.Contracts;

public sealed record LocalizedMessage(string Code, Dictionary<string, object?> Args, string Message);
public sealed class LocalizedException(LocalizedMessage issue) : Exception(issue.Message)
{
    public LocalizedMessage Issue { get; } = issue;
}
public static partial class MessageCatalog
{
    private const string ExceptionKey = "NaSeqMen.LocalizedMessage";
    public static IReadOnlyDictionary<string, Dictionary<string, string>> Catalog { get; } = Load();
    public static LocalizedMessage M(string code, params (string Name, object? Value)[] arguments)
    {
        var args = arguments.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);
        return new(code, args, Format(code, args, "en"));
    }
    public static string Format(string code, IReadOnlyDictionary<string, object?>? args = null, string language = "en")
    {
        var catalog = Catalog.TryGetValue(language, out var selected) ? selected : Catalog["en"];
        var template = catalog.GetValueOrDefault(code) ?? Catalog["en"].GetValueOrDefault(code) ?? code;
        return Placeholder().Replace(template, match => args is not null && args.TryGetValue(match.Groups[1].Value, out var value)
            ? value is LocalizedMessage nested ? Format(nested.Code, nested.Args, language) : Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
            : match.Value);
    }
    public static T Error<T>(LocalizedMessage message) where T : Exception
        => WithMessage((T)Activator.CreateInstance(typeof(T), message.Message)!, message);
    public static T WithMessage<T>(T error, LocalizedMessage message) where T : Exception
    { error.Data[ExceptionKey] = message; return error; }
    public static AggregateException Aggregate(LocalizedMessage message, IEnumerable<Exception> errors)
        => WithMessage(new AggregateException(message.Message, errors), message);
    public static Win32Exception Win32(int errorCode, LocalizedMessage message)
        => WithMessage(new Win32Exception(errorCode, message.Message), message);
    public static LocalizedMessage FromException(Exception error)
        => error is LocalizedException localized ? localized.Issue
        : error.Data[ExceptionKey] is LocalizedMessage message ? message
        : M("error.external", ("detail", error.Message));
    public static object ApiError(LocalizedMessage issue)
        => new { code = issue.Code, args = issue.Args, message = issue.Message, error = issue.Message };
    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        using var stream = typeof(MessageCatalog).Assembly.GetManifestResourceStream("NaSeqMen.Contracts.messages.json")
            ?? throw new InvalidOperationException("The built-in message catalog is missing.");
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream)!;
    }
    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9_]*)\}")]
    private static partial Regex Placeholder();
}
