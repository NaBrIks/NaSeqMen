// SPDX-License-Identifier: MIT
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NaSeqMen.Desktop;

internal sealed partial class DesktopWindow
{
    private Form? _debugWindow;

    private async void OpenDebugWindow(CoreWebView2Environment environment, string uri)
    {
        if (_debugWindow is { IsDisposed: false } existing)
        {
            if (existing.Controls.OfType<WebView2>().Single().CoreWebView2 is { } current &&
                current.Source != uri) current.Navigate(uri);
            existing.Activate();
            return;
        }

        var window = new Form { Text = "NaSeqMen · Debug", ClientSize = new Size(1000, 580),
            MinimumSize = new Size(600, 300), StartPosition = FormStartPosition.CenterParent };
        var view = new WebView2 { Dock = DockStyle.Fill };
        window.Controls.Add(view);
        _debugWindow = window;
        window.FormClosed += (_, _) => _debugWindow = null;
        window.Show(this);
        try
        {
            await view.EnsureCoreWebView2Async(environment);
            if (window.IsDisposed) return;
            var core = view.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.NavigationStarting += (_, e) =>
                e.Cancel = !NavigationPolicy.IsEditor(e.Uri, _services.EditorUri);
            core.DocumentTitleChanged += (_, _) => window.Text = core.DocumentTitle;
            core.Navigate(uri);
        }
        catch (Exception error)
        {
            Debug.WriteLine(error);
            window.Close();
        }
    }
}
