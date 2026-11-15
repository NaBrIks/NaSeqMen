// SPDX-License-Identifier: MIT
using System.ComponentModel;
using System.Windows.Media;

namespace NaSeqMen.Editor;

internal sealed class DeviceRow(string id, string name, string kind, string hardwareId, string suppression) : INotifyPropertyChanged
{
    private static readonly ImageSource StartIcon = TablerIcons.Load("player-play", Brushes.LightSteelBlue);
    private static readonly ImageSource StopIcon = TablerIcons.Load("player-stop", Brushes.LightGreen);
    private static readonly ImageSource UnsupportedIcon = TablerIcons.Load("ban", Brushes.Gray);
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Kind { get; } = kind;
    public string HardwareId { get; } = hardwareId;
    public string Suppression { get; } = suppression;
    public bool Supported => Kind is "mouse" or "keyboard";
    public bool CanObserve => Supported && _connected;
    private bool _connected, _observing;
    public bool Observing => _observing;
    public string Observation => !Supported ? "Недоступно" : !_connected ? "Нет связи" : Observing ? "Идёт" : "Выключено";
    public ImageSource ObservationIcon => !Supported ? UnsupportedIcon : Observing ? StopIcon : StartIcon;
    public string ObservationHelp => !Supported
        ? "Служебный HID-интерфейс обнаружен, но чтение его событий пока не реализовано. Используйте мышиный или клавиатурный интерфейс этого устройства."
        : !_connected ? "Нет подключения к живой диагностике исполнителя. Нажмите «Подключить» вверху окна."
        : Observing ? "Обнаружение кнопок этого интерфейса включено. Нажмите, чтобы остановить его показ в области изучения. Рабочие правила и ввод Windows продолжат работать."
        : "Нажмите, чтобы обнаруживать кнопки этого интерфейса. Новые сигналы появятся в области изучения. Можно включить несколько интерфейсов одновременно. Исходный ввод Windows не блокируется.";
    public string ObservationAction => (Observing ? "Остановить обнаружение: " : "Начать обнаружение: ") + Name;
    public void SetObservation(bool connected, bool observing)
    {
        _connected = connected;
        _observing = connected && Supported && observing;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
