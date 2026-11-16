// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace NaSeqMen.Desktop;

internal sealed partial class DesktopWindow
{
    private const int CalculateNonClientSize = 0x0083;
    private readonly Panel _caption = new() { Visible = false, BackColor = SystemColors.Control };
    private Button? _maximize;
    private ToolTip? _captionTips;
    private bool _integratedCaption;
    private CaptionTheme _captionTheme = new(SystemColors.Control, SystemColors.ControlText, SystemColors.ControlDark);

    private readonly record struct CaptionTheme(Color Background, Color Text, Color Border);

    private static async Task<bool> PrepareCaptionAsync(CoreWebView2 core)
    {
        try
        {
            core.Settings.IsNonClientRegionSupportEnabled = true;
            await core.AddScriptToExecuteOnDocumentCreatedAsync("""
                if (window === window.top) {
                    const publishCaptionTheme = () => {
                        const shell = document.getElementById('shell');
                        if (!shell) return;
                        const style = getComputedStyle(shell);
                        chrome.webview.postMessage({
                            type: 'caption-theme',
                            background: style.getPropertyValue('--lu-topbar-bg').trim(),
                            text: style.getPropertyValue('--lu-topbar-text').trim(),
                            border: style.getPropertyValue('--lu-topbar-border').trim()
                        });
                    };
                    document.addEventListener('shell-appearance', publishCaptionTheme);
                    document.addEventListener('DOMContentLoaded', () => {
                        document.documentElement.classList.add('desktop-caption');
                        publishCaptionTheme();
                    }, { once: true });
                }
                """);
            return true;
        }
        catch (NotImplementedException) { return false; }
    }

    private void OnCaptionThemeReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_closingStarted || !NavigationPolicy.IsEditor(e.Source, _services.EditorUri)) return;
        var json = e.WebMessageAsJson;
        if (json.Length > 256) return;
        using var message = JsonDocument.Parse(json);
        var root = message.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
            || !type.ValueEquals("caption-theme")
            || !TryReadColor(root, "background", out var background)
            || !TryReadColor(root, "text", out var text)
            || !TryReadColor(root, "border", out var border))
            return;

        _captionTheme = new CaptionTheme(background, text, border);
        ApplyCaptionTheme();
    }

    private static bool TryReadColor(JsonElement message, string name, out Color color)
    {
        color = default;
        if (!message.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { Length: 7 } rgb || rgb[0] != '#'
            || !int.TryParse(rgb.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var colorValue))
            return false;
        color = Color.FromArgb(unchecked((int)0xff000000) | colorValue);
        return true;
    }

    private void ApplyCaptionTheme()
    {
        _caption.BackColor = _captionTheme.Background;
        foreach (Button button in _caption.Controls)
        {
            button.BackColor = _captionTheme.Background;
            button.ForeColor = _captionTheme.Text;
            button.FlatAppearance.MouseOverBackColor = _captionTheme.Border;
            button.FlatAppearance.MouseDownBackColor = _captionTheme.Border;
        }
    }

    private void ShowIntegratedCaption()
    {
        if (_caption.Parent is null)
        {
            _captionTips = new ToolTip();
            Disposed += (_, _) => _captionTips.Dispose();
            _caption.Controls.Add(MakeCaptionButton("−", "windowMinimize", () => WindowState = FormWindowState.Minimized));
            _maximize = MakeCaptionButton("□", "windowMaximize", () => WindowState =
                WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized);
            _caption.Controls.Add(_maximize);
            _caption.Controls.Add(MakeCaptionButton("×", "windowClose", Close));
            Controls.Add(_caption);
            ApplyCaptionTheme();
            ClientSizeChanged += (_, _) => LayoutCaption();
            DpiChanged += (_, _) => LayoutCaption();
        }
        _integratedCaption = true;
        RefreshNonClientFrame();
        LayoutCaption();
        _caption.Visible = true;
        _caption.BringToFront();
    }

    private void HideIntegratedCaption()
    {
        if (!_integratedCaption) return;
        _caption.Visible = false;
        _integratedCaption = false;
        RefreshNonClientFrame();
    }

    private Button MakeCaptionButton(string glyph, string label, Action action)
    {
        var button = new Button { Text = glyph, FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false,
            TabStop = false, AccessibleName = NativeText.Get(label) };
        button.FlatAppearance.BorderSize = 0;
        _captionTips!.SetToolTip(button, button.AccessibleName);
        button.Click += (_, _) => action();
        return button;
    }

    private void LayoutCaption()
    {
        if (_maximize is null) return;
        var buttonWidth = LogicalToDeviceUnits(42);
        var height = LogicalToDeviceUnits(41);
        _caption.SetBounds(Math.Max(0, ClientSize.Width - buttonWidth * 3), 0, buttonWidth * 3, height);
        for (var index = 0; index < _caption.Controls.Count; index++)
            _caption.Controls[index].SetBounds(index * buttonWidth, 0, buttonWidth, height);
        var maximized = WindowState == FormWindowState.Maximized;
        _maximize.Text = maximized ? "❐" : "□";
        _maximize.AccessibleName = NativeText.Get(maximized ? "windowRestore" : "windowMaximize");
        _captionTips!.SetToolTip(_maximize, _maximize.AccessibleName);
    }

    protected override void WndProc(ref Message message)
    {
        if (!_integratedCaption || message.Msg != CalculateNonClientSize || message.WParam == 0)
        {
            base.WndProc(ref message);
            return;
        }
        // Keep the native resize border on all edges while reclaiming the vertical caption area.
        var top = Marshal.ReadInt32(message.LParam, 4);
        var bottom = Marshal.ReadInt32(message.LParam, 12);
        base.WndProc(ref message);
        var border = Math.Max(0, bottom - Marshal.ReadInt32(message.LParam, 12));
        Marshal.WriteInt32(message.LParam, 4, top + border);
        message.Result = 0;
    }

    private void RefreshNonClientFrame() => SetWindowPos(Handle, 0, 0, 0, 0, 0, 0x37);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
