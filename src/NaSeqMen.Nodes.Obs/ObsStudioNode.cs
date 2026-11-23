using System.Collections.Concurrent;
using System.Text.Json;
using NaSeqMen.Core.Messages;
using NaSeqMen.Core.Nodes;

namespace NaSeqMen.Nodes.Obs;

public sealed partial class ObsStudioNode : NodeBase
{
    public static readonly NodeTypeDescriptor TypeDescriptor = new()
    {
        TypeId = "obs.studio",
        DisplayName = "OBS Studio",
        Category = "OBS",
        Color = "#1d4ed8",
        WidgetKind = "obs-status",
        Description = "Connects to OBS Studio via WebSocket v5 for live audio VU meters, scene/mute/volume events, and remote control.",
        Inputs =
        [
            new PortDefinition { Id = "scene", Label = "Set Scene", Direction = PortDirection.Input, DataType = PortDataType.String },
            new PortDefinition { Id = "volume", Label = "Set Volume", Direction = PortDirection.Input, DataType = PortDataType.Number },
            new PortDefinition { Id = "mute", Label = "Mute / Toggle", Direction = PortDirection.Input, DataType = PortDataType.Any },
            new PortDefinition { Id = "record", Label = "Record", Direction = PortDirection.Input, DataType = PortDataType.Any },
            new PortDefinition { Id = "stream", Label = "Stream", Direction = PortDirection.Input, DataType = PortDataType.Any },
            new PortDefinition { Id = "command", Label = "Raw Request", Direction = PortDirection.Input, DataType = PortDataType.Object },
        ],
        Outputs =
        [
            new PortDefinition { Id = "vuLevel", Label = "VU Level (0..100)", Direction = PortDirection.Output, DataType = PortDataType.Number },
            new PortDefinition { Id = "sceneChanged", Label = "Scene Changed", Direction = PortDirection.Output, DataType = PortDataType.String },
            new PortDefinition { Id = "muteChanged", Label = "Mute Changed", Direction = PortDirection.Output, DataType = PortDataType.Boolean },
            new PortDefinition { Id = "volumeChanged", Label = "Volume Changed", Direction = PortDirection.Output, DataType = PortDataType.Number },
            new PortDefinition { Id = "status", Label = "Status / Response", Direction = PortDirection.Output, DataType = PortDataType.Object },
        ],
        Parameters =
        [
            new ParameterDefinition { Key = "enabled", Label = "Connect", Kind = ParameterKind.Boolean, DefaultValue = true },
            new ParameterDefinition { Key = "host", Label = "Host", Kind = ParameterKind.String, DefaultValue = "127.0.0.1" },
            new ParameterDefinition { Key = "port", Label = "Port", Kind = ParameterKind.Number, DefaultValue = 4455 },
            new ParameterDefinition { Key = "password", Label = "Password", Kind = ParameterKind.String, DefaultValue = "", Secret = true, Placeholder = "OBS WebSocket password" },
            new ParameterDefinition { Key = "audioSource", Label = "Audio Input", Kind = ParameterKind.String, DefaultValue = "", Placeholder = "e.g. Mic/Aux (empty = all)" },
            new ParameterDefinition { Key = "sceneName", Label = "Default Scene", Kind = ParameterKind.String, DefaultValue = "", Placeholder = "Optional scene name" },
            new ParameterDefinition { Key = "subscribeMeters", Label = "Audio VU Meters", Kind = ParameterKind.Boolean, DefaultValue = true },
            new ParameterDefinition { Key = "meterThrottleMs", Label = "VU Interval (ms)", Kind = ParameterKind.Number, DefaultValue = 50 },
            new ParameterDefinition
            {
                Key = "volumeScale",
                Label = "Volume Scale",
                Kind = ParameterKind.Select,
                DefaultValue = "percent",
                Options =
                [
                    new ParameterOption("percent", "0..100%"),
                    new ParameterOption("mul", "0..1 Multiplier"),
                    new ParameterOption("db", "-60..0 dB"),
                ],
            },
        ],
    };

    private readonly ConcurrentDictionary<string, long> _lastMeterTickMs = new(StringComparer.OrdinalIgnoreCase);

    private ObsWebSocketClient? _client;
    private CancellationTokenSource? _runCts;
    private Task? _connectionTask;

    private bool _enabled;
    private string _audioSource = string.Empty;
    private string _defaultSceneName = string.Empty;
    private string _volumeScale = "percent";
    private long _meterThrottleMs = 50;

