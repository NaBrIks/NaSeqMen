using System.Globalization;
using System.Text.Json;
using NaSeqMen.Core.Messages;
using NaSeqMen.Core.Nodes;

namespace NaSeqMen.Nodes.Controls;

public sealed class VirtualSliderNode : NodeBase
{
    public static readonly NodeTypeDescriptor TypeDescriptor = new()
    {
        TypeId = "controls.slider",
        DisplayName = "Virtual Slider",
        Category = "Controls",
        Color = "#db2777",
        Description = "Emulates an analog slider or fader directly on the canvas, with optional external feedback sync.",
        WidgetKind = "slider",
        Inputs =
        [
            new PortDefinition
            {
                Id = "set",
                Label = "Set Value",
                Direction = PortDirection.Input,
                DataType = PortDataType.Number,
                Description = "External feedback input to update slider position without causing a loop.",
            },
        ],
        Outputs =
        [
            new PortDefinition
            {
                Id = "value",
                Label = "Value",
                Direction = PortDirection.Output,
                DataType = PortDataType.Number,
                Description = "Emits the current numeric value whenever adjusted.",
            },
        ],
        Parameters =
        [
            new ParameterDefinition
            {
                Key = "min",
                Label = "Min",
                Kind = ParameterKind.Number,
                DefaultValue = 0,
                Inline = true,
            },
            new ParameterDefinition
            {
                Key = "max",
                Label = "Max",
                Kind = ParameterKind.Number,
                DefaultValue = 100,
                Inline = true,
            },
            new ParameterDefinition
            {
                Key = "step",
                Label = "Step",
                Kind = ParameterKind.Number,
                DefaultValue = 1,
                Inline = false,
            },
            new ParameterDefinition
            {
                Key = "defaultValue",
                Label = "Initial",
                Kind = ParameterKind.Number,
                DefaultValue = 75,
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
            new ParameterDefinition
            {
                Key = "topic",
                Label = "Topic",
                Kind = ParameterKind.String,
                DefaultValue = "control.slider",
                Inline = false,
            },
        ],
    };

    private double _value;

    public override NodeTypeDescriptor Descriptor => TypeDescriptor;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        var (min, max, step, unit) = ReadConfig();
        _value = Math.Clamp(GetNumber("defaultValue", 75), min, max);
        PublishSliderState(_value, min, max, step, unit);
        Context.ReportStatus(FormatStatus(_value, unit));
        return Task.CompletedTask;
    }

    public override Task OnInputAsync(string inputPortId, FlowMessage message, CancellationToken cancellationToken)
    {
        if (!string.Equals(inputPortId, "set", StringComparison.Ordinal))
            return Task.CompletedTask;

        if (!NumericPayloadHelper.TryExtractDouble(message.Payload, out var incoming))
            return Task.CompletedTask;

        var (min, max, step, unit) = ReadConfig();
        _value = Math.Clamp(incoming, min, max);
        PublishSliderState(_value, min, max, step, unit);
        Context.ReportStatus(FormatStatus(_value, unit));
        return Task.CompletedTask;
    }

    public override async Task OnInteractAsync(string action, JsonElement? data, CancellationToken cancellationToken)
    {
        if (!string.Equals(action, "change", StringComparison.OrdinalIgnoreCase))
            return;

        if (!TryExtractInteractionValue(data, out var rawValue))
            return;

        var (min, max, step, unit) = ReadConfig();
        _value = Math.Clamp(rawValue, min, max);
        PublishSliderState(_value, min, max, step, unit);
        Context.ReportStatus(FormatStatus(_value, unit));

        var msg = new FlowMessage
        {
            Topic = GetString("topic", "control.slider"),
            Payload = _value,
            Properties =
            {
                ["min"] = min,
                ["max"] = max,
                ["unit"] = unit,
            },
        };

        await EmitAsync("value", msg, cancellationToken);
    }

    private (double Min, double Max, double Step, string Unit) ReadConfig()
    {
        var rawMin = GetNumber("min", 0);
        var rawMax = GetNumber("max", 100);
        var min = Math.Min(rawMin, rawMax);
        var max = Math.Max(rawMin, rawMax);
        var step = GetNumber("step", 1);
        if (!double.IsFinite(step) || step <= 0)
            step = 1;
        var unit = GetString("unit", "%");
        return (min, max, step, unit);
    }

    private void PublishSliderState(double value, double min, double max, double step, string unit)
    {
        PublishWidgetState(new { value, min, max, step, unit });
    }

    private static string FormatStatus(double value, string unit)
        => $"{value.ToString("0.##", CultureInfo.InvariantCulture)}{unit}";

    private static bool TryExtractInteractionValue(JsonElement? data, out double value)
    {
        value = 0;
        if (!data.HasValue)
            return false;

        var element = data.Value;
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("value", out var prop))
            return NumericPayloadHelper.TryExtractDouble(prop, out value);

        return NumericPayloadHelper.TryExtractDouble(element, out value);
    }
}
