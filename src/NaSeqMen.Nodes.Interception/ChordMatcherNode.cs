using System.Collections.Concurrent;
using NaSeqMen.Core.Messages;
using NaSeqMen.Core.Nodes;

namespace NaSeqMen.Nodes.Interception;

/// <summary>
/// Universal two-key chord/modifier matcher with configurable window and suppression flags.
/// Maintains in-memory held state per device slot and emits matched chords on "matched"
/// and all other strokes (including suppressed modifier/release decisions) on "other".
/// </summary>
public sealed class ChordMatcherNode : NodeBase
{
    public static readonly NodeTypeDescriptor TypeDescriptor = new()
    {
        TypeId = "interception.chord-matcher",
        DisplayName = "Chord Matcher",
        Category = "Interception",
        Color = "#c2410c",
        Description = "Detects any configured modifier + trigger key chord within a time window using in-memory held state. Wire both Matched and Other back to Return / Action for immediate loop-back decisions.",
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
                Description = "Emitted when Trigger Key is pressed while Modifier Key is held within Chord Window.",
            },
            new PortDefinition
            {
                Id = "other",
                Label = "Other",
                Direction = PortDirection.Output,
                DataType = PortDataType.Object,
                Description = "Emitted for modifier strokes, trigger release, and non-matching keys (carrying suppress flags when enabled).",
            },
        ],
        Parameters =
        [
            new ParameterDefinition
            {
                Key = "modifierKey",
                Label = "Modifier Key",
                Kind = ParameterKind.Key,
                DefaultValue = "LWin",
                Placeholder = "Modifier (e.g. LWin, LCtrl)",
                Inline = true,
            },
            new ParameterDefinition
            {
                Key = "triggerKey",
                Label = "Trigger Key",
                Kind = ParameterKind.Key,
                DefaultValue = "",
                Placeholder = "Trigger key (e.g. D)",
                Inline = true,
            },
            new ParameterDefinition
            {
                Key = "suppressModifier",
                Label = "Suppress Modifier",
                Kind = ParameterKind.Boolean,
                DefaultValue = true,
                Inline = true,
            },
            new ParameterDefinition
            {
                Key = "suppressTrigger",
                Label = "Suppress Trigger",
                Kind = ParameterKind.Boolean,
                DefaultValue = true,
                Inline = true,
            },
            new ParameterDefinition
            {
                Key = "sendAction",
                Label = "Send Action",
                Kind = ParameterKind.KeyCombo,
                DefaultValue = "",
                Placeholder = "Replacement e.g. LCtrl+X",
                Inline = true,
            },
            new ParameterDefinition
            {
                Key = "chordWindowMs",
                Label = "Chord Window (ms)",
                Kind = ParameterKind.Number,
                DefaultValue = 400,
                Inline = false,
                Description = "Maximum time between modifier press and trigger press (0 = unlimited while held).",
            },
        ],
    };

    private sealed class DeviceChordState
    {
        public DateTimeOffset? ModifierDownAtUtc;
        public bool TriggerHeldFromChord;
    }

    private readonly ConcurrentDictionary<int, DeviceChordState> _states = new();

    public override NodeTypeDescriptor Descriptor => TypeDescriptor;

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _states.Clear();
        return Task.CompletedTask;
    }

    public override async Task OnInputAsync(string inputPortId, FlowMessage message, CancellationToken cancellationToken)
    {
        var modifierKey = GetString("modifierKey", "LWin").Trim();
        var triggerKey = GetString("triggerKey", "").Trim();
        var suppressModifier = GetBool("suppressModifier", true);
        var suppressTrigger = GetBool("suppressTrigger", true);
        var sendAction = GetString("sendAction", "").Trim();
        var chordWindowMs = GetNumber("chordWindowMs", 400);

        if (string.IsNullOrEmpty(modifierKey) || string.IsNullOrEmpty(triggerKey))
        {
            await EmitAsync("other", message.Clone(), cancellationToken);
            return;
        }

        var actualKey = StrokeActionNode.ExtractStrokeKey(message);
        var phase = StrokeActionNode.ExtractPhase(message);
        var deviceSlot = DecisionParser.TryReadInt(message.Properties, "deviceSlot") ?? 0;
        var state = _states.GetOrAdd(deviceSlot, static _ => new DeviceChordState());
        var now = DateTimeOffset.UtcNow;

        if (ScanCodeMap.KeyMatches(modifierKey, actualKey))
        {
            var next = message.Clone();
            lock (state)
            {
                if (phase == "down")
                    state.ModifierDownAtUtc ??= now;
                else if (phase == "up")
                    state.ModifierDownAtUtc = null;
            }

            if (suppressModifier)
                next.Properties["suppress"] = true;

            await EmitAsync("other", next, cancellationToken);
            return;
        }

        if (ScanCodeMap.KeyMatches(triggerKey, actualKey))
        {
            var next = message.Clone();

            if (phase == "down")
            {
                var matched = false;
                lock (state)
                {
                    if (state.ModifierDownAtUtc.HasValue)
                    {
                        var elapsedMs = (now - state.ModifierDownAtUtc.Value).TotalMilliseconds;
                        if (chordWindowMs <= 0 || elapsedMs <= chordWindowMs)
                        {
                            matched = true;
                            state.TriggerHeldFromChord = true;
                        }
                    }
                }

                if (matched)
                {
                    if (suppressTrigger)
                        next.Properties["suppress"] = true;

                    next.Properties["chordMatched"] = $"{modifierKey}+{triggerKey}";
                    if (!string.IsNullOrEmpty(sendAction))
                        StrokeActionNode.AttachSendAction(next, sendAction, "tapOnDown", "down");

                    Context.ReportStatus($"chord: {modifierKey}+{triggerKey}");
                    await EmitAsync("matched", next, cancellationToken);
                    return;
                }
            }
            else if (phase == "up")
            {
                var wasChordTrigger = false;
                lock (state)
                {
                    if (state.TriggerHeldFromChord)
                    {
                        wasChordTrigger = true;
                        state.TriggerHeldFromChord = false;
                    }
                }

                if (wasChordTrigger && suppressTrigger)
                    next.Properties["suppress"] = true;

                await EmitAsync("other", next, cancellationToken);
                return;
            }
        }

        await EmitAsync("other", message.Clone(), cancellationToken);
    }

    public override ValueTask DisposeAsync()
    {
        _states.Clear();
        return ValueTask.CompletedTask;
    }
}
