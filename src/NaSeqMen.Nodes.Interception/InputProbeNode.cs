using System.Text.Json;
using NaSeqMen.Core.Messages;
using NaSeqMen.Core.Nodes;

namespace NaSeqMen.Nodes.Interception;

/// <summary>
/// Interactive diagnostic probe / trap node for inspecting incoming strokes in a live mini-terminal
/// and optionally suppressing them while armed, with multi-layer safety failsafes
/// (inactivity timeout, hard max-duration cap, and instant Esc disarm).
/// </summary>
public sealed class InputProbeNode : NodeBase
{
    public static readonly NodeTypeDescriptor TypeDescriptor = new()
    {
        TypeId = "interception.probe",
        DisplayName = "Input Probe",
        Category = "Interception",
        Color = "#0f766e",
        WidgetKind = "input-probe",
        Description = "Live mini-terminal for inspecting hardware strokes. When armed with Block enabled, suppresses original input while showing exact key/button codes; auto-disarms on inactivity, max duration, or Esc.",
        Inputs =
        [
            new PortDefinition
            {
                Id = "in",
                Label = "Event",
                Direction = PortDirection.Input,
                DataType = PortDataType.Object,
                Description = "Incoming stroke basket from Interception Device or Hub.",
            },
        ],
        Outputs =
        [
            new PortDefinition
            {
                Id = "return",
                Label = "Return",
                Direction = PortDirection.Output,
                DataType = PortDataType.Object,
                Description = "Connect back to Interception Device/Hub [Return / Action]. Suppresses while armed + blocking; passes through otherwise.",
            },
            new PortDefinition
            {
                Id = "observed",
                Label = "Observed",
                Direction = PortDirection.Output,
                DataType = PortDataType.Object,
                Description = "Optional tap forwarding every observed event (e.g. to Debug).",
            },
        ],
        Parameters =
        [
            new ParameterDefinition
            {
                Key = "blockWhenArmed",
                Label = "Block When Armed",
                Kind = ParameterKind.Boolean,
                DefaultValue = true,
                Inline = true,
                Description = "Suppress original strokes while armed so keys/buttons do not trigger Windows actions during inspection.",
            },
            new ParameterDefinition
            {
                Key = "idleTimeoutSec",
                Label = "Idle Auto-Off (s)",
                Kind = ParameterKind.Number,
                DefaultValue = 15,
                Inline = false,
                Description = "Automatically disarm if no events arrive for this many seconds.",
            },
            new ParameterDefinition
            {
                Key = "maxDurationSec",
                Label = "Max Armed (s)",
                Kind = ParameterKind.Number,
                DefaultValue = 45,
                Inline = false,
                Description = "Hard safety cap on how long blocking capture can remain armed.",
            },
            new ParameterDefinition
            {
                Key = "maxLines",
                Label = "Terminal Lines",
                Kind = ParameterKind.Number,
                DefaultValue = 8,
                Inline = false,
            },
        ],
    };

    private sealed record ProbeLine(string Time, string Device, string Phase, string Key, string Extra, bool Blocked);

    private readonly object _sync = new();
    private readonly List<ProbeLine> _lines = [];
    private CancellationTokenSource? _watchdogCts;

    private bool _armed;
    private bool _blockWhenArmed = true;
    private double _idleTimeoutSec = 15;
    private double _maxDurationSec = 45;
    private int _maxLines = 8;
    private DateTimeOffset _armedAtUtc;
    private DateTimeOffset _lastActivityUtc;
    private string? _lastDisarmReason;

