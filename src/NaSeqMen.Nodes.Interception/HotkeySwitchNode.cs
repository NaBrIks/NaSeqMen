using System.Collections.Concurrent;
using NaSeqMen.Core.Messages;
using NaSeqMen.Core.Nodes;

namespace NaSeqMen.Nodes.Interception;

/// <summary>
/// Unified multi-rule hotkey switch for Interception loop-back flows.
/// Coordinates N chord and single-key rules in one state machine with automatic
/// suppress-and-delayed-replay for shared prefix modifiers.
/// </summary>
public sealed class HotkeySwitchNode : NodeBase
{
    public static readonly NodeTypeDescriptor TypeDescriptor = new()
    {
        TypeId = "interception.hotkey-switch",
        DisplayName = "Hotkey Switch",
        Category = "Interception",
        Color = "#b91c1c",
        WidgetKind = "hotkey-switch",
        Description = "Evaluates multiple hotkey/button remap rules together with unified modifier buffering, timeout handling, and optional delayed replay of unmatched modifiers.",
        Inputs =
        [
            new PortDefinition
            {
                Id = "in",
                Label = "Event",
                Direction = PortDirection.Input,
                DataType = PortDataType.Object,
                Description = "Incoming Interception stroke event basket.",
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
                Description = "Connect back to Interception Device/Hub [Return / Action]. Carries all decisions and delayed modifier replays.",
            },
            new PortDefinition
            {
                Id = "matched",
                Label = "Matched",
                Direction = PortDirection.Output,
                DataType = PortDataType.Object,
                Description = "Optional tap emitted only when a rule matches (connect to Debug, OBS, etc.).",
            },
        ],
        Parameters =
        [
            new ParameterDefinition
            {
                Key = "rules",
                Label = "Hotkey Rules",
                Kind = ParameterKind.RuleList,
                DefaultValue = Array.Empty<HotkeyRuleDto>(),
                Inline = false,
                Description = "List of From -> To hotkey or mouse button remaps evaluated together.",
            },
            new ParameterDefinition
            {
                Key = "replayUnmatchedModifier",
                Label = "Replay Unmatched Mod",
                Kind = ParameterKind.Boolean,
                DefaultValue = true,
                Inline = true,
                Description = "If a prefix modifier (e.g. LWin) is pressed and no rule matches, replay it to Windows so normal shortcuts and bare taps still work.",
            },
            new ParameterDefinition
            {
                Key = "chordWindowMs",
                Label = "Replay Window (ms)",
                Kind = ParameterKind.Number,
                DefaultValue = 400,
                Inline = false,
                Description = "Auto-replay a held prefix modifier after this timeout if no trigger arrives (0 = wait until another key or release).",
            },
            new ParameterDefinition
            {
                Key = "targetKeyboardFilter",
                Label = "Target KB Filter",
                Kind = ParameterKind.String,
                DefaultValue = "",
                Placeholder = "Optional target KB HWID",
                Inline = false,
                Description = "Optional Hardware ID substring for injecting keyboard actions triggered from a mouse.",
            },
        ],
    };

    private sealed class ModifierEntry
    {
        public required string KeyName { get; init; }
        public required ushort ScanCode { get; init; }
        public required bool IsE0 { get; init; }
        public required int DeviceSlot { get; init; }
        public bool IsPending { get; set; }
        public bool WasConsumed { get; set; }
        public bool WasReplayed { get; set; }
        public CancellationTokenSource? TimerCts { get; set; }
    }

    private sealed class DeviceState
    {
        public object Sync { get; } = new();
        public Dictionary<string, ModifierEntry> HeldModifiers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, CompiledHotkeyRule> HeldTriggers { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly ConcurrentDictionary<int, DeviceState> _deviceStates = new();
    private IReadOnlyList<CompiledHotkeyRule> _rules = Array.Empty<CompiledHotkeyRule>();
    private HashSet<string> _watchedModifiers = new(StringComparer.OrdinalIgnoreCase);
    private bool _replayUnmatched = true;
    private int _chordWindowMs = 400;
    private string _targetKeyboardFilter = string.Empty;
    private string? _lastMatch;

    public override NodeTypeDescriptor Descriptor => TypeDescriptor;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        ClearAllStates();
        _rules = HotkeyRuleParser.ParseRules(Context.Parameters);
        _watchedModifiers = _rules
            .SelectMany(r => r.Modifiers)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _replayUnmatched = GetBool("replayUnmatchedModifier", true);
        _chordWindowMs = (int)Math.Clamp(GetNumber("chordWindowMs", 400), 0, 5000);
        _targetKeyboardFilter = GetString("targetKeyboardFilter", "").Trim();
        _lastMatch = null;

        PublishSwitchWidget();
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        ClearAllStates();
        return Task.CompletedTask;
    }

