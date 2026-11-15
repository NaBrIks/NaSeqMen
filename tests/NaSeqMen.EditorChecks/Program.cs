// SPDX-License-Identifier: MIT
using System.Collections;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NaSeqMen.Contracts;
using NaSeqMen.Editor;

internal static class Program
{
    private static int _checks;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static void Main(string[] args)
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var window = new MainWindow(); // Deliberately never InitializeAsync: no connection to a real executor.
        var network = new ForbiddenNetwork();
        ((HttpClient)Field(window, "_http")).Dispose();
        typeof(MainWindow).GetField("_http", Private)!.SetValue(window, new HttpClient(network));
        var devices = (IList)Field(window, "_devices");
        var inputs = (IList)Field(window, "_inputs");
        var configuration = (Configuration)Field(window, "_configuration");
        configuration.Mappings.Add(new() { Source = "test-mouse", Control = "mouse:button4", Name = "Копировать" });
        var before = JsonSerializer.Serialize(configuration);
        var mouse = Device("test-mouse", "Мышь — длинное имя составного устройства", "mouse");
        var keyboard = Device("test-keyboard", "Клавиатурная часть мыши", "keyboard");
        var other = Device("test-other", "Другая мышь", "mouse");
        var hid = Device("test-hid", "Служебный HID", "hid");
        foreach (var device in new[] { mouse, keyboard, other, hid }) devices.Add(device);
        Call(window, "SetDiagnosticsConnected", true);
        Toggle(window, mouse);
        Toggle(window, keyboard);
        Assert(Get<bool>(mouse, "Observing") && Get<bool>(keyboard, "Observing"), "multiple interfaces can be observed independently");
        Assert(!Get<bool>(hid, "CanObserve"), "unsupported HID cannot start observation");

        Emit(window, "test-mouse", "mouse:button1", "down", "1");
        var first = inputs[0]!;
        Assert(inputs.Count == 1 && IsPressed(first), "first down creates and highlights one indicator");
        Emit(window, "test-mouse", "mouse:button1", "down", "1");
        Emit(window, "test-mouse", "mouse:button1", "up", "0");
        Assert(inputs.Count == 1 && ReferenceEquals(inputs[0], first) && !IsPressed(first) && Get<string>(first, "Phase") == "up", "repeat down and up update the same indicator");
        Emit(window, "test-mouse", "mouse:button4", "down", "1");
        Assert(inputs.Count == 2 && Get<string>(inputs[1]!, "Caption").Contains("Копировать"), "different control creates an indicator using its saved name");
        Emit(window, "test-keyboard", "mouse:button1", "down", "1");
        Assert(inputs.Count == 3, "same control on a different source remains separate");
        Emit(window, "test-other", "mouse:button1", "down", "1");
        Emit(window, "test-hid", "key:FF", "down", "1");
        Assert(inputs.Count == 3, "stopped and unsupported interfaces do not discover inputs");
        Emit(window, "test-mouse", "mouse:wheel", "change", "120");
        var wheel = inputs[3]!;
        Assert(Get<string>(wheel, "Value") == "120" && ((SolidColorBrush)Get<Brush>(wheel, "Background")).Color == Colors.DarkSlateBlue, "wheel shows value and pulse");
        wheel.GetType().GetField("_pulseUntil", Private)!.SetValue(wheel, DateTime.UtcNow.AddSeconds(-1));
        wheel.GetType().GetMethod("Tick")!.Invoke(wheel, null);
        Assert(((SolidColorBrush)Get<Brush>(wheel, "Background")).Color != Colors.DarkSlateBlue, "wheel pulse expires without another input");

        Toggle(window, mouse);
        Assert(!Get<bool>(mouse, "Observing") && inputs.Cast<object>().Where(i => Get<string>(i, "Source") == "test-mouse").All(i => !IsPressed(i)), "row stop clears highlights of that source");
        Emit(window, "test-mouse", "key:55", "down", "1");
        Assert(inputs.Count == 4 && IsPressed(inputs[2]!), "stopped source is ignored while another source remains active");
        Call(window, "SetDiagnosticsConnected", false);
        Assert(devices.Cast<object>().All(d => !Get<bool>(d, "Observing")) && inputs.Cast<object>().All(i => !IsPressed(i)), "disconnect clears all observation and held highlights");
        Call(window, "SetDiagnosticsConnected", true);
        Assert(devices.Cast<object>().All(d => !Get<bool>(d, "Observing")), "reconnect does not resume stale observation automatically");
        Assert(JsonSerializer.Serialize(configuration) == before && network.Calls == 0, "discovery changes no configuration and makes no external requests");

