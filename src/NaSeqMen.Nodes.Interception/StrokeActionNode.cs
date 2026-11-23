using NaSeqMen.Core.Messages;
using NaSeqMen.Core.Nodes;

namespace NaSeqMen.Nodes.Interception;

/// <summary>
/// Universal rule node that matches a hardware key or mouse button stroke, sets suppress/pass,
/// and optionally attaches replacement key or mouse actions onto the message basket.
/// </summary>
public sealed class StrokeActionNode : NodeBase
{
    public static readonly NodeTypeDescriptor TypeDescriptor = new()
    {
        TypeId = "interception.stroke-action",
        DisplayName = "Stroke Action",
        Category = "Interception",
        Color = "#ea580c",
        Description = "Matches a configured key/button and phase, marks the basket to suppress or pass the original stroke, and optionally attaches replacement key chords, sequences, or mouse actions.",
        Inputs =
        [
            new PortDefinition
            {
                Id = "in",
                Label = "Event",
                Direction = PortDirection.Input,
                DataType = PortDataType.Object,
            },
        ],
        Outputs =
        [
            new PortDefinition
            {
                Id = "matched",
                Label = "Matched",
                Direction = PortDirection.Output,
                DataType = PortDataType.Object,
                Description = "Emitted when the stroke matches Match Key and Phase, with decision properties attached.",
            },
            new PortDefinition
            {
                Id = "unmatched",
                Label = "Other",
                Direction = PortDirection.Output,
                DataType = PortDataType.Object,
                Description = "Emitted unchanged when the stroke does not match.",
            },
        ],
        Parameters =
        [
            new ParameterDefinition
            {
                Key = "matchKey",
                Label = "Match Key",
                Kind = ParameterKind.Key,
                DefaultValue = "",
                Placeholder = "Key/Button (e.g. D, LWin, X1, *)",
                Inline = true,
                Description = "Key or mouse button name(s) separated by commas, or * to match any stroke.",
            },
            new ParameterDefinition
            {
                Key = "matchPhase",
                Label = "Phase",
                Kind = ParameterKind.Select,
                DefaultValue = "any",
                Inline = true,
                Options =
                [
                    new ParameterOption("any", "Any"),
                    new ParameterOption("down", "Down"),
                    new ParameterOption("up", "Up"),
                ],
            },
            new ParameterDefinition
            {
                Key = "suppress",
                Label = "Suppress",
                Kind = ParameterKind.Select,
                DefaultValue = "suppress",
                Inline = true,
                Options =
                [
                    new ParameterOption("suppress", "Suppress"),
                    new ParameterOption("pass", "Pass"),
                    new ParameterOption("unchanged", "Unchanged"),
                ],
            },
            new ParameterDefinition
            {
                Key = "sendAction",
                Label = "Send Action",
                Kind = ParameterKind.KeyCombo,
                DefaultValue = "",
                Placeholder = "e.g. LCtrl+X or LCtrl+K, LCtrl+D",
                Inline = true,
                Description = "Optional replacement key chord, comma-separated sequence, or mouse button action.",
            },
            new ParameterDefinition
            {
                Key = "sendMode",
                Label = "Send Mode",
                Kind = ParameterKind.Select,
                DefaultValue = "tapOnDown",
                Inline = false,
                Options =
                [
                    new ParameterOption("tapOnDown", "Tap on Down"),
                    new ParameterOption("mirrorHold", "Mirror Hold"),
                    new ParameterOption("tapOnUp", "Tap on Up"),
                ],
                Description = "Controls when replacement strokes are generated relative to key down/up.",
            },
            new ParameterDefinition
            {
                Key = "targetKeyboardFilter",
                Label = "Target KB Filter",
                Kind = ParameterKind.String,
                DefaultValue = "",
                Placeholder = "Optional target KB HWID",
                Inline = false,
                Description = "Optional Hardware ID substring selecting which keyboard slot injects replacement keys.",
            },
        ],
    };

    public override NodeTypeDescriptor Descriptor => TypeDescriptor;

