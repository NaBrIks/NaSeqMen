using System.Text.Json;
using NaSeqMen.Core.Messages;

namespace NaSeqMen.Nodes.Obs;

public sealed partial class ObsStudioNode
{
    private void OnConnectionStateChanged(ObsConnectionState state, string? error)
    {
        _connected = state == ObsConnectionState.Connected;
        _lastError = error;

        Context.ReportStatus(state switch
        {
            ObsConnectionState.Connected => "connected",
            ObsConnectionState.Connecting => "connecting...",
            ObsConnectionState.Reconnecting => "reconnecting...",
            ObsConnectionState.AuthFailed => "auth failed",
            _ => _enabled ? "disconnected" : "disabled",
        });
        PublishCurrentWidgetState();

        _ = EmitStatusAsync("obs.connection", new Dictionary<string, object?>
        {
            ["type"] = "connection",
            ["state"] = state.ToString(),
            ["connected"] = _connected,
            ["error"] = error,
        }, CancellationToken.None);

        if (state == ObsConnectionState.Connected)
            _ = QueryInitialMetadataAsync(_runCts?.Token ?? CancellationToken.None);
    }

    private async Task QueryInitialMetadataAsync(CancellationToken ct)
    {
        var client = _client;
        if (client is not { IsConnected: true })
            return;

        try
        {
            var versionTask = client.SendRequestAsync("GetVersion", null, ct);
            var scenesTask = client.SendRequestAsync("GetSceneList", null, ct);
            var inputsTask = client.SendRequestAsync("GetInputList", null, ct);
            await Task.WhenAll(versionTask, scenesTask, inputsTask).ConfigureAwait(false);

            var versionRes = versionTask.Result;
            if (versionRes.Result && versionRes.ResponseData is { ValueKind: JsonValueKind.Object } versionObj &&
                ObsJsonHelpers.TryGetString(versionObj, "obsVersion", out var obsVer))
            {
                _obsVersion = obsVer;
            }

            var scenesRes = scenesTask.Result;
            if (scenesRes.Result && scenesRes.ResponseData is { ValueKind: JsonValueKind.Object } scenesObj)
            {
                if (ObsJsonHelpers.TryGetString(scenesObj, "currentProgramSceneName", out var current))
                    _currentScene = current;

                if (scenesObj.TryGetProperty("scenes", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    var names = new List<string>();
                    foreach (var item in list.EnumerateArray())
                        if (ObsJsonHelpers.TryGetString(item, "sceneName", out var name) && !string.IsNullOrEmpty(name))
                            names.Add(name);
                    names.Reverse();
                    _scenes = names.ToArray();
                }
            }

            var inputsRes = inputsTask.Result;
            if (inputsRes.Result && inputsRes.ResponseData is { ValueKind: JsonValueKind.Object } inputsObj &&
                inputsObj.TryGetProperty("inputs", out var inputList) && inputList.ValueKind == JsonValueKind.Array)
            {
                var names = new List<string>();
                foreach (var item in inputList.EnumerateArray())
                    if (ObsJsonHelpers.TryGetString(item, "inputName", out var name) && !string.IsNullOrEmpty(name))
                        names.Add(name);
                _inputs = names.ToArray();
            }

            PublishCurrentWidgetState();
        }
        catch { }
    }

    private void OnEventReceived(ObsEvent evt)
    {
        if (evt.EventData is not { ValueKind: JsonValueKind.Object } data)
            return;

        switch (evt.EventType)
        {
            case "InputVolumeMeters":
                HandleVolumeMetersEvent(data);
                break;

            case "CurrentProgramSceneChanged":
                if (ObsJsonHelpers.TryGetString(data, "sceneName", out var sceneName))
                {
                    _currentScene = sceneName;
                    PublishCurrentWidgetState();
                    _ = EmitAsync("sceneChanged", new FlowMessage
                    {
                        Topic = "obs.scene",
                        Payload = sceneName,
                        OriginNodeId = Context.NodeId,
                        Properties = { ["sceneName"] = sceneName },
                    });
                }
                break;

            case "InputMuteStateChanged":
                if (ObsJsonHelpers.TryGetString(data, "inputName", out var muteInput) &&
                    MatchesAudioSource(muteInput) &&
                    ObsJsonHelpers.TryGetBool(data, "inputMuted", out var inputMuted))
                {
                    _ = EmitAsync("muteChanged", new FlowMessage
                    {
                        Topic = muteInput,
                        Payload = inputMuted,
                        OriginNodeId = Context.NodeId,
                        Properties = { ["inputName"] = muteInput, ["inputMuted"] = inputMuted },
                    });
                }
                break;

            case "InputVolumeChanged":
                if (ObsJsonHelpers.TryGetString(data, "inputName", out var volInput) && MatchesAudioSource(volInput))
                {
                    var mul = ObsJsonHelpers.TryGetDouble(data, "inputVolumeMul", out var m) ? m : 0.0;
                    var db = ObsJsonHelpers.TryGetDouble(data, "inputVolumeDb", out var d) ? Math.Round(d, 1) : MulToDb(mul);
                    var percent = DbToVuPercent(db);
                    var payload = _volumeScale switch { "mul" => Math.Round(mul, 4), "db" => db, _ => percent };

                    _ = EmitAsync("volumeChanged", new FlowMessage
                    {
                        Topic = volInput,
                        Payload = payload,
                        OriginNodeId = Context.NodeId,
                        Properties = { ["inputName"] = volInput, ["mul"] = Math.Round(mul, 6), ["db"] = db, ["percent"] = percent },
                    });
                }
                break;

            case "StreamStateChanged":
                EmitOutputStateEvent("stream", data);
                break;

            case "RecordStateChanged":
                EmitOutputStateEvent("record", data);
                break;

            case "SceneListChanged":
            case "InputCreated":
            case "InputRemoved":
            case "InputNameChanged":
                _ = QueryInitialMetadataAsync(_runCts?.Token ?? CancellationToken.None);
                break;
        }
    }

    private void HandleVolumeMetersEvent(JsonElement data)
    {
        if (!data.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Array)
            return;

        var now = Environment.TickCount64;
        foreach (var item in inputs.EnumerateArray())
        {
            if (!ObsJsonHelpers.TryGetString(item, "inputName", out var inputName) || string.IsNullOrEmpty(inputName))
                continue;
            if (!MatchesAudioSource(inputName))
                continue;
            if (_meterThrottleMs > 0 && _lastMeterTickMs.TryGetValue(inputName, out var lastTick) && now - lastTick < _meterThrottleMs)
                continue;

            _lastMeterTickMs[inputName] = now;
            var (leftMul, rightMul, maxMul) = ExtractMeterLevels(item);
            var db = MulToDb(maxMul);
            var vuPercent = DbToVuPercent(db);

            _ = EmitAsync("vuLevel", new FlowMessage
            {
                Topic = inputName,
                Payload = vuPercent,
                OriginNodeId = Context.NodeId,
                Properties =
                {
                    ["inputName"] = inputName,
                    ["mul"] = Math.Round(maxMul, 6),
                    ["db"] = db,
                    ["leftPercent"] = DbToVuPercent(MulToDb(leftMul)),
                    ["rightPercent"] = DbToVuPercent(MulToDb(rightMul)),
                },
            });
        }
    }

    private static (double Left, double Right, double Max) ExtractMeterLevels(JsonElement item)
    {
        double left = 0, right = 0, max = 0;
        var chIndex = 0;
        if (item.TryGetProperty("inputLevelsMul", out var levels) && levels.ValueKind == JsonValueKind.Array)
        {
            foreach (var channel in levels.EnumerateArray())
            {
                var peak = ObsJsonHelpers.ExtractChannelPeak(channel);
                if (chIndex == 0) left = peak;
                else if (chIndex == 1) right = peak;
                if (peak > max) max = peak;
                chIndex++;
            }
            if (chIndex == 1) right = left;
        }
        return (left, right, max);
    }

    private void EmitOutputStateEvent(string type, JsonElement data)
    {
        var active = ObsJsonHelpers.TryGetBool(data, "outputActive", out var a) && a;
        var state = ObsJsonHelpers.TryGetString(data, "outputState", out var s) ? s : string.Empty;
        _ = EmitStatusAsync($"obs.{type}", new Dictionary<string, object?>
        {
            ["type"] = type,
            ["outputActive"] = active,
            ["outputState"] = state,
        }, CancellationToken.None);
    }

    private Task EmitResponseStatusAsync(ObsRequestResponse res, CancellationToken ct)
        => EmitStatusAsync("obs.response", new Dictionary<string, object?>
        {
            ["type"] = "response",
            ["requestType"] = res.RequestType,
            ["requestId"] = res.RequestId,
            ["ok"] = res.Result,
            ["code"] = res.Code,
            ["comment"] = res.Comment,
            ["responseData"] = res.ResponseData,
        }, ct);

    private Task EmitStatusAsync(string topic, Dictionary<string, object?> payload, CancellationToken ct)
        => EmitAsync("status", new FlowMessage { Topic = topic, Payload = payload, OriginNodeId = Context.NodeId }, ct);

    private void PublishCurrentWidgetState()
        => PublishWidgetState(new
        {
            connected = _connected,
            scene = _currentScene,
            inputs = _inputs,
            scenes = _scenes,
            error = _lastError,
            obsVersion = _obsVersion,
        });

    private bool MatchesAudioSource(string inputName)
        => string.IsNullOrEmpty(_audioSource) || string.Equals(_audioSource, inputName, StringComparison.OrdinalIgnoreCase);
}
