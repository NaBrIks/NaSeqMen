// SPDX-License-Identifier: MIT
using NaSeqMen.Contracts;
using static NaSeqMen.Contracts.MessageCatalog;

namespace NaSeqMen.Runtime;

public sealed partial class GraphRuntime
{
    private static bool ValidEvent(NodeDefinition node, Configuration config)
    {
        if (node.Operation == "match")
            return node.Settings.GetValueOrDefault("phase", "") is "" or "down" or "up" or "change";
        if (node.Operation == "default") return true;
        if (node.Operation == "chord")
            return node.Settings.GetValueOrDefault("source", "").Length > 0 &&
                   node.Settings.GetValueOrDefault("control", "").StartsWith("key:", StringComparison.Ordinal) &&
                   ChordModifiers(node) != ModifierKeys.None;
        return node.Operation == "sequence" &&
               config.Connections.Any(c => c.Id == node.ConnectionId && c.Module == "windows.input") &&
               !string.IsNullOrWhiteSpace(node.Settings.GetValueOrDefault("source")) &&
               !string.IsNullOrWhiteSpace(node.Settings.GetValueOrDefault("eventName")) &&
               EventSequence.Parse(node.Settings) is not null;
    }

    private static ModifierKeys Modifier(string? name) => name switch
    {
        "Shift" => ModifierKeys.Shift, "Ctrl" => ModifierKeys.Ctrl, "Alt" => ModifierKeys.Alt, "Win" => ModifierKeys.Win,
        "LeftShift" => ModifierKeys.LeftShift, "RightShift" => ModifierKeys.RightShift,
        "LeftCtrl" => ModifierKeys.LeftCtrl, "RightCtrl" => ModifierKeys.RightCtrl,
        "LeftAlt" => ModifierKeys.LeftAlt, "RightAlt" => ModifierKeys.RightAlt,
        "LeftWin" => ModifierKeys.LeftWin, "RightWin" => ModifierKeys.RightWin,
        _ => ModifierKeys.None
    };

    private static bool ValidBuiltInCondition(NodeDefinition node) => node.Operation switch
    {
        "equals" => Fields.Contains(node.Settings.GetValueOrDefault("field", "value")),
        "modifierHeld" => Modifier(node.Settings.GetValueOrDefault("modifier")) != ModifierKeys.None &&
                          node.Settings.GetValueOrDefault("scope") is "source" or "global",
        _ => false
    };

    private static bool EvaluateBuiltInCondition(NodeDefinition node, Signal signal)
    {
        if (node.Operation == "equals")
            return Field(signal, node.Settings.GetValueOrDefault("field", "value")) ==
                   node.Settings.GetValueOrDefault("value", "");
        if (signal.Modifiers is not { } state) return false;
        var held = node.Settings["scope"] == "source" ? state.SourceHeld : state.GlobalHeld;
        return (held & Modifier(node.Settings["modifier"])) != 0;
    }

    private static readonly ModifierKeys[] ChordGroups = [ModifierKeys.Ctrl, ModifierKeys.Alt, ModifierKeys.Shift, ModifierKeys.Win];

    private static ModifierKeys ChordModifiers(NodeDefinition node)
    {
        var required = ModifierKeys.None;
        foreach (var name in node.Settings.GetValueOrDefault("modifiers", "").Split('+', StringSplitOptions.TrimEntries))
        {
            var group = Modifier(name);
            if (!ChordGroups.Contains(group) || (required & group) != 0) return ModifierKeys.None;
            required |= group;
        }
        return required;
    }

    private EventOutcome MatchEvent(NodeDefinition node, Signal signal, Prepared prepared, out Signal output)
    {
        output = signal;
        if (node.Operation == "default") return EventOutcome.Match;
        if (!MatchesSource(node, signal, sequence: node.Operation is "sequence" or "chord")) return EventOutcome.Miss;
        if (node.Operation == "match" && signal.IsRepeat && node.Settings.GetValueOrDefault("ignoreRepeat") == "true")
            return EventOutcome.Miss;
        if (node.Operation == "match") return EventOutcome.Match;
        if (signal.Module != "windows.input") return EventOutcome.Miss;
        if (node.Operation == "chord")
        {
            var held = signal.Modifiers?.SourceHeld ?? ModifierKeys.None;
            var required = ChordModifiers(node);
            return signal.Phase == "down" && !signal.IsRepeat &&
                (signal.OriginalControl ?? signal.Control) == node.Settings["control"] &&
                ChordGroups.All(group => ((held & group) != 0) == ((required & group) != 0))
                ? EventOutcome.Match : EventOutcome.Miss;
        }
        var outcome = prepared.Sequences[node.Id].Accept(signal);
        if (outcome != EventOutcome.Match) return outcome;
        output = signal with
        {
            Control = node.Settings["eventName"],
            OriginalControl = null,
            Phase = "change",
            Value = "1",
            ValueType = "number"
        };
        return EventOutcome.Match;
    }

    private static void ResetDisconnectedSequences(Prepared prepared, Signal signal)
    {
        if (signal.Module != "windows.input" || signal.Control != "device:connection") return;
        foreach (var (id, sequence) in prepared.Sequences)
        {
            var node = prepared.Nodes[id];
            if (node.ConnectionId == signal.ConnectionId &&
                node.Settings.GetValueOrDefault("source") == signal.Source) sequence.Reset();
        }
    }
}