    public override async Task OnInputAsync(string inputPortId, FlowMessage message, CancellationToken cancellationToken)
    {
        var matchKey = GetString("matchKey", "").Trim();
        var matchPhase = GetString("matchPhase", "any").Trim().ToLowerInvariant();
        var suppressMode = GetString("suppress", "suppress").Trim().ToLowerInvariant();
        var sendAction = GetString("sendAction", "").Trim();
        var sendMode = GetString("sendMode", "tapOnDown").Trim();
        var targetKbFilter = GetString("targetKeyboardFilter", "").Trim();

        var actualKey = ExtractStrokeKey(message);
        var actualPhase = ExtractPhase(message);

        if (!ScanCodeMap.KeyMatches(matchKey, actualKey) || !PhaseMatches(matchPhase, actualPhase))
        {
            await EmitAsync("unmatched", message.Clone(), cancellationToken);
            return;
        }

        var next = message.Clone();
        if (suppressMode == "suppress")
            next.Properties["suppress"] = true;
        else if (suppressMode == "pass")
            next.Properties["suppress"] = false;

        if (!string.IsNullOrEmpty(sendAction))
            AttachSendAction(next, sendAction, sendMode, actualPhase);

        if (!string.IsNullOrEmpty(targetKbFilter))
            next.Properties["sendHardwareId"] = targetKbFilter;

        Context.ReportStatus($"{actualKey} ({actualPhase}) -> {suppressMode}");
        await EmitAsync("matched", next, cancellationToken);
    }

    internal static void AttachSendAction(FlowMessage target, string sendAction, string sendMode, string phase)
    {
        var isMouse = ScanCodeMap.IsMouseActionExpression(sendAction);

        if (sendMode.Equals("mirrorHold", StringComparison.OrdinalIgnoreCase))
        {
            if (phase == "down")
            {
                if (isMouse)
                {
                    var steps = ScanCodeMap.ParseMouseHoldPress(sendAction);
                    if (steps.Count > 0) target.Properties["sendMouse"] = steps;
                }
                else
                {
                    var steps = ScanCodeMap.ParseHoldPress(sendAction);
                    if (steps.Count > 0) target.Properties["sendKeys"] = steps;
                }
            }
            else if (phase == "up")
            {
                if (isMouse)
                {
                    var steps = ScanCodeMap.ParseMouseHoldRelease(sendAction);
                    if (steps.Count > 0) target.Properties["sendMouse"] = steps;
                }
                else
                {
                    var steps = ScanCodeMap.ParseHoldRelease(sendAction);
                    if (steps.Count > 0) target.Properties["sendKeys"] = steps;
                }
            }

            return;
        }

        var shouldTap = sendMode.Equals("tapOnUp", StringComparison.OrdinalIgnoreCase)
            ? phase == "up"
            : phase is "down" or "wheel";

        if (!shouldTap)
            return;

        if (isMouse)
        {
            var steps = ScanCodeMap.ParseMouseSequence(sendAction);
            if (steps.Count > 0) target.Properties["sendMouse"] = steps;
        }
        else
        {
            var steps = ScanCodeMap.ParseKeySequence(sendAction);
            if (steps.Count > 0) target.Properties["sendKeys"] = steps;
        }
    }

    internal static string ExtractStrokeKey(FlowMessage message)
    {
        var payload = message.PayloadAsString().Trim();
        if (!string.IsNullOrEmpty(payload))
            return payload;

        return DecisionParser.TryReadString(message.Properties, "key")
               ?? DecisionParser.TryReadString(message.Properties, "button")
               ?? string.Empty;
    }

    internal static string ExtractPhase(FlowMessage message)
    {
        var phase = DecisionParser.TryReadString(message.Properties, "phase");
        if (!string.IsNullOrEmpty(phase))
            return phase.ToLowerInvariant();

        var isDown = DecisionParser.TryReadBool(message.Properties, "isDown");
        if (isDown.HasValue)
            return isDown.Value ? "down" : "up";

        return "down";
    }

    private static bool PhaseMatches(string configured, string actual)
    {
        return configured switch
        {
            "" or "any" => true,
            "down" => actual is "down" or "wheel",
            "up" => actual == "up",
            _ => string.Equals(configured, actual, StringComparison.OrdinalIgnoreCase),
        };
    }
}
