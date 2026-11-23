using System.Globalization;
using NaSeqMen.Core.Messages;
using NaSeqMen.Core.Nodes;

namespace NaSeqMen.Nodes.Controls;

public sealed class VuMeterNode : NodeBase
{
    private const double PeakHoldSeconds = 1.2;
    private const double PeakDecayPerSecond = 80.0;

    public static readonly NodeTypeDescriptor TypeDescriptor = new()
    {
        TypeId = "controls.vu-meter",
        DisplayName = "VU Meter",
        Category = "Controls",
        Color = "#059669",
        Description = "Live visual audio or level meter with peak hold and optional source filtering.",
        WidgetKind = "vu-meter",
        Inputs =
        [
            new PortDefinition
            {
                Id = "in",
                Label = "Level",
                Direction = PortDirection.Input,
                DataType = PortDataType.Number,
                Description = "Incoming numeric level measurement.",
            },
        ],
        Outputs =
        [
            new PortDefinition
            {
                Id = "out",
                Label = "Pass-through",
                Direction = PortDirection.Output,
                DataType = PortDataType.Number,
                Description = "Forwards matching messages downstream.",
            },
        ],
        Parameters =
        [
            new ParameterDefinition
            {
                Key = "sourceFilter",
                Label = "Source Filter",
                Kind = ParameterKind.String,
                DefaultValue = "",
                Placeholder = "Optional OBS source name",
                Inline = true,
            },
            new ParameterDefinition
            {
                Key = "min",
                Label = "Min",
                Kind = ParameterKind.Number,
                DefaultValue = 0,
                Inline = false,
            },
            new ParameterDefinition
            {
                Key = "max",
                Label = "Max",
                Kind = ParameterKind.Number,
                DefaultValue = 100,
                Inline = false,
            },
            new ParameterDefinition
            {
                Key = "unit",
                Label = "Unit",
                Kind = ParameterKind.String,
                DefaultValue = "%",
                Inline = false,
            },
        ],
    };

    private readonly object _peakLock = new();
    private double _peak;
    private DateTimeOffset _peakSetAt;
    private DateTimeOffset _lastUpdateAt;

    public override NodeTypeDescriptor Descriptor => TypeDescriptor;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_peakLock)
        {
            _peak = 0;
            _peakSetAt = DateTimeOffset.UtcNow;
            _lastUpdateAt = _peakSetAt;
        }

        var unit = GetString("unit", "%");
        PublishWidgetState(new
        {
            level = 0.0,
            left = 0.0,
            right = 0.0,
            peak = 0.0,
            db = (double?)null,
            raw = 0.0,
            unit,
            source = string.Empty,
        });

        return Task.CompletedTask;
    }

    public override async Task OnInputAsync(string inputPortId, FlowMessage message, CancellationToken cancellationToken)
    {
        var inputName = TryGetStringProperty(message.Properties, "inputName");
        var sourceFilter = GetString("sourceFilter", "").Trim();
        if (!string.IsNullOrEmpty(sourceFilter) && !MatchesFilter(sourceFilter, inputName, message.Topic))
            return;

        if (!NumericPayloadHelper.TryExtractDouble(message.Payload, out var val))
            return;

        var min = GetNumber("min", 0);
        var max = GetNumber("max", 100);
        var unit = GetString("unit", "%");

        var span = max - min;
        var percent = Math.Abs(span) < 1e-12
            ? (val >= max ? 100.0 : 0.0)
            : Math.Clamp((val - min) / span * 100.0, 0.0, 100.0);

        var left = TryGetDoubleProperty(message.Properties, "leftPercent", out var leftVal)
            ? Math.Clamp(leftVal, 0.0, 100.0)
            : percent;
        var right = TryGetDoubleProperty(message.Properties, "rightPercent", out var rightVal)
            ? Math.Clamp(rightVal, 0.0, 100.0)
            : percent;
        double? db = TryGetDoubleProperty(message.Properties, "db", out var dbVal)
            ? Math.Round(dbVal, 1)
            : null;

        var peak = UpdatePeakHold(percent);
        var source = !string.IsNullOrEmpty(inputName) ? inputName : (message.Topic ?? string.Empty);

        PublishWidgetState(new
        {
            level = Math.Round(percent, 1),
            left = Math.Round(left, 1),
            right = Math.Round(right, 1),
            peak,
            db,
            raw = val,
            unit,
            source,
        });

        var status = db.HasValue
            ? $"{db.Value.ToString("0.0", CultureInfo.InvariantCulture)} dB"
            : $"{val.ToString("0.#", CultureInfo.InvariantCulture)}{unit}";
        Context.ReportStatus(status);

        await EmitAsync("out", message.Clone(), cancellationToken);
    }

    private double UpdatePeakHold(double percent)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_peakLock)
        {
            if (percent >= _peak)
            {
                _peak = percent;
                _peakSetAt = now;
                _lastUpdateAt = now;
            }
            else
            {
                var holdElapsed = (now - _peakSetAt).TotalSeconds;
                if (holdElapsed >= PeakHoldSeconds)
                {
                    var dt = Math.Max(0.0, (now - _lastUpdateAt).TotalSeconds);
                    _peak = Math.Max(percent, _peak - dt * PeakDecayPerSecond);
                    if (_peak <= percent)
                    {
                        _peak = percent;
                        _peakSetAt = now;
                    }
                }

                _lastUpdateAt = now;
            }

            return Math.Round(_peak, 1);
        }
    }

    private static bool MatchesFilter(string filter, string? inputName, string? topic)
    {
        if (!string.IsNullOrEmpty(inputName) && inputName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrEmpty(topic) && topic.Contains(filter, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static string? TryGetStringProperty(Dictionary<string, object?> props, string key)
    {
        if (!props.TryGetValue(key, out var raw) || raw is null)
            return null;
        return raw as string ?? raw.ToString();
    }

    private static bool TryGetDoubleProperty(Dictionary<string, object?> props, string key, out double value)
    {
        if (props.TryGetValue(key, out var raw))
            return NumericPayloadHelper.TryExtractDouble(raw, out value);

        value = 0;
        return false;
    }
}