        Toggle(window, mouse);
        Toggle(window, keyboard);
        Emit(window, "test-mouse", "mouse:button4", "down", "1");
        Emit(window, "test-mouse", "mouse:button2", "up", "0");
        Emit(window, "test-keyboard", "key:E0:5B", "down", "1");
        Call(window, "RefreshConfiguration");
        var content = (Grid)window.Content;
        content.Children.OfType<TabControl>().Single().SelectedIndex = 1;
        window.Content = null;
        var surface = new Border { Background = window.Background, Child = content };
        TextElement.SetForeground(surface, window.Foreground);
        TextElement.SetFontFamily(surface, window.FontFamily);
        TextElement.SetFontSize(surface, window.FontSize);
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "local");
        Directory.CreateDirectory(output);
        Render(surface, 1280, 820, Path.Combine(output, "editor-checks.png"));
        Assert(DiscoveryViewport(surface, inputs).ViewportHeight >= 29, "discovered buttons remain visible at 1280x820");
        Render(surface, 900, 600, Path.Combine(output, "editor-checks-narrow.png"));
        Assert(DiscoveryViewport(surface, inputs).ViewportHeight >= 29, "discovered buttons remain visible at 900x600");
        var clear = Descendants<Button>(surface).Single(b => b.Content as string == "Очистить обнаруженные");
        clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, clear));
        Assert(inputs.Count == 0, "clear removes temporary discovered indicators");
        Emit(window, "test-mouse", "mouse:button1", "up", "0");
        Assert(inputs.Count == 1 && !IsPressed(inputs[0]!) && JsonSerializer.Serialize(configuration) == before, "next signal rediscovers after clear without saving state");
        window.DisconnectAsync().GetAwaiter().GetResult();
        app.Shutdown();
        Console.WriteLine($"EDITOR_CHECKS_OK {_checks}");
    }

    private static object Field(object obj, string name) => obj.GetType().GetField(name, Private)!.GetValue(obj)!;
    private static T Get<T>(object obj, string name) => (T)obj.GetType().GetProperty(name)!.GetValue(obj)!;
    private static void Call(object obj, string name, params object[] args) => obj.GetType().GetMethod(name, Private)!.Invoke(obj, args);
    private static object Device(string source, string name, string kind) => Activator.CreateInstance(typeof(MainWindow).Assembly.GetType("NaSeqMen.Editor.DeviceRow")!, source, name, kind, "TEST/" + source, "Нет · наблюдение")!;
    private static bool IsPressed(object input) => ((SolidColorBrush)Get<Brush>(input, "Background")).Color == Colors.DarkGreen;
    private static void Toggle(MainWindow window, object device)
    {
        var grid = (DataGrid)Field(window, "_deviceGrid");
        var template = ((DataGridTemplateColumn)grid.Columns[0]).CellTemplate;
        var presenter = new ContentPresenter { Content = device, ContentTemplate = template };
        presenter.Measure(new Size(126, 25));
        presenter.Arrange(new Rect(0, 0, 126, 25));
        presenter.UpdateLayout();
        var button = Descendants<Button>(presenter).Single();
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }
    private static void Emit(MainWindow window, string source, string control, string phase, string value)
    {
        var signal = new Signal { Module = "windows.input", Source = source, Control = control, Phase = phase, Value = value, ValueType = "number" };
        Call(window, "OnDiagnostic", new Diagnostic(DateTimeOffset.UtcNow, signal.Id, "", "input", "Test input", signal));
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static ScrollViewer DiscoveryViewport(DependencyObject surface, IList inputs)
        => Descendants<ScrollViewer>(surface).Single(view => view.Content is ItemsControl items && ReferenceEquals(items.ItemsSource, inputs));
    private static void Render(FrameworkElement surface, int width, int height, string path)
    {
        surface.Width = width; surface.Height = height;
        surface.Measure(new Size(width, height));
        surface.Arrange(new Rect(0, 0, width, height));
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
        Console.WriteLine("RENDER " + path);
    }
    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        _checks++;
        Console.WriteLine("PASS " + name);
    }
    private sealed class ForbiddenNetwork : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Editor checks must not connect to an executor.");
        }
    }
}