    public override async Task OnInputAsync(string inputPortId, FlowMessage message, CancellationToken cancellationToken)
    {
        if (_rules.Count == 0)
        {
            await EmitAsync("return", message.Clone(), cancellationToken);
            return;
        }

        var rawKey = StrokeActionNode.ExtractStrokeKey(message);
        var key = HotkeyRuleParser.CanonicalizeToken(rawKey);
        var phase = StrokeActionNode.ExtractPhase(message);
        var slot = DecisionParser.TryReadInt(message.Properties, "deviceSlot") ?? 0;
        var state = _deviceStates.GetOrAdd(slot, static _ => new DeviceState());

        // 1. Handle watched prefix modifier key (e.g. LWin shared by LWin+D, LWin+E, ...)
        if (_watchedModifiers.Contains(key))
        {
            await HandleWatchedModifierAsync(state, slot, key, phase, message, cancellationToken);
            return;
        }

        // 2. Handle trigger / regular key down
        if (phase == "down")
        {
            await HandleKeyDownAsync(state, key, message, cancellationToken);
            return;
        }

        // 3. Handle trigger / regular key up
        if (phase == "up")
        {
            await HandleKeyUpAsync(state, key, message, cancellationToken);
            return;
        }

        await EmitAsync("return", message.Clone(), cancellationToken);
    }

    private async Task HandleWatchedModifierAsync(
        DeviceState state,
        int slot,
        string key,
        string phase,
        FlowMessage message,
        CancellationToken ct)
    {
        var next = message.Clone();

        if (phase == "down")
        {
            var shouldStartTimer = false;
            ModifierEntry? entry = null;

            lock (state.Sync)
            {
                if (state.HeldModifiers.TryGetValue(key, out var existing))
                {
                    // Auto-repeat while key is held: keep same decision (suppress if pending/consumed, pass if replayed)
                    if (existing.IsPending || existing.WasConsumed || !_replayUnmatched)
                        next.Properties["suppress"] = true;
                }
                else
                {
                    ScanCodeMap.TryGetScanCode(key, out var sc, out var isE0);
                    entry = new ModifierEntry
                    {
                        KeyName = key,
                        ScanCode = sc,
                        IsE0 = isE0,
                        DeviceSlot = slot,
                        IsPending = true,
                        WasConsumed = false,
                        WasReplayed = false,
                    };
                    state.HeldModifiers[key] = entry;
                    next.Properties["suppress"] = true;
                    shouldStartTimer = _replayUnmatched && _chordWindowMs > 0;
                }
            }

            if (shouldStartTimer && entry is not null)
                StartReplayTimer(state, entry);

            await EmitAsync("return", next, ct);
            return;
        }

        if (phase == "up")
        {
            List<KeyActionStep>? tapReplay = null;
            bool suppressUp = false;

            lock (state.Sync)
            {
                if (state.HeldModifiers.Remove(key, out var entry))
                {
                    CancelTimerLocked(entry);

                    if (entry.WasConsumed)
                    {
                        suppressUp = true;
                    }
                    else if (entry.WasReplayed)
                    {
                        suppressUp = false; // Pass real key-up through so OS releases it
                    }
                    else if (entry.IsPending)
                    {
                        // Bare tap (down then up with no other key)
                        suppressUp = true;
                        if (_replayUnmatched && entry.ScanCode != 0)
                        {
                            tapReplay =
                            [
                                new KeyActionStep(entry.ScanCode, entry.IsE0, true),
                                new KeyActionStep(entry.ScanCode, entry.IsE0, false),
                            ];
                        }
                    }
                }
            }

            next.Properties["suppress"] = suppressUp;
            if (tapReplay is not null)
                next.Properties["sendKeys"] = tapReplay;

            await EmitAsync("return", next, ct);
            return;
        }

        await EmitAsync("return", next, ct);
    }

