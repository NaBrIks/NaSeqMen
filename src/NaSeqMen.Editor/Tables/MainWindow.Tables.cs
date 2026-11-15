// SPDX-License-Identifier: MIT
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using NaSeqMen.Contracts;
using ValidationResult = NaSeqMen.Contracts.ValidationResult;

namespace NaSeqMen.Editor;

public sealed partial class MainWindow
{
    private static DataGrid GridFor<T>(ObservableCollection<T> items, (string Label, string Property, double Width)[] columns)
    {
        var grid = new DataGrid { ItemsSource = items, ColumnHeaderHeight = 49 };
        var filters = new Dictionary<string, string>();
        var view = CollectionViewSource.GetDefaultView(items);
        view.Filter = item => filters.All(f => string.IsNullOrWhiteSpace(f.Value) || (item.GetType().GetProperty(f.Key)?.GetValue(item)?.ToString() ?? "").Contains(f.Value, StringComparison.CurrentCultureIgnoreCase));
        foreach (var column in columns)
        {
            var explanation = HelpText.Column(column.Label);
            var header = HelpText.Attach(new StackPanel(), explanation + "\nНажатие на заголовок сортирует строки по этой колонке.");
            header.Children.Add(new TextBlock { Text = column.Label });
            var filter = HelpText.Attach(new TextBox { Height = 21, MinWidth = 45, FontSize = 11 }, "Фильтр колонки «" + column.Label + "»: введите часть текста, чтобы оставить совпадающие строки. Очистите поле, чтобы снова показать все. Фильтр меняет только просмотр.\n\n" + explanation);
            filter.TextChanged += (_, _) => { filters[column.Property] = filter.Text; view.Refresh(); };
            header.Children.Add(filter);
            var cell = new Style(typeof(TextBlock));
            cell.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            cell.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(column.Property) { Converter = new CellHelpConverter(explanation) }));
            cell.Setters.Add(new Setter(ToolTipService.InitialShowDelayProperty, 350));
            cell.Setters.Add(new Setter(ToolTipService.ShowDurationProperty, 120000));
            grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(column.Property), SortMemberPath = column.Property, Width = column.Width, ElementStyle = cell });
        }
        return grid;
    }

    private void SetDiagnosticsConnected(bool connected)
    {
        _diagnosticsConnected = connected;
        foreach (var device in _devices) device.SetObservation(connected, device.Observing);
        if (!connected) foreach (var input in _inputs) input.StopHighlight();
    }

    private void AddObservationColumn(DataGrid grid)
    {
        var button = new FrameworkElementFactory(typeof(Button));
        button.SetResourceReference(FrameworkElement.StyleProperty, "ObservationButton");
        button.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(DeviceRow.CanObserve)));
        button.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(DeviceRow.ObservationHelp)));
        button.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty, new Binding(nameof(DeviceRow.ObservationAction)));
        button.SetValue(ToolTipService.ShowOnDisabledProperty, true);
        button.SetValue(ToolTipService.ShowDurationProperty, 120000);
        button.SetValue(ToolTipService.InitialShowDelayProperty, 350);
        var content = new FrameworkElementFactory(typeof(StackPanel));
        content.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var icon = new FrameworkElementFactory(typeof(Image));
        icon.SetValue(FrameworkElement.WidthProperty, 17d);
        icon.SetValue(FrameworkElement.HeightProperty, 17d);
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 5, 0));
        icon.SetBinding(Image.SourceProperty, new Binding(nameof(DeviceRow.ObservationIcon)));
        content.AppendChild(icon);
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(DeviceRow.Observation)));
        content.AppendChild(label);
        button.AppendChild(content);
        button.AddHandler(System.Windows.Controls.Button.ClickEvent, new RoutedEventHandler((sender, e) =>
        {
            if (sender is Button { DataContext: DeviceRow row } && row.CanObserve)
            {
                row.SetObservation(_diagnosticsConnected, !row.Observing);
                if (!row.Observing) foreach (var input in _inputs.Where(i => i.Source == row.Id)) input.StopHighlight();
                Status((row.Observing ? "Обнаружение включено: " : "Обнаружение остановлено: ") + row.Name + ". Исходный ввод Windows не блокируется.");
            }
            e.Handled = true;
        }));
        grid.Columns.Insert(0, new DataGridTemplateColumn
        {
            Header = HelpText.Attach(new TextBlock { Text = "Обнаружение" }, "Включение и остановка изучения кнопок прямо в строке интерфейса. «Идёт» означает, что редактор принимает его новые сигналы; это не подавление ввода Windows."),
            Width = 126, SortMemberPath = nameof(DeviceRow.Observation), CellTemplate = new DataTemplate { VisualTree = button }
        });
    }

    private static void AddEditColumn(DataGrid grid, Action<object> edit)
    {
        var factory = new FrameworkElementFactory(typeof(Button));
        factory.SetValue(ContentControl.ContentProperty, "✎"); factory.SetValue(Control.BorderThicknessProperty, new Thickness(0)); factory.SetValue(Control.BackgroundProperty, Brushes.Transparent);
        factory.SetValue(FrameworkElement.ToolTipProperty, HelpText.Button("✎"));
        factory.SetValue(ToolTipService.ShowDurationProperty, 120000);
        factory.AddHandler(System.Windows.Controls.Button.ClickEvent, new RoutedEventHandler((sender, _) => { if (sender is Button { DataContext: { } item }) edit(item); }));
        grid.Columns.Insert(0, new DataGridTemplateColumn { Header = "", Width = 32, CellTemplate = new DataTemplate { VisualTree = factory } });
    }

}
