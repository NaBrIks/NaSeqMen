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

    private Signal? MatchEvent(NodeDefinition node, Signal signal, Prepared prepared)
    {
        if (!MatchesSource(node, signal, sequence: node.Operation == "sequence")) return null;
        if (node.Operation == "match")
            return signal.IsRepeat && node.Settings.GetValueOrDefault("ignoreRepeat") == "true" ? null : signal;
        if (signal.Module != "windows.input" || !prepared.Sequences[node.Id].Accept(signal)) return null;
        var output = signal with
        {
            Control = node.Settings["eventName"],
            OriginalControl = null,
            Phase = "change",
            Value = "1",
            ValueType = "number"
        };
        Emit(node, output, "transformed", M("runtime.sequenceMatched", ("event", output.Control)));
        return output;
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