    private volatile bool _connected;
    private string? _obsVersion;
    private string? _currentScene;
    private string? _lastError;
    private string[] _scenes = Array.Empty<string>();
    private string[] _inputs = Array.Empty<string>();

    public override NodeTypeDescriptor Descriptor => TypeDescriptor;

    public static double MulToDb(double mul)
        => mul <= 0.000001 ? -100.0 : Math.Round(20.0 * Math.Log10(mul), 1);

    public static double DbToVuPercent(double db)
        => Math.Round(Math.Clamp((db + 60.0) / 60.0 * 100.0, 0.0, 100.0), 1);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _enabled = GetBool("enabled", true);
        _audioSource = GetString("audioSource", "").Trim();
        _defaultSceneName = GetString("sceneName", "").Trim();
        _volumeScale = GetString("volumeScale", "percent").Trim().ToLowerInvariant();
        _meterThrottleMs = (long)Math.Clamp(GetNumber("meterThrottleMs", 50), 0, 10000);
        _lastMeterTickMs.Clear();

        if (!_enabled)
        {
            _connected = false;
            _lastError = null;
            Context.ReportStatus("disabled");
            PublishCurrentWidgetState();
            return Task.CompletedTask;
        }

        var host = GetString("host", "127.0.0.1");
        var port = (int)Math.Clamp(GetNumber("port", 4455), 1, 65535);
        var password = GetString("password", "");
        var subscribeMeters = GetBool("subscribeMeters", true);

        var client = new ObsWebSocketClient();
        client.ConnectionStateChanged += OnConnectionStateChanged;
        client.EventReceived += OnEventReceived;

