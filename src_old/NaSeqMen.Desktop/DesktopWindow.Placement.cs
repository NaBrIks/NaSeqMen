// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Text.Json;

namespace NaSeqMen.Desktop;

internal sealed partial class DesktopWindow
{
    private sealed record Placement(int X, int Y, int Width, int Height, bool Maximized);
    private bool _restoreMaximized;
    private Placement? _savedPlacement;
    private readonly System.Windows.Forms.Timer _placementTimer = new() { Interval = 350 };
    private string PlacementPath => Path.Combine(_root, "local", "window-placement.json");

    private void WatchPlacement()
    {
        _placementTimer.Tick += (_, _) =>
        {
            _placementTimer.Stop();
            if (WindowState != FormWindowState.Minimized) SavePlacement();
        };
        Shown += Schedule;
        LocationChanged += Schedule;
        SizeChanged += Schedule;
        ResizeEnd += (_, _) =>
        {
            _placementTimer.Stop();
            if (WindowState != FormWindowState.Minimized) SavePlacement();
        };

        void Schedule(object? sender, EventArgs args)
        {
            if (_closingStarted || !Visible || WindowState == FormWindowState.Minimized) return;
            _placementTimer.Stop();
            _placementTimer.Start();
        }
    }

    private void RestorePlacement()
    {
        try
        {
            var placement = JsonSerializer.Deserialize<Placement>(File.ReadAllText(PlacementPath));
            if (placement is null || placement.Width < 1 || placement.Height < 1) return;
            _savedPlacement = placement;

            var bounds = new Rectangle(placement.X, placement.Y, placement.Width, placement.Height);
            // Leave a reachable title bar after a monitor is disconnected or changes resolution.
            var top = new Rectangle(bounds.X, bounds.Y, bounds.Width, Math.Min(bounds.Height, 42));
            if (!Screen.AllScreens.Any(screen =>
                {
                    var visible = Rectangle.Intersect(screen.WorkingArea, top);
                    return visible.Width >= 160 && visible.Height >= 24;
                }))
            {
                var workArea = Screen.FromRectangle(bounds).WorkingArea;
                var width = Math.Clamp(bounds.Width, MinimumSize.Width, Math.Max(MinimumSize.Width, workArea.Width));
                var height = Math.Clamp(bounds.Height, MinimumSize.Height, Math.Max(MinimumSize.Height, workArea.Height));
                bounds = new Rectangle(
                    Math.Clamp(bounds.X, workArea.Left, Math.Max(workArea.Left, workArea.Right - width)),
                    Math.Clamp(bounds.Y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height)),
                    width, height);
            }
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            _restoreMaximized = placement.Maximized;
            if (_restoreMaximized) WindowState = FormWindowState.Maximized;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine(error.Message);
        }
    }

    private void SavePlacement()
    {
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        if (bounds.Width < 1 || bounds.Height < 1) return;
        var maximized = WindowState == FormWindowState.Maximized
            || WindowState == FormWindowState.Minimized && _restoreMaximized;
        var placement = new Placement(bounds.X, bounds.Y, bounds.Width, bounds.Height, maximized);
        if (placement == _savedPlacement) return;
        var path = PlacementPath;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(placement));
                File.Move(temporary, path, overwrite: true);
                _savedPlacement = placement;
            }
            finally { File.Delete(temporary); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(error.Message);
        }
    }
}
