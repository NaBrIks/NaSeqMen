// SPDX-License-Identifier: MIT
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NaSeqMen.Desktop;

internal sealed partial class DesktopWindow : Form
{
    private readonly string _root;
    private readonly DesktopServices _services;
    private readonly CancellationTokenSource _closing = new();
    private readonly Panel _startup = new() { Dock = DockStyle.Fill };
    private readonly Label _message = new() { AutoEllipsis = true, Location = new Point(8, 9), Size = new Size(680, 24), Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right, Text = NativeText.Get("loading") };
    private readonly Button _retry = new() { Text = NativeText.Get("retry"), Location = new Point(8, 39), Size = new Size(100, 28), Enabled = false };
    private readonly Button _details = new() { Text = NativeText.Get("details"), Location = new Point(112, 39), Size = new Size(116, 28), Enabled = false };
    private WebView2? _webView;
    private Task? _initialization;
    private Exception? _lastError;
    private bool _closingStarted, _closeAllowed;

    public DesktopWindow(string root)
    {
        _root = root;
        _services = new DesktopServices(root);
        Text = "NaSeqMen";
        ClientSize = new Size(1280, 820);
        MinimumSize = new Size(850, 550);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        RestorePlacement();
        Resize += (_, _) =>
        {
            if (WindowState != FormWindowState.Minimized)
                _restoreMaximized = WindowState == FormWindowState.Maximized;
        };
        _startup.Size = ClientSize;
        _message.Width = ClientSize.Width - 16;
        _startup.Controls.AddRange([_message, _retry, _details]);
        Controls.Add(_startup);
        _retry.Click += (_, _) => BeginInitialization();
        _details.Click += (_, _) => ShowDetails();
        Shown += (_, _) => BeginInitialization();
        FormClosing += OnClosingAsync;
    }

    private void BeginInitialization()
    {
        if (_initialization is { IsCompleted: false } || _closingStarted) return;
        _initialization = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        _retry.Enabled = false; _details.Enabled = false;
        _message.Text = NativeText.Get("loading");
        try
        {
            await _services.EnsureReadyAsync(_closing.Token);
            _closing.Token.ThrowIfCancellationRequested();
            _webView?.Dispose();
            _webView = new WebView2 { Dock = DockStyle.Fill, Visible = false, DefaultBackgroundColor = BackColor };
            Controls.Add(_webView);
            var profile = Path.Combine(_root, "local", "webview2");
            Directory.CreateDirectory(profile);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile).WaitAsync(_closing.Token);
            await _webView.EnsureCoreWebView2Async(environment).WaitAsync(_closing.Token);
            _closing.Token.ThrowIfCancellationRequested();
            var core = _webView.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            var integratedCaption = await PrepareCaptionAsync(core);
            _closing.Token.ThrowIfCancellationRequested();
            if (integratedCaption)
            {
                core.WebMessageReceived += OnCaptionThemeReceived;
                core.Settings.IsWebMessageEnabled = true;
            }
            core.NavigationStarting += (_, e) =>
            {
                if (NavigationPolicy.IsEditor(e.Uri, _services.EditorUri)) return;
                e.Cancel = true;
                if (NavigationPolicy.CanOpenExternal(e.Uri, e.IsUserInitiated, e.IsRedirected)) OpenExternal(e.Uri);
            };
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (!e.IsUserInitiated) return;
                if (NavigationPolicy.IsEditor(e.Uri, _services.EditorUri)) core.Navigate(e.Uri);
                else if (NavigationPolicy.CanOpenExternal(e.Uri, true, false)) OpenExternal(e.Uri);
            };
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.NavigationCompleted += (_, e) =>
            {
                if (_closingStarted) return;
                if (e.IsSuccess)
                {
                    _startup.Visible = false;
                    _webView.Visible = true;
                    _webView.BringToFront();
                    if (integratedCaption) ShowIntegratedCaption();
                    _webView.Focus();
                }
                else ShowStartupError(new IOException(NativeText.Get("navigationFailed", e.WebErrorStatus)));
            };
            core.Navigate(_services.EditorUri.AbsoluteUri);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex) { if (!_closingStarted) ShowStartupError(ex); }
    }

    private void ShowStartupError(Exception error)
    {
        _lastError = error;
        HideIntegratedCaption();
        _message.Text = error is WebView2RuntimeNotFoundException
            ? NativeText.Get("runtimeMissing")
            : NativeText.Get("startupFailed", error.Message);
        _retry.Enabled = true; _details.Enabled = true;
        if (_webView is not null) _webView.Visible = false;
        _startup.Visible = true; _startup.BringToFront();
    }

    private void ShowDetails()
    {
        using var dialog = new Form { Text = NativeText.Get("detailsTitle"), ClientSize = new Size(720, 320), StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false };
        dialog.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Text = _lastError?.ToString() ?? "" });
        dialog.ShowDialog(this);
    }

    private void OpenExternal(string address)
    {
        try { Process.Start(new ProcessStartInfo(address) { UseShellExecute = true }); }
        catch (Exception ex) { _lastError = ex; Text = NativeText.Get("externalLinkFailed"); }
    }

    private async void OnClosingAsync(object? sender, FormClosingEventArgs e)
    {
        if (_closeAllowed) return;
        e.Cancel = true;
        if (_closingStarted) return;
        _closingStarted = true;
        SavePlacement();
        _closing.Cancel();
        try
        {
            if (_initialization is not null) await _initialization;
            _webView?.Dispose();
            await _services.DisposeAsync();
        }
        finally
        {
            _closing.Dispose();
            _closeAllowed = true;
            Close();
        }
    }
}