        _client = client;
        _runCts = new CancellationTokenSource();
        var loopToken = _runCts.Token;
        _connectionTask = Task.Run(() => client.ConnectLoopAsync(host, port, password, subscribeMeters, loopToken), CancellationToken.None);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await ShutdownClientAsync().ConfigureAwait(false);
        _lastMeterTickMs.Clear();
        _connected = false;
        PublishCurrentWidgetState();
    }

    public override async ValueTask DisposeAsync()
        => await ShutdownClientAsync().ConfigureAwait(false);

    public override async Task OnInputAsync(string inputPortId, FlowMessage message, CancellationToken cancellationToken)
    {
        var client = _client;
        if (client is not { IsConnected: true })
        {
            await EmitStatusAsync("obs.error", new Dictionary<string, object?>
            {
                ["type"] = "error",
                ["port"] = inputPortId,
                ["error"] = "OBS Studio is not connected.",
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            switch (inputPortId)
            {
                case "scene":
                    await HandleSceneInputAsync(client, message, cancellationToken).ConfigureAwait(false);
                    break;
                case "volume":
                    await HandleVolumeInputAsync(client, message, cancellationToken).ConfigureAwait(false);
                    break;
                case "mute":
                    await HandleMuteInputAsync(client, message, cancellationToken).ConfigureAwait(false);
                    break;
                case "record":
                    await HandleActionToggleAsync(client, message, "StartRecord", "StopRecord", "ToggleRecord", cancellationToken).ConfigureAwait(false);
                    break;
                case "stream":
                    await HandleActionToggleAsync(client, message, "StartStream", "StopStream", "ToggleStream", cancellationToken).ConfigureAwait(false);
                    break;
                case "command":
                    await HandleRawCommandAsync(client, message, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await EmitStatusAsync("obs.error", new Dictionary<string, object?>
            {
                ["type"] = "error",
                ["port"] = inputPortId,
                ["error"] = ex.Message,
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    public override async Task OnInteractAsync(string action, JsonElement? data, CancellationToken cancellationToken)
    {
        var client = _client;
        if (client is not { IsConnected: true })
            return;

        if (string.Equals(action, "refresh", StringComparison.OrdinalIgnoreCase))
        {
            await QueryInitialMetadataAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (string.Equals(action, "setScene", StringComparison.OrdinalIgnoreCase) &&
                 data is { ValueKind: JsonValueKind.Object } obj &&
                 ObsJsonHelpers.TryGetString(obj, "scene", out var scene) &&
                 !string.IsNullOrWhiteSpace(scene))
        {
            await client.SendRequestAsync("SetCurrentProgramScene", new { sceneName = scene }, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task HandleSceneInputAsync(ObsWebSocketClient client, FlowMessage message, CancellationToken ct)
    {
        var scene = message.PayloadAsString().Trim();
        if (string.IsNullOrEmpty(scene))
            scene = _defaultSceneName;
        if (string.IsNullOrEmpty(scene))
            return;

        var res = await client.SendRequestAsync("SetCurrentProgramScene", new { sceneName = scene }, ct).ConfigureAwait(false);
        if (!res.Result)
            await EmitResponseStatusAsync(res, ct).ConfigureAwait(false);
    }

    private async Task HandleVolumeInputAsync(ObsWebSocketClient client, FlowMessage message, CancellationToken ct)
    {
        var inputName = ResolveInputName(message);
        if (string.IsNullOrEmpty(inputName) || !ObsJsonHelpers.TryParseDouble(message.Payload, out var value))
            return;

        object requestData = _volumeScale switch
        {
            "mul" => new { inputName, inputVolumeMul = Math.Clamp(value, 0.0, 20.0) },
            "db" => value <= -100.0
                ? new { inputName, inputVolumeMul = 0.0 }
                : new { inputName, inputVolumeDb = Math.Clamp(value, -100.0, 26.0) },
            _ => BuildPercentVolumeRequest(inputName, value),
        };

        var res = await client.SendRequestAsync("SetInputVolume", requestData, ct).ConfigureAwait(false);
        if (!res.Result)
            await EmitResponseStatusAsync(res, ct).ConfigureAwait(false);
    }

    private static object BuildPercentVolumeRequest(string inputName, double percent)
    {
        var clamped = Math.Clamp(percent, 0.0, 100.0);
        if (clamped <= 0.001)
            return new { inputName, inputVolumeMul = 0.0 };

        return new { inputName, inputVolumeDb = Math.Round(-60.0 + (clamped / 100.0) * 60.0, 2) };
    }

    private async Task HandleMuteInputAsync(ObsWebSocketClient client, FlowMessage message, CancellationToken ct)
    {
        var inputName = ResolveInputName(message);
        if (string.IsNullOrEmpty(inputName))
            return;

        var res = ObsJsonHelpers.TryParseExplicitBool(message.Payload, out var muted)
            ? await client.SendRequestAsync("SetInputMute", new { inputName, inputMuted = muted }, ct).ConfigureAwait(false)
            : await client.SendRequestAsync("ToggleInputMute", new { inputName }, ct).ConfigureAwait(false);

        if (!res.Result)
            await EmitResponseStatusAsync(res, ct).ConfigureAwait(false);
    }

    private async Task HandleActionToggleAsync(
        ObsWebSocketClient client, FlowMessage message, string startReq, string stopReq, string toggleReq, CancellationToken ct)
    {
        var reqType = ObsJsonHelpers.ResolveStartStopToggle(message.Payload, startReq, stopReq, toggleReq);
        var res = await client.SendRequestAsync(reqType, null, ct).ConfigureAwait(false);
        if (!res.Result)
            await EmitResponseStatusAsync(res, ct).ConfigureAwait(false);
    }

    private async Task HandleRawCommandAsync(ObsWebSocketClient client, FlowMessage message, CancellationToken ct)
    {
        if (!ObsJsonHelpers.TryExtractRawCommand(message.Payload, out var requestType, out var requestData))
            return;

        var res = await client.SendRequestAsync(requestType, requestData, ct).ConfigureAwait(false);
        await EmitResponseStatusAsync(res, ct).ConfigureAwait(false);
    }

    private async Task ShutdownClientAsync()
    {
        var cts = Interlocked.Exchange(ref _runCts, null);
        var task = Interlocked.Exchange(ref _connectionTask, null);
        var client = Interlocked.Exchange(ref _client, null);

        if (cts is not null) { try { await cts.CancelAsync().ConfigureAwait(false); } catch { } }
        if (task is not null) { try { await task.ConfigureAwait(false); } catch { } }
        if (client is not null)
        {
            client.ConnectionStateChanged -= OnConnectionStateChanged;
            client.EventReceived -= OnEventReceived;
            await client.DisposeAsync().ConfigureAwait(false);
        }
        cts?.Dispose();
    }

    private string ResolveInputName(FlowMessage message)
    {
        if (message.Properties.TryGetValue("inputName", out var prop) && prop is not null)
        {
            var str = prop is JsonElement je && je.ValueKind == JsonValueKind.String ? je.GetString() : prop.ToString();
            if (!string.IsNullOrWhiteSpace(str))
                return str.Trim();
        }
        return _audioSource;
    }
}
