// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Resources;

namespace NaSeqMen.Desktop;

internal static class NativeText
{
    private static readonly ResourceManager Resources = new("NaSeqMen.Desktop.DesktopText", typeof(NativeText).Assembly);
    // The native startup surface has no browser preferences yet. English is the product default.
    internal static CultureInfo Culture { get; set; } = CultureInfo.GetCultureInfo("en");
    internal static string Get(string key, params object[] arguments)
        => string.Format(Culture, Resources.GetString(key, Culture) ?? key, arguments);
}