    private async Task HandleKeyDownAsync(DeviceState state, string key, FlowMessage message, CancellationToken ct)
    {
        var next = message.Clone();
        CompiledHotkeyRule? matchedRule = null;
        List<KeyActionStep>? replaySteps = null;

        lock (state.Sync)
        {
            matchedRule = FindMatchingRuleLocked(state, key);

            if (matchedRule is not null)
            {
                // Consume all modifiers belonging to the matched rule
                foreach (var modName in matchedRule.Modifiers)
                {
                    if (state.HeldModifiers.TryGetValue(modName, out var modEntry))
                    {
                        CancelTimerLocked(modEntry);
                        modEntry.IsPending = false;
                        modEntry.WasConsumed = true;
                    }
                }

                state.HeldTriggers[key] = matchedRule;
            }
            else if (_replayUnmatched)
            {
                // No rule matched: if any prefix modifier is still pending, atomically replay [mod down..., this key down]
                var pendingMods = state.HeldModifiers.Values.Where(m => m.IsPending && m.ScanCode != 0).ToList();
                if (pendingMods.Count > 0 && ScanCodeMap.TryGetScanCode(key, out var keySc, out var keyE0))
                {
                    replaySteps = [];
                    foreach (var pm in pendingMods)
                    {
                        CancelTimerLocked(pm);
                        pm.IsPending = false;
                        pm.WasReplayed = true;
                        replaySteps.Add(new KeyActionStep(pm.ScanCode, pm.IsE0, true));
                    }
                    replaySteps.Add(new KeyActionStep(keySc, keyE0, true));
                }
            }
        }

        if (matchedRule is not null)
        {
            next.Properties["suppress"] = true;
            next.Properties["matchedRule"] = matchedRule.From;

            if (!string.Equals(matchedRule.Mode, "suppressOnly", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(matchedRule.To))
            {
                StrokeActionNode.AttachSendAction(next, matchedRule.To, matchedRule.Mode, "down");
                if (!string.IsNullOrEmpty(_targetKeyboardFilter))
                    next.Properties["sendHardwareId"] = _targetKeyboardFilter;
            }

            _lastMatch = string.IsNullOrEmpty(matchedRule.To)
                ? $"{matchedRule.From} (block)"
                : $"{matchedRule.From} → {matchedRule.To}";
            PublishSwitchWidget();

            await EmitAsync("return", next, ct);
            await EmitAsync("matched", next.Clone(), ct);
            return;
        }

        if (replaySteps is not null)
        {
            next.Properties["suppress"] = true;
            next.Properties["sendKeys"] = replaySteps;
            next.Properties["replayedModifier"] = true;
        }
        else
        {
            next.Properties["suppress"] = false;
        }

        await EmitAsync("return", next, ct);
    }

    private async Task HandleKeyUpAsync(DeviceState state, string key, FlowMessage message, CancellationToken ct)
    {
        var next = message.Clone();
        CompiledHotkeyRule? heldRule;

        lock (state.Sync)
        {
            state.HeldTriggers.Remove(key, out heldRule);
        }

        if (heldRule is not null)
        {
            next.Properties["suppress"] = true;
            next.Properties["matchedRule"] = heldRule.From;

            if (string.Equals(heldRule.Mode, "mirrorHold", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(heldRule.To))
            {
                StrokeActionNode.AttachSendAction(next, heldRule.To, "mirrorHold", "up");
                if (!string.IsNullOrEmpty(_targetKeyboardFilter))
                    next.Properties["sendHardwareId"] = _targetKeyboardFilter;
            }
        }
        else
        {
            next.Properties["suppress"] = false;
        }

        await EmitAsync("return", next, ct);
    }

    private CompiledHotkeyRule? FindMatchingRuleLocked(DeviceState state, string triggerKey)
    {
        foreach (var rule in _rules)
        {
            if (!ScanCodeMap.KeyMatches(rule.TriggerKey, triggerKey))
                continue;

            var allModsHeld = true;
            foreach (var mod in rule.Modifiers)
            {
                if (!state.HeldModifiers.TryGetValue(mod, out var entry) || entry.WasReplayed)
                {
                    allModsHeld = false;
                    break;
                }
            }

            if (allModsHeld)
                return rule;
        }

        return null;
    }

    private void StartReplayTimer(DeviceState state, ModifierEntry entry)
    {
        var cts = new CancellationTokenSource();
        entry.TimerCts = cts;
        var delayMs = _chordWindowMs;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            KeyActionStep? stepToReplay = null;
            lock (state.Sync)
            {
                if (!entry.IsPending || entry.WasConsumed || entry.WasReplayed)
                    return;

                entry.IsPending = false;
                entry.WasReplayed = true;
                entry.TimerCts = null;
                if (entry.ScanCode != 0)
                    stepToReplay = new KeyActionStep(entry.ScanCode, entry.IsE0, true);
            }

            if (stepToReplay is not null)
            {
                var replayMsg = new FlowMessage
                {
                    Topic = "interception.replay",
                    Payload = entry.KeyName,
                    OriginNodeId = Context.NodeId,
                    Properties =
                    {
                        ["replayedModifier"] = true,
                        ["sendDeviceSlot"] = entry.DeviceSlot,
                        ["sendKeys"] = new List<KeyActionStep> { stepToReplay },
                    },
                };

                try
                {
                    await EmitAsync("return", replayMsg).ConfigureAwait(false);
                }
                catch
                {
                    // Engine may be stopping
                }
            }
        });
    }

    private static void CancelTimerLocked(ModifierEntry entry)
    {
        if (entry.TimerCts is null)
            return;

        try { entry.TimerCts.Cancel(); } catch { }
        try { entry.TimerCts.Dispose(); } catch { }
        entry.TimerCts = null;
    }

    private void ClearAllStates()
    {
        foreach (var state in _deviceStates.Values)
        {
            lock (state.Sync)
            {
                foreach (var entry in state.HeldModifiers.Values)
                    CancelTimerLocked(entry);
                state.HeldModifiers.Clear();
                state.HeldTriggers.Clear();
            }
        }
        _deviceStates.Clear();
    }

    private void PublishSwitchWidget()
    {
        Context.ReportStatus(_lastMatch ?? $"{_rules.Count} rules");
        PublishWidgetState(new
        {
            ruleCount = _rules.Count,
            replayEnabled = _replayUnmatched,
            lastMatch = _lastMatch,
            preview = _rules.Take(4).Select(r => new { from = r.From, to = r.To, mode = r.Mode }).ToArray(),
        });
    }

    public override ValueTask DisposeAsync()
    {
        ClearAllStates();
        return ValueTask.CompletedTask;
    }
}
