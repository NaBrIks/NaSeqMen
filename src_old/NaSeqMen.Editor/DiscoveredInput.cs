// SPDX-License-Identifier: MIT
using System.ComponentModel;
using System.Windows.Media;

namespace NaSeqMen.Editor;

// Session-only presentation of observed input. Never persisted or used by the executor.
internal sealed class DiscoveredInput(string source, string control, string deviceLabel) : INotifyPropertyChanged
{
    public string Source { get; } = source;
    public string Control { get; } = control;
    public string DeviceLabel { get; } = deviceLabel;
    public string Time { get; private set; } = "";
    public string Phase { get; private set; } = "";
    public string Value { get; private set; } = "";
    public string Name { get; set; } = "";
    private bool _pressed;
    private DateTime _pulseUntil;
    public string Caption => DeviceLabel + " · " + (string.IsNullOrWhiteSpace(Name) ? Control switch
    {
        "mouse:button1" => "Левая", "mouse:button2" => "Правая", "mouse:button3" => "Средняя",
        "mouse:button4" => "Боковая 1", "mouse:button5" => "Боковая 2",
        "mouse:wheel" => "Колесо", "mouse:horizontalWheel" => "Колесо ↔",
        _ when Control.StartsWith("key:") => "Клавиша " + Control[4..], _ => Control
    } : Name);
    public Brush Background => _pressed ? Brushes.DarkGreen : _pulseUntil > DateTime.UtcNow ? Brushes.DarkSlateBlue : new SolidColorBrush(Color.FromRgb(48, 67, 83));
    public string Help => Caption + "\nПоследний сигнал: " + (Phase switch { "down" => "Нажата", "up" => "Отпущена", "change" => "Изменение", _ => Phase })
        + " · значение: " + Value + " · " + Time + "\nКод: " + Control + "\nИнтерфейс: " + Source
        + "\nНажмите, чтобы открыть подробности и сохранить понятное имя для схемы. Это настройка, а не отправка нажатия.";
    public void Update(string time, string phase, string value)
    {
        Time = time; Phase = phase; Value = value;
        _pressed = phase == "down";
        _pulseUntil = phase == "change" ? DateTime.UtcNow.AddMilliseconds(350) : default;
        Notify();
    }
    public void StopHighlight() { _pressed = false; _pulseUntil = default; Notify(); }
    public void Tick()
    {
        if (_pulseUntil != default && _pulseUntil <= DateTime.UtcNow) { _pulseUntil = default; Notify(); }
    }
    public void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public event PropertyChangedEventHandler? PropertyChanged;
}
