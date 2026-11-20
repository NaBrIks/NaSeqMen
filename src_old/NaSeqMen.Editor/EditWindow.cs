// SPDX-License-Identifier: MIT
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NaSeqMen.Editor;

public sealed record EditField(string Key, string Label, string Value, bool Multiline = false, string[]? Choices = null, bool Secret = false, bool ReadOnly = false);

public sealed class EditWindow : Window
{
    private readonly Dictionary<string, FrameworkElement> _fields = new();
    private readonly TextBlock _status;
    private readonly Button _save;

    public EditWindow(string title, IEnumerable<EditField> fields, Func<Dictionary<string, string>, Task<string?>> save, string saveLabel = "Сохранить")
    {
        Background = (Brush)FindResource("WindowBrush");
        Foreground = (Brush)FindResource("TextBrush");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 13;
        Title = title;
        Width = 640;
        Height = 480;
        MinWidth = 440;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(8) };
        var bottom = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        _save = new Button { Content = saveLabel, IsDefault = true };
        var cancel = new Button { Content = "Отмена", IsCancel = true };
        HelpText.Attach(_save, HelpText.Button(saveLabel));
        HelpText.Attach(cancel, HelpText.Button("Отмена"));
        DockPanel.SetDock(cancel, Dock.Right);
        DockPanel.SetDock(_save, Dock.Right);
        bottom.Children.Add(cancel);
        bottom.Children.Add(_save);
        _status = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, MinWidth = 30 };
        bottom.Children.Add(_status);
        root.Children.Add(bottom);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var index = 0;
        foreach (var field in fields)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = field.Label, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 6, 0) };
            HelpText.Attach(label, HelpText.Field(field.Key, field.Label));
            Grid.SetRow(label, index);
            grid.Children.Add(label);
            FrameworkElement input;
            if (field.Secret)
                input = new PasswordBox { Password = field.Value, Background = (Brush)FindResource("PanelBrush"), Foreground = Foreground, Padding = new Thickness(3, 2, 3, 2) };
            else if (field.Choices is not null)
                input = BuildChoices(field);
            else
                input = new TextBox { Text = field.Value, IsReadOnly = field.ReadOnly, AcceptsReturn = field.Multiline, TextWrapping = field.Multiline ? TextWrapping.NoWrap : TextWrapping.Wrap,
                    MinHeight = field.Multiline ? 190 : 26, VerticalScrollBarVisibility = field.Multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
                    HorizontalScrollBarVisibility = field.Multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
                    FontFamily = field.Multiline ? new System.Windows.Media.FontFamily("Consolas") : FontFamily };
            input.Margin = new Thickness(0, 2, 0, 2);
            HelpText.Attach(input, HelpText.Field(field.Key, field.Label));
            _fields[field.Key] = input;
            Grid.SetRow(input, index++);
            Grid.SetColumn(input, 1);
            grid.Children.Add(input);
        }
        root.Children.Add(new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        _save.Click += async (_, _) =>
        {
            _save.IsEnabled = false;
            try
            {
                var values = _fields.ToDictionary(pair => pair.Key, pair => pair.Value switch
                {
                    TextBox text => text.Text,
                    PasswordBox password => password.Password,
                    WrapPanel choices => choices.Children.OfType<RadioButton>().FirstOrDefault(radio => radio.IsChecked == true)?.Tag as string ?? "",
                    _ => ""
                });
                var error = await save(values);
                if (error is null) { DialogResult = true; Close(); }
                else { _status.Text = error; _status.ToolTip = error; }
            }
            catch (Exception ex) { _status.Text = ex.Message; _status.ToolTip = ex.ToString(); }
            finally { _save.IsEnabled = true; }
        };
    }

    private static WrapPanel BuildChoices(EditField field)
    {
        var panel = new WrapPanel();
        var group = Guid.NewGuid().ToString("N");
        foreach (var choice in field.Choices!.Append(field.Value).Distinct())
        {
            panel.Children.Add(new RadioButton
            {
                Content = choice.Length == 0 ? "—" : choice,
                Tag = choice, GroupName = group, IsChecked = choice == field.Value,
                Margin = new Thickness(0, 3, 8, 3)
            });
        }
        return panel;
    }
}
