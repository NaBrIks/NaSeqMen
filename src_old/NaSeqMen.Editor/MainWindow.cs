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

public sealed partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly CancellationTokenSource _stop = new();
    private ClientWebSocket? _socket;
    private Configuration _configuration = new();
    private ModuleDescriptor[] _modules = [];
    private readonly ObservableCollection<DeviceRow> _devices = [];
    private readonly ObservableCollection<ConnectionRow> _connections = [];
    private readonly ObservableCollection<MappingRow> _mappings = [];
    private readonly ObservableCollection<TraceRow> _traces = [];
    private readonly ObservableCollection<DiscoveredInput> _inputs = [];
    private readonly Dictionary<(string Source, string Control), DiscoveredInput> _discovered = [];
    private readonly System.Windows.Threading.DispatcherTimer _inputPulse = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly TextBlock _status = new() { Text = "Не подключено", TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _selection = new() { Text = "Выберите блок или соедините контакты", TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBox _url = new() { Width = 210 };
    private readonly CheckBox _dryRun = new() { Content = "Пробный режим", IsChecked = true };
    private readonly GraphView _graph = new();
    private DataGrid _deviceGrid = null!, _mappingGrid = null!, _connectionGrid = null!;
    private readonly string _root;
    private readonly bool _smoke;
    private string _token = "";
    private string? _selectedNode;
    private bool _diagnosticsConnected;
    private bool _refreshing;

    public MainWindow()
    {
        Background = (Brush)FindResource("WindowBrush");
        Foreground = (Brush)FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        Title = "NaSeqMen — редактор · альфа";
        Width = 1280; Height = 820; MinWidth = 850; MinHeight = 550;
        _root = Argument("--root") ?? FindRoot();
        _url.Text = Argument("--url") ?? "http://127.0.0.1:17963";
        HelpText.Attach(_url, "Адрес отдельного исполнителя NaSeqMen. Он принимает ввод и выполняет схему независимо от этого окна. Для локального запуска оставьте http://127.0.0.1:17963. Это не адрес OBS.");
        HelpText.Attach(_dryRun, HelpText.DryRun);
        HelpText.Attach(_selection, "Название выбранного блока. Дважды нажмите на блок, чтобы открыть его параметры. Сигналы проходят по проводам слева направо.");
        HelpText.Attach(_status, "Результат последней операции редактора. Наведите сюда, чтобы прочитать полный текст, если он не помещается в строке.");
        _smoke = Environment.GetCommandLineArgs().Contains("--smoke");
        Content = BuildUi();
        _inputPulse.Tick += (_, _) => { foreach (var input in _inputs) input.Tick(); };
        _inputPulse.Start();
        _graph.NodeSelected += node => { _selectedNode = node.Id; _selection.Text = node.Title + " · " + node.Detail; };
        _graph.NodeEditRequested += node => EditNode(node.Id);
        _graph.LinkRequested += async (from, to) =>
        {
            var candidate = Clone();
            var source = candidate.Nodes.First(n => n.Id == from);
            var target = candidate.Nodes.First(n => n.Id == to);
            if (!PortTypes.Compatible(source, target)) { Status("Типы контактов несовместимы: " + PortTypes.Output(source) + " → " + PortTypes.Input(target)); return; }
            if (candidate.Edges.Any(edge => edge.From == from && edge.To == to)) { Status("Эта связь уже есть"); return; }
            candidate.Edges.Add(new EdgeDefinition { From = from, To = to });
            await ApplyAsync(candidate);
        };
        _dryRun.Click += async (_, _) => { if (!_refreshing) { var candidate = Clone(); candidate.DryRun = _dryRun.IsChecked == true; await ApplyAsync(candidate); } };
        Closed += async (_, _) => await DisconnectAsync();
    }

    public async Task InitializeAsync()
    {
        if (_smoke && !Environment.GetCommandLineArgs().Contains("--live"))
        {
            var example = Path.Combine(_root, "examples", "default.json");
            _configuration = File.Exists(example) ? JsonSerializer.Deserialize<Configuration>(await File.ReadAllTextAsync(example), Json) ?? DemoConfiguration() : DemoConfiguration();
            _configuration.DryRun = true;
            _modules = [new("windows.desktop", "Windows", "alpha", false, false, [new("chord", "Сочетание клавиш", ["keys"]), new("key", "Нажать клавишу", ["key", "phase"])], [])];
            RefreshConfiguration();
            Status("Визуальная проверка · демонстрационная схема · внешние действия не выполняются");
            _traces.Add(new TraceRow("12:34:56.120", "Боковая кнопка", "Вход", "Нажата", "mouse.button4 / down", "demo"));
            _traces.Add(new TraceRow("12:34:56.121", "Только нажатие", "Условие", "Пропущено", "phase = down", "demo"));
            _traces.Add(new TraceRow("12:34:56.122", "Копировать", "Пробный выход", "Не отправлено", "Ctrl+C", "demo"));
            return;
        }
        await ConnectAsync();
    }

    private UIElement BuildUi()
    {
        var root = new Grid { Margin = new Thickness(6) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(120) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(25) });
        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(new TextBlock { Text = "Исполнитель ", Margin = new Thickness(0, 0, 5, 0) });
        left.Children.Add(_url);
        left.Children.Add(Button("Подключить", async () => await ConnectAsync()));
        left.Children.Add(Button("Обновить", async () => await ReloadAsync()));
        left.Children.Add(_dryRun);
        left.Children.Add(Button("Проверить схему", async () => await ValidateAsync()));
        toolbar.Children.Add(left);
        root.Children.Add(toolbar);
        var tabs = new TabControl();
        tabs.Items.Add(HelpText.Attach(new TabItem { Header = "Схема", Content = GraphTab() }, HelpText.Tab("Схема")));
        tabs.Items.Add(HelpText.Attach(new TabItem { Header = "Устройства и кнопки", Content = DevicesTab() }, HelpText.Tab("Устройства и кнопки")));
        tabs.Items.Add(HelpText.Attach(new TabItem { Header = "Соединения", Content = ConnectionsTab() }, HelpText.Tab("Соединения")));
        Grid.SetRow(tabs, 1); root.Children.Add(tabs);
        var splitter = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = new SolidColorBrush(Color.FromRgb(55, 73, 86)) };
        HelpText.Attach(splitter, "Потяните вверх или вниз, чтобы изменить высоту области диагностики. Это меняет только компоновку окна.");
        Grid.SetRow(splitter, 2); root.Children.Add(splitter);
        var diagnostic = new DockPanel();
        var traceBar = new DockPanel { Margin = new Thickness(0, 2, 0, 3) };
        DockPanel.SetDock(traceBar, Dock.Top);
        var clear = Button("Очистить просмотр", () => _traces.Clear()); DockPanel.SetDock(clear, Dock.Right); traceBar.Children.Add(clear);
        traceBar.Children.Add(HelpText.Attach(new TextBlock { Text = "Живое прохождение · только события текущего подключения", FontWeight = FontWeights.SemiBold }, HelpText.Trace));
        diagnostic.Children.Add(traceBar);
        var traceGrid = GridFor(_traces, [("Время", nameof(TraceRow.Time), 105), ("Узел", nameof(TraceRow.Node), 190), ("Этап", nameof(TraceRow.Stage), 115), ("Результат", nameof(TraceRow.Message), 220), ("Данные", nameof(TraceRow.Data), 300)]);
        traceGrid.MouseDoubleClick += (_, _) => { if (traceGrid.SelectedItem is TraceRow row) ShowDetails("Диагностика", row.Message + "\n" + row.Data); };
        diagnostic.Children.Add(traceGrid); Grid.SetRow(diagnostic, 3); root.Children.Add(diagnostic);
        Grid.SetRow(_status, 4); root.Children.Add(_status);
        return root;
    }

    private UIElement GraphTab()
    {
        var panel = new DockPanel { Margin = new Thickness(4) };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(bar, Dock.Top);
        bar.Children.Add(Button("+ Узел", () => EditNode(null)));
        bar.Children.Add(Button("Изменить", () => { if (_selectedNode is not null) EditNode(_selectedNode); }));
        bar.Children.Add(Button("Удалить узел", async () => await DeleteNodeAsync()));
        bar.Children.Add(Button("Связи…", ShowLinks));
        bar.Children.Add(Button("Подать событие…", Inject));
        bar.Children.Add(new TextBlock { Text = "Масштаб", Margin = new Thickness(8, 0, 4, 0) });
        var zoom = new Slider { Minimum = 0.8, Maximum = 1.8, Value = 1, Width = 90, VerticalAlignment = VerticalAlignment.Center };
        HelpText.Attach(zoom, "Уменьшить или увеличить изображение схемы. Логика, параметры и размер самого окна не меняются. Если схема не помещается, используйте прокрутку.");
        zoom.ValueChanged += (_, _) => { _graph.Zoom = zoom.Value; _graph.InvalidateVisual(); };
        bar.Children.Add(zoom); panel.Children.Add(bar);
        var bottom = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        bottom.Children.Add(_selection);
        bottom.Children.Add(HelpText.Attach(new TextBlock { Text = "Пробный режим подавляет действия API; прямые внешние вызовы C# не перехватываются.", FontSize = 11, Foreground = Brushes.LightSteelBlue }, HelpText.DryRun));
        panel.Children.Add(bottom);
        panel.Children.Add(new ScrollViewer { Content = _graph, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return panel;
    }

    private UIElement DevicesTab()
    {
        var panel = new Grid { Margin = new Thickness(4) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 90, MaxHeight = 230 });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 80 });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(96) });
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        bar.Children.Add(Button("Обновить устройства", async () => await LoadDevicesAsync()));
        panel.Children.Add(bar);
        _deviceGrid = GridFor(_devices, [("Устройство / интерфейс", nameof(DeviceRow.Name), 330), ("Тип", nameof(DeviceRow.Kind), 100), ("Подавление", nameof(DeviceRow.Suppression), 110), ("Аппаратный путь", nameof(DeviceRow.HardwareId), 500)]);
        AddObservationColumn(_deviceGrid);
        Grid.SetRow(_deviceGrid, 1); panel.Children.Add(_deviceGrid);
        var splitter = new GridSplitter { HorizontalAlignment = HorizontalAlignment.Stretch, Height = 5, Background = new SolidColorBrush(Color.FromRgb(55, 73, 86)) };
        HelpText.Attach(splitter, "Потяните, чтобы разделить доступное место между списком интерфейсов и изучением кнопок.");
        Grid.SetRow(splitter, 2); panel.Children.Add(splitter);
        var inputs = DiscoveredInputsPanel();
        Grid.SetRow(inputs, 3); panel.Children.Add(inputs);
        _mappingGrid = GridFor(_mappings, [("Имя кнопки", nameof(MappingRow.Name), 200), ("Исходный код", nameof(MappingRow.Control), 190), ("Политика", nameof(MappingRow.Policy), 120), ("Интерфейс", nameof(MappingRow.Source), 450)]);
        AddEditColumn(_mappingGrid, row => { if (row is MappingRow mapping) EditMapping(mapping.Id, null); });
        Grid.SetRow(_mappingGrid, 4); panel.Children.Add(_mappingGrid);
        return panel;
    }

    private UIElement DiscoveredInputsPanel()
    {
        var panel = new DockPanel();
        var bar = new DockPanel { Margin = new Thickness(0, 2, 0, 3) };
        DockPanel.SetDock(bar, Dock.Top);
        var clear = HelpText.Attach(new Button { Content = "Очистить обнаруженные" }, "Убрать временные индикаторы кнопок из этого окна. Сохранённые имена и схема останутся. Следующий сигнал снова создаст индикатор.");
        clear.Click += (_, _) => { _inputs.Clear(); _discovered.Clear(); };
        DockPanel.SetDock(clear, Dock.Right); bar.Children.Add(clear);
        bar.Children.Add(HelpText.Attach(new TextBlock { Text = "Обнаруженные кнопки · нажмите физическую кнопку, затем её индикатор для настройки", TextTrimming = TextTrimming.CharacterEllipsis }, "Одна кнопка на интерфейс и код. Нажатие подсвечивается зелёным, отпускание снимает подсветку; колесо кратко вспыхивает. Это временный просмотр в редакторе, а не хранилище состояний исполнителя."));
        panel.Children.Add(bar);
        var button = new FrameworkElementFactory(typeof(Button));
        button.SetValue(FrameworkElement.WidthProperty, 186d);
        button.SetValue(FrameworkElement.HeightProperty, 29d);
        button.SetValue(FrameworkElement.MarginProperty, new Thickness(1));
        button.SetValue(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left);
        button.SetBinding(Control.BackgroundProperty, new Binding(nameof(DiscoveredInput.Background)));
        button.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(DiscoveredInput.Help)));
        button.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty, new Binding(nameof(DiscoveredInput.Caption)));
        button.SetValue(ToolTipService.ShowDurationProperty, 120000);
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(DiscoveredInput.Caption)));
        label.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        button.AppendChild(label);
        button.AddHandler(System.Windows.Controls.Button.ClickEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is Button { DataContext: DiscoveredInput input })
            {
                var mapping = _configuration.Mappings.FirstOrDefault(m => m.Source == input.Source && m.Control == input.Control);
                EditMapping(mapping?.Id, new(input.Time, input.Source, input.Control, input.Phase, input.Value));
                input.Name = _configuration.Mappings.FirstOrDefault(m => m.Source == input.Source && m.Control == input.Control)?.Name ?? "";
                input.Notify();
            }
        }));
        var items = new ItemsControl
        {
            ItemsSource = _inputs,
            ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel))),
            ItemTemplate = new DataTemplate { VisualTree = button }
        };
        panel.Children.Add(new ScrollViewer { Content = items, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        return panel;
    }

    private UIElement ConnectionsTab()
    {
        var panel = new DockPanel { Margin = new Thickness(4) };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(bar, Dock.Top);
        bar.Children.Add(Button("+ Соединение", () => EditConnection(null)));
        bar.Children.Add(Button("Удалить", async () =>
        {
            if (_connectionGrid.SelectedItem is not ConnectionRow row) return;
            var candidate = Clone(); candidate.Connections.RemoveAll(item => item.Id == row.Id); await ApplyAsync(candidate);
        }));
        bar.Children.Add(new TextBlock { Text = "Один экземпляр соединения используется входами и выходами.", Margin = new Thickness(6, 0, 0, 0) });
        panel.Children.Add(bar);
        _connectionGrid = GridFor(_connections, [("Название", nameof(ConnectionRow.Name), 260), ("Модуль", nameof(ConnectionRow.Module), 220), ("Включено", nameof(ConnectionRow.Enabled), 100), ("Параметры", nameof(ConnectionRow.Parameters), 450)]);
        AddEditColumn(_connectionGrid, row => { if (row is ConnectionRow connection) EditConnection(connection.Id); });
        panel.Children.Add(_connectionGrid); return panel;
    }

    private async Task ConnectAsync()
    {
        try
        {
            SetDiagnosticsConnected(false);
            _socket?.Abort(); _socket?.Dispose();
            _token = await File.ReadAllTextAsync(Path.Combine(_root, "local", "api-token.txt"));
            _http.Dispose();
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            _http.BaseAddress = new Uri(_url.Text.Trim().TrimEnd('/') + "/");
            _http.DefaultRequestHeaders.Remove("X-NaSeqMen-Token");
            _http.DefaultRequestHeaders.Add("X-NaSeqMen-Token", _token.Trim());
            await ReloadAsync();
            _traces.Clear(); _inputs.Clear(); _discovered.Clear();
            _socket = new ClientWebSocket();
            _socket.Options.SetRequestHeader("X-NaSeqMen-Token", _token.Trim());
            var builder = new UriBuilder(_http.BaseAddress) { Scheme = _http.BaseAddress.Scheme == "https" ? "wss" : "ws", Path = "api/diagnostics" };
            await _socket.ConnectAsync(builder.Uri, _stop.Token);
            SetDiagnosticsConnected(true);
            _ = ReceiveAsync(_socket);
            Status("Подключено · события не сохраняются · Raw Input наблюдает, но не блокирует ввод");
        }
        catch (Exception ex) { Status("Нет подключения: " + ex.Message); }
    }

    private async Task ReloadAsync()
    {
        try
        {
            _configuration = await _http.GetFromJsonAsync<Configuration>("api/config", Json) ?? new();
            _modules = await _http.GetFromJsonAsync<ModuleDescriptor[]>("api/modules", Json) ?? [];
            RefreshConfiguration(); await LoadDevicesAsync(); Status("Конфигурация получена");
        }
        catch (Exception ex) { Status("Не удалось получить конфигурацию: " + ex.Message); }
    }

    private async Task LoadDevicesAsync()
    {
        try
        {
            var devices = await _http.GetFromJsonAsync<DeviceDescriptor[]>("api/devices", Json) ?? [];
            var observing = _devices.Where(d => d.Observing).Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
            _devices.Clear();
            foreach (var device in devices.OrderBy(d => d.Kind is "mouse" or "keyboard" ? 0 : 1))
            {
                var row = new DeviceRow(device.Id, device.Name, device.Kind, device.HardwareId, device.CanSuppress ? "Доступно" : "Нет · наблюдение");
                row.SetObservation(_diagnosticsConnected, observing.Contains(device.Id));
                _devices.Add(row);
            }
            foreach (var input in _inputs.Where(input => !_devices.Any(d => d.Observing && d.Id == input.Source))) input.StopHighlight();
        }
        catch (Exception ex) { Status("Устройства: " + ex.Message); }
    }

    private async Task ReceiveAsync(ClientWebSocket socket)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!_stop.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, _stop.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > 1024 * 1024) throw new InvalidDataException("Диагностическое сообщение слишком велико");
                } while (!result.EndOfMessage);
                var diagnostic = JsonSerializer.Deserialize<Diagnostic>(message.ToArray(), Json);
                if (diagnostic is not null) await Dispatcher.InvokeAsync(() => OnDiagnostic(diagnostic));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_stop.IsCancellationRequested) await Dispatcher.InvokeAsync(() => Status("Диагностика отключена: " + ex.Message)); }
        finally
        {
            if (!_stop.IsCancellationRequested) await Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(socket, _socket)) SetDiagnosticsConnected(false);
            });
        }
    }

    private void OnDiagnostic(Diagnostic diagnostic)
    {
        var node = _configuration.Nodes.FirstOrDefault(n => n.Id == diagnostic.NodeId);
        var signal = diagnostic.Signal;
        var data = signal is null ? "" : $"{signal.Control} / {signal.Phase} / {signal.Value}";
        _traces.Add(new(diagnostic.Time.LocalDateTime.ToString("HH:mm:ss.fff"), node?.Name ?? (diagnostic.Stage == "input" ? signal?.Module == "test" ? "Пробный ввод" : "Вход: " + (_modules.FirstOrDefault(m => m.Id == signal?.Module)?.Name ?? signal?.Module ?? "источник") : ""), Stage(diagnostic.Stage), diagnostic.Message, data, diagnostic.SignalId));
        while (_traces.Count > 400) _traces.RemoveAt(0);
        var view = _graph.Nodes.FirstOrDefault(n => n.Id == diagnostic.NodeId);
        if (view is not null)
        {
            view.FlashUntil = DateTime.UtcNow.AddMilliseconds(650);
            view.Failed = diagnostic.Stage is "error" or "blocked";
            _graph.InvalidateVisual();
        }
        if (diagnostic.Stage == "routed")
        {
            var edge = _graph.Edges.FirstOrDefault(e => e.From == diagnostic.NodeId && e.To == diagnostic.NextNodeId);
            if (edge is not null) { edge.FlashUntil = DateTime.UtcNow.AddMilliseconds(650); _graph.InvalidateVisual(); }
        }
        if (diagnostic.Stage == "input" && signal is not null && _devices.Any(d => d.Observing && d.Id == signal.Source))
        {
            var key = (signal.Source, signal.Control);
            if (!_discovered.TryGetValue(key, out var input))
            {
                if (_inputs.Count >= 512) { Status("Достигнут предел просмотра: 512 обнаруженных входов. Очистите панель для нового изучения."); return; }
                var device = _devices.First(d => d.Id == signal.Source);
                var number = _devices.Where(d => d.Kind == device.Kind).ToList().IndexOf(device) + 1;
                input = new DiscoveredInput(signal.Source, signal.Control, (device.Kind == "mouse" ? "Мышь " : "Клав. ") + number);
                _discovered.Add(key, input); _inputs.Add(input);
            }
            input.Name = _configuration.Mappings.FirstOrDefault(m => m.Source == signal.Source && m.Control == signal.Control)?.Name ?? "";
            input.Update(diagnostic.Time.LocalDateTime.ToString("HH:mm:ss.fff"), signal.Phase, signal.Value);
        }
    }

    private async Task<string?> ApplyAsync(Configuration candidate)
    {
        if (_smoke) { _configuration = candidate; RefreshConfiguration(); return null; }
        try
        {
            var response = await _http.PutAsJsonAsync("api/config", candidate, Json);
            var result = await response.Content.ReadFromJsonAsync<ValidationResult>(Json);
            if (!response.IsSuccessStatusCode || result?.Valid != true)
            {
                var error = string.Join("; ", result?.Errors ?? [response.ReasonPhrase ?? "Ошибка сохранения"]);
                Status(error); _dryRun.IsChecked = _configuration.DryRun; return error;
            }
            _configuration = candidate; RefreshConfiguration(); Status("Сохранено и применено исполнителем"); return null;
        }
        catch (Exception ex) { Status(ex.Message); _dryRun.IsChecked = _configuration.DryRun; return ex.Message; }
    }

    private async Task ValidateAsync()
    {
        try
        {
            var response = await _http.PostAsJsonAsync("api/config/validate", _configuration, Json);
            var result = await response.Content.ReadFromJsonAsync<ValidationResult>(Json);
            Status(result?.Valid == true ? "Схема прошла проверку" : string.Join("; ", result?.Errors ?? ["Нет ответа"]));
        }
        catch (Exception ex) { Status(ex.Message); }
    }

    private void RefreshConfiguration()
    {
        _refreshing = true; _dryRun.IsChecked = _configuration.DryRun; _refreshing = false;
        _graph.Nodes = _configuration.Nodes.OrderBy(n => n.Order).Select(n => new NodeView { Id = n.Id, Title = n.Name, Kind = n.Kind,
            Lane = n.Kind switch { "source" => 0, "event" => 1, "condition" => 2, "transform" or "script" => 3, _ => 4 },
            Detail = (n.Enabled ? "" : "Выключен · ") + (n.Kind == "source" ? string.Join(" / ", n.Settings.Where(p => p.Key is "control" or "phase").Select(p => p.Value)) : n.Kind == "script" ? "C# · пользовательский обработчик" : OperationLabel(n.Operation)) }).ToList();
        _graph.Edges = _configuration.Edges.Select(e => new EdgeView(e.From, e.To)).ToList(); _graph.InvalidateMeasure(); _graph.InvalidateVisual();
        _connections.Clear();
        foreach (var connection in _configuration.Connections) _connections.Add(new(connection.Id, connection.Name, connection.Module, connection.Enabled ? "Да" : "Нет", string.Join("; ", connection.Settings.Where(p => !p.Key.Contains("password", StringComparison.OrdinalIgnoreCase)).Select(p => p.Key + "=" + p.Value))));
        _mappings.Clear(); foreach (var mapping in _configuration.Mappings) _mappings.Add(new(mapping.Id, mapping.Name, mapping.Source, mapping.Control, mapping.Policy));
    }

    private static Button Button(string text, Action action) { var button = HelpText.Attach(new Button { Content = text }, HelpText.Button(text)); button.Click += (_, _) => action(); return button; }
    private static Button Button(string text, Func<Task> action) { var button = HelpText.Attach(new Button { Content = text }, HelpText.Button(text)); button.Click += async (_, _) => { try { await action(); } catch (Exception ex) { button.ToolTip = HelpText.Button(text) + "\n\nПоследняя ошибка: " + ex.Message; } }; return button; }
    private Configuration Clone() => JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(_configuration, Json), Json)!;
    private string ConnectionLabel(string id) => _configuration.Connections.FirstOrDefault(c => c.Id == id)?.Name ?? "";
    private string ConnectionId(string label) => _configuration.Connections.FirstOrDefault(c => c.Name == label)?.Id ?? "";
    private void Status(string message) { _status.Text = message; _status.ToolTip = message; }
    private static string SettingsText(IEnumerable<KeyValuePair<string, string>> settings) => string.Join(Environment.NewLine, settings.Select(p => p.Key + "=" + p.Value));
    private static Dictionary<string, string> ParseSettings(string text)
    {
        var result = new Dictionary<string, string>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator < 1) throw new FormatException("Параметр должен иметь вид key=value: " + line.Trim());
            result[line[..separator].Trim()] = line[(separator + 1)..].TrimEnd('\r');
        }
        return result;
    }
    private static string Stage(string stage) => stage switch { "input" => "Вход", "received" => "Получено", "routed" => "Переход", "passed" => "Пропущено", "blocked" => "Остановлено", "transformed" => "Преобразовано", "script" => "C#", "intended" => "Команда", "suppressed" => "Пробный выход", "completed" => "Выполнено", "error" => "Ошибка", _ => stage };
    private string OperationLabel(string operation) => operation switch { "equals" => "Сравнить поле", "scale" => "Преобразовать шкалу", "text" => "Сформировать текст", _ => _modules.SelectMany(m => m.Actions.Concat(m.Conditions)).FirstOrDefault(o => o.Id == operation)?.Name ?? operation };
    private static string? Argument(string key) { var args = Environment.GetCommandLineArgs(); var index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")) && Directory.Exists(Path.Combine(dir.FullName, "src"))) return dir.FullName;
        return Environment.CurrentDirectory;
    }
    public async Task DisconnectAsync()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel(); _inputPulse.Stop(); SetDiagnosticsConnected(false);
        if (_socket?.State == WebSocketState.Open) { try { await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "editor closed", CancellationToken.None); } catch { } }
        _socket?.Dispose(); _http.Dispose();
    }
    private static Configuration DemoConfiguration() => new()
    {
        DryRun = true,
        Nodes = [new() { Id = "input", Name = "Боковая кнопка", Kind = "source", Settings = new() { ["control"] = "demo" } },
            new() { Id = "condition", Name = "Только нажатие", Kind = "condition", Operation = "equals", Settings = new() { ["field"] = "phase", ["value"] = "down" } },
            new() { Id = "script", Name = "Обработчик C#", Kind = "script", Settings = new() { ["code"] = "return Input;" } },
            new() { Id = "action", Name = "Копировать", Kind = "action", Module = "windows.desktop", Operation = "chord", Settings = new() { ["keys"] = "CTRL+C" } }],
        Edges = [new() { From = "input", To = "condition" }, new() { From = "condition", To = "script" }, new() { From = "script", To = "action" }]
    };
    private sealed record ConnectionRow(string Id, string Name, string Module, string Enabled, string Parameters);
    private sealed record MappingRow(string Id, string Name, string Source, string Control, string Policy);
    private sealed record TraceRow(string Time, string Node, string Stage, string Message, string Data, string SignalId);
    private sealed record InputRow(string Time, string Source, string Control, string Phase, string Value);
}
