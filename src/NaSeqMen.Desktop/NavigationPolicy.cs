// SPDX-License-Identifier: MIT
namespace NaSeqMen.Desktop;

public static class NavigationPolicy
{
    public static bool IsEditor(string address, Uri editor)
        => Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && uri.Scheme == editor.Scheme && uri.Host == editor.Host && uri.Port == editor.Port
        && string.IsNullOrEmpty(uri.UserInfo);

    public static bool CanOpenExternal(string address, bool userInitiated, bool redirected)
        => userInitiated && !redirected && Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo);
}
