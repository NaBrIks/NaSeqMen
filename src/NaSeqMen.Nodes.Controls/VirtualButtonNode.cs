using System.Globalization;
using System.Text.Json;
using NaSeqMen.Core.Messages;
using NaSeqMen.Core.Nodes;

namespace NaSeqMen.Nodes.Controls;

public sealed class VirtualButtonNode : NodeBase
{
    public static readonly NodeTypeDescriptor TypeDescriptor = new()
    {
        TypeId = "controls.button",
        DisplayName = "Virtual Button",
        Category = "Controls",
        Color = "#d97706",
        Description = "Emulates a momentary or toggle button directly on the canvas, with optional external state feedback.",
        WidgetKind = "button",
        SupportsManualTrigger = true,
        Inputs =
        [
            new PortDefinition
            {
                Id = "set",
                Label = "Set State",
                Direction = PortDirection.Input,
                DataType = PortDataType.Boolean,
                Description = "Optional feedback input to sync visual toggle state without re-emitting.",
            },
        ],
        Outputs =
        [
            new PortDefinition
            {
                Id = "press",
                Label = "Press",
                Direction = PortDirection.Output,
                DataType = PortDataType.Any,
                Description = "Emits payload on button press or toggle change.",
            },
            new PortDefinition
            {
                Id = "release",
                Label = "Release",
                Direction = PortDirection.Output,
                DataType = PortDataType.Any,
                Description = "Emits payload on button release in momentary mode.",
            },
        ],
        Parameters =
        [
            new ParameterDefinition
            {
                Key = "buttonLabel",
                Label = "Button Text",
                Kind = ParameterKind.String,
                DefaultValue = "Press",
                Inline = true,
            },
            new ParameterDefinition
            {
                Key = "mode",
                Label = "Mode",
                Kind = ParameterKind.Select,
                DefaultValue = "momentary",
                Inline = true,
                Options =
                [
                    new ParameterOption("momentary", "Momentary"),
                    new ParameterOption("toggle", "Toggle"),
                ],
            },
            new ParameterDefinition
            {
                Key = "payload",
                Label = "On / Press Payload",
                Kind = ParameterKind.String,
                DefaultValue = "toggle",
                Inline = true,
                Description = "Supports string, number, true/false",
            },
            new ParameterDefinition
            {
                Key = "offPayload",
                Label = "Off Payload",
                Kind = ParameterKind.String,
                DefaultValue = "false",
                Inline = false,
            },
            new ParameterDefinition
            {
                Key = "topic",
                Label = "Topic",
                Kind = ParameterKind.String,
                DefaultValue = "control.button",
                Inline = false,
            },
        ],
    };

    private bool _isOn;

