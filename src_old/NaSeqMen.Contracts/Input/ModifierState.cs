// SPDX-License-Identifier: MIT
namespace NaSeqMen.Contracts;

[Flags]
public enum ModifierKeys
{
    None = 0,
    LeftShift = 1, RightShift = 2,
    LeftCtrl = 4, RightCtrl = 8,
    LeftAlt = 16, RightAlt = 32,
    LeftWin = 64, RightWin = 128,
    Shift = LeftShift | RightShift,
    Ctrl = LeftCtrl | RightCtrl,
    Alt = LeftAlt | RightAlt,
    Win = LeftWin | RightWin
}

/// <summary>Observed transitions through the current event. Unobserved keys have unknown state.</summary>
public sealed record ModifierState(ModifierKeys SourceHeld, ModifierKeys SourceKnown, ModifierKeys GlobalHeld);
