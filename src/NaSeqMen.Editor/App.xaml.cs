// SPDX-License-Identifier: MIT
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NaSeqMen.Editor;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        var smoke = Array.IndexOf(e.Args, "--smoke");
        if (smoke >= 0 && smoke + 1 < e.Args.Length)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await window.InitializeAsync();
            var content = (FrameworkElement)window.Content;
            if (e.Args.Contains("--smoke-devices") && content is System.Windows.Controls.Grid grid)
                grid.Children.OfType<System.Windows.Controls.TabControl>().Single().SelectedIndex = 1;
            window.Content = null;
            var widthIndex = Array.IndexOf(e.Args, "--smoke-width");
            var heightIndex = Array.IndexOf(e.Args, "--smoke-height");
            var width = widthIndex >= 0 && widthIndex + 1 < e.Args.Length ? Math.Clamp(int.Parse(e.Args[widthIndex + 1]), 850, 3840) : 1280;
            var height = heightIndex >= 0 && heightIndex + 1 < e.Args.Length ? Math.Clamp(int.Parse(e.Args[heightIndex + 1]), 550, 2160) : 820;
            var surface = new System.Windows.Controls.Border { Background = window.Background, Child = content, Width = width, Height = height };
            System.Windows.Documents.TextElement.SetForeground(surface, window.Foreground);
            System.Windows.Documents.TextElement.SetFontFamily(surface, window.FontFamily);
            System.Windows.Documents.TextElement.SetFontSize(surface, window.FontSize);
            surface.Measure(new Size(width, height));
            surface.Arrange(new Rect(0, 0, width, height));
            surface.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.GetFullPath(e.Args[smoke + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var output = File.Create(path)) encoder.Save(output);
            await window.DisconnectAsync();
            Shutdown();
            return;
        }
        window.Show();
        await window.InitializeAsync();
    }
}