    public override NodeTypeDescriptor Descriptor => TypeDescriptor;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _isOn = false;
        PublishState(_isOn);
        return Task.CompletedTask;
    }

    public override Task OnInputAsync(string inputPortId, FlowMessage message, CancellationToken cancellationToken)
    {
        if (!string.Equals(inputPortId, "set", StringComparison.Ordinal))
            return Task.CompletedTask;

        if (!TryExtractBool(message.Payload, out var active))
            return Task.CompletedTask;

        _isOn = active;
        PublishState(_isOn);
        Context.ReportStatus(_isOn ? "ON" : "OFF");
        return Task.CompletedTask;
    }

    public override Task OnManualTriggerAsync(FlowMessage? message, CancellationToken cancellationToken)
        => HandlePressAsync(cancellationToken);

    public override Task OnInteractAsync(string action, JsonElement? data, CancellationToken cancellationToken)
    {
        if (string.Equals(action, "press", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action, "click", StringComparison.OrdinalIgnoreCase))
        {
            return HandlePressAsync(cancellationToken);
        }

        if (string.Equals(action, "release", StringComparison.OrdinalIgnoreCase))
        {
            return HandleReleaseAsync(cancellationToken);
        }

        return Task.CompletedTask;
    }

    private async Task HandlePressAsync(CancellationToken cancellationToken)
    {
        var isToggle = IsToggleMode();
        var topic = GetString("topic", "control.button");

        if (isToggle)
        {
            _isOn = !_isOn;
            PublishState(_isOn);
            Context.ReportStatus(_isOn ? "ON" : "OFF");

            var raw = _isOn
                ? GetString("payload", "toggle")
                : GetString("offPayload", "false");

            var msg = new FlowMessage
            {
                Topic = topic,
                Payload = ParseTypedPayload(raw),
                Properties =
                {
                    ["mode"] = "toggle",
                    ["state"] = _isOn,
                    ["action"] = "toggle",
                },
            };

            await EmitAsync("press", msg, cancellationToken);
            return;
        }

        _isOn = true;
        PublishState(true);
        Context.ReportStatus("pressed");

        var pressMsg = new FlowMessage
        {
            Topic = topic,
            Payload = ParseTypedPayload(GetString("payload", "toggle")),
            Properties =
            {
                ["mode"] = "momentary",
                ["state"] = true,
                ["action"] = "press",
            },
        };

        await EmitAsync("press", pressMsg, cancellationToken);
    }

    private async Task HandleReleaseAsync(CancellationToken cancellationToken)
    {
        if (IsToggleMode())
            return;

        _isOn = false;
        PublishState(false);
        Context.ReportStatus("released");

        var releaseMsg = new FlowMessage
        {
            Topic = GetString("topic", "control.button"),
            Payload = ParseTypedPayload(GetString("offPayload", "false")),
            Properties =
            {
                ["mode"] = "momentary",
                ["state"] = false,
                ["action"] = "release",
            },
        };

        await EmitAsync("release", releaseMsg, cancellationToken);
    }

    private bool IsToggleMode()
        => string.Equals(GetString("mode", "momentary"), "toggle", StringComparison.OrdinalIgnoreCase);

    private void PublishState(bool active)
    {
        var label = GetString("buttonLabel", "Press");
        PublishWidgetState(new { active, label });
    }

    public static object ParseTypedPayload(string raw)
    {
        var trimmed = raw.Trim();
        if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase))
            return false;
        if (double.TryParse(trimmed, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            && double.IsFinite(number))
            return number;

        return raw;
    }

    private static bool TryExtractBool(object? payload, out bool value)
    {
        switch (payload)
        {
            case bool b:
                value = b;
                return true;
            case int i:
                value = i != 0;
                return true;
            case long l:
                value = l != 0;
                return true;
            case double d when double.IsFinite(d):
                value = Math.Abs(d) > double.Epsilon;
                return true;
            case float f when float.IsFinite(f):
                value = Math.Abs(f) > float.Epsilon;
                return true;
            case decimal m:
                value = m != 0m;
                return true;
            case JsonElement je:
                return TryExtractBoolFromJson(je, out value);
            case string s:
                return TryParseBoolString(s, out value);
            default:
                value = false;
                return false;
        }
    }

    private static bool TryExtractBoolFromJson(JsonElement element, out bool value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                value = false;
                return true;
            case JsonValueKind.Number when element.TryGetDouble(out var d):
                value = Math.Abs(d) > double.Epsilon;
                return true;
            case JsonValueKind.String:
                return TryParseBoolString(element.GetString() ?? string.Empty, out value);
            default:
                value = false;
                return false;
        }
    }

    private static bool TryParseBoolString(string text, out bool value)
    {
        var trimmed = text.Trim();
        if (bool.TryParse(trimmed, out value))
            return true;
        if (string.Equals(trimmed, "1", StringComparison.Ordinal)
            || string.Equals(trimmed, "on", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }
        if (string.Equals(trimmed, "0", StringComparison.Ordinal)
            || string.Equals(trimmed, "off", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }

        value = false;
        return false;
    }
}