    public override NodeTypeDescriptor Descriptor => TypeDescriptor;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _armed = false;
            _blockWhenArmed = GetBool("blockWhenArmed", true);
            _idleTimeoutSec = Math.Clamp(GetNumber("idleTimeoutSec", 15), 1, 120);
            _maxDurationSec = Math.Clamp(GetNumber("maxDurationSec", 45), 2, 180);
            _maxLines = (int)Math.Clamp(GetNumber("maxLines", 8), 3, 25);
            _lastDisarmReason = null;
            StopWatchdogLocked();
        }

        PublishProbeWidget();
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _armed = false;
            StopWatchdogLocked();
        }
        return Task.CompletedTask;
    }

    public override Task OnInteractAsync(string action, JsonElement? data, CancellationToken cancellationToken)
    {
        switch (action.ToLowerInvariant())
        {
            case "arm":
                SetArmed(true);
                break;
            case "disarm":
                SetArmed(false, "manual");
                break;
            case "toggle":
                SetArmed(!IsArmed);
                break;
            case "clear":
                lock (_sync)
                {
                    _lines.Clear();
                    _lastDisarmReason = null;
                }
                PublishProbeWidget();
                break;
        }

        return Task.CompletedTask;
    }

    public override async Task OnInputAsync(string inputPortId, FlowMessage message, CancellationToken cancellationToken)
    {
        var key = StrokeActionNode.ExtractStrokeKey(message);
        var phase = StrokeActionNode.ExtractPhase(message);
        var slot = DecisionParser.TryReadInt(message.Properties, "deviceSlot") ?? 0;
        var kind = message.Properties.TryGetValue("deviceKind", out var kObj) ? kObj?.ToString() ?? "" : "";
        var deviceName = DecisionParser.TryReadString(message.Properties, "deviceName");
        var scanCode = DecisionParser.TryReadInt(message.Properties, "scanCode");
        var isE0 = message.Properties.TryGetValue("isE0", out var e0Obj) && e0Obj is true;

        var next = message.Clone();
        var shouldSuppress = false;
        var disarmedByEsc = false;

        lock (_sync)
        {
            if (_armed)
            {
                if (ScanCodeMap.KeyMatches("Esc", key))
                {
                    _armed = false;
                    _lastDisarmReason = "Esc";
                    StopWatchdogLocked();
                    disarmedByEsc = true;
                    shouldSuppress = false;
                }
                else
                {
                    _lastActivityUtc = DateTimeOffset.UtcNow;
                    shouldSuppress = _blockWhenArmed;
                }
            }

            var shortBrand = ExtractShortBrand(deviceName);
            var baseTag = kind.Equals("mouse", StringComparison.OrdinalIgnoreCase)
                ? $"M#{slot}"
                : slot > 0 ? $"KB#{slot}" : "IN";
            var devTag = !string.IsNullOrEmpty(shortBrand) ? $"{baseTag} {shortBrand}" : baseTag;
            var phaseArrow = phase switch
            {
                "down" => "↓",
                "up" => "↑",
                "wheel" => "↕",
                _ => "•",
            };
            var extra = scanCode.HasValue && scanCode.Value > 0
                ? $"SC:{scanCode.Value:X2}{(isE0 ? " E0" : "")}"
                : string.Empty;

            _lines.Add(new ProbeLine(
                DateTimeOffset.Now.ToString("HH:mm:ss.fff"),
                devTag,
                phaseArrow,
                key,
                extra,
                shouldSuppress));

            while (_lines.Count > _maxLines)
                _lines.RemoveAt(0);
        }

        next.Properties["suppress"] = shouldSuppress;
        if (shouldSuppress)
            next.Properties["blockActions"] = true;
        if (disarmedByEsc)
            next.Properties["probeDisarmed"] = "Esc";

        PublishProbeWidget();

        await EmitAsync("return", next, cancellationToken);
        await EmitAsync("observed", next.Clone(), cancellationToken);
    }

    private static string ExtractShortBrand(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName) || deviceName.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var s = deviceName.Trim();
        var paren = s.IndexOf('(');
        if (paren > 0) s = s[..paren].Trim();
        var dash = s.IndexOf('—');
        if (dash > 0) s = s[..dash].Trim();
        return s.Length <= 12 ? s : s[..12];
    }

    private bool IsArmed
    {
        get { lock (_sync) return _armed; }
    }

    private void SetArmed(bool armed, string? reason = null)
    {
        lock (_sync)
        {
            _armed = armed;
            StopWatchdogLocked();
            if (armed)
            {
                var now = DateTimeOffset.UtcNow;
                _armedAtUtc = now;
                _lastActivityUtc = now;
                _lastDisarmReason = null;
                StartWatchdogLocked();
            }
            else
            {
                _lastDisarmReason = reason;
            }
        }

        PublishProbeWidget();
    }

    private void StartWatchdogLocked()
    {
        var cts = new CancellationTokenSource();
        _watchdogCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
                while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
                {
                    var disarmed = false;
                    lock (_sync)
                    {
                        if (!_armed)
                            return;

                        var now = DateTimeOffset.UtcNow;
                        var idleElapsed = (now - _lastActivityUtc).TotalSeconds;
                        var totalElapsed = (now - _armedAtUtc).TotalSeconds;

                        if (idleElapsed >= _idleTimeoutSec)
                        {
                            _armed = false;
                            _lastDisarmReason = "idle timeout";
                            StopWatchdogLocked();
                            disarmed = true;
                        }
                        else if (totalElapsed >= _maxDurationSec)
                        {
                            _armed = false;
                            _lastDisarmReason = "max duration";
                            StopWatchdogLocked();
                            disarmed = true;
                        }
                    }

                    PublishProbeWidget();
                    if (disarmed)
                        return;
                }
            }
            catch (OperationCanceledException)
            {
                // Stopped normally
            }
        });
    }

    private void StopWatchdogLocked()
    {
        if (_watchdogCts is null)
            return;

        try { _watchdogCts.Cancel(); } catch { }
        try { _watchdogCts.Dispose(); } catch { }
        _watchdogCts = null;
    }

    private void PublishProbeWidget()
    {
        bool armed;
        bool blocking;
        int remainingSec;
        string? reason;
        ProbeLine[] linesCopy;

        lock (_sync)
        {
            armed = _armed;
            blocking = _armed && _blockWhenArmed;
            reason = _lastDisarmReason;
            linesCopy = _lines.ToArray();

            if (armed)
            {
                var now = DateTimeOffset.UtcNow;
                var remIdle = Math.Max(0, _idleTimeoutSec - (now - _lastActivityUtc).TotalSeconds);
                var remMax = Math.Max(0, _maxDurationSec - (now - _armedAtUtc).TotalSeconds);
                remainingSec = (int)Math.Ceiling(Math.Min(remIdle, remMax));
            }
            else
            {
                remainingSec = 0;
            }
        }

        Context.ReportStatus(armed
            ? (blocking ? $"BLOCKING ({remainingSec}s)" : $"armed ({remainingSec}s)")
            : (reason is not null ? $"monitor ({reason})" : "monitor"));

        PublishWidgetState(new
        {
            armed,
            blocking,
            remainingSec,
            reason,
            lines = linesCopy.Select(l => new
            {
                time = l.Time,
                device = l.Device,
                phase = l.Phase,
                key = l.Key,
                extra = l.Extra,
                blocked = l.Blocked,
            }).ToArray(),
        });
    }

    public override ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _armed = false;
            StopWatchdogLocked();
        }
        return ValueTask.CompletedTask;
    }
}
