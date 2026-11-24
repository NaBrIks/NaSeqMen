# Hotkey Switch

## Purpose

**Hotkey Switch** is a unified multi-rule state machine for Interception loop-back flows. It evaluates an entire table of single-key, chord, and mouse button remap rules together on a per-device basis, handling shared prefix modifiers cleanly without conflicts.

Whenever multiple rules share a prefix modifier (such as `LWin+D`, `LWin+E`, and `LWin+F`), **Hotkey Switch** buffers that modifier once, tests every matching rule in priority order (most-specific modifier combinations first), and automatically replays the modifier to Windows if no rule matches.

## Typical Scenarios

- **Shared-modifier shortcut layer:** Remap `LWin+D -> LCtrl+X` and `LWin+E -> LCtrl+V` while keeping all other `LWin` shortcuts (`LWin+L`, `LWin+R`, bare `LWin` tap) working normally via **Replay Unmatched Mod**.
- **Mouse side-button productivity shortcuts:** Remap `X1 -> LCtrl+C`, `X2 -> LCtrl+V`, or `WheelUp` / `WheelDown` to keyboard chords or multi-step IDE commands such as `LCtrl+K, LCtrl+D`.
- **Blocking unwanted keys:** Silence accidental keys or buttons (such as `CapsLock` or `Insert`) using **Block** mode while optionally triggering side effects (OBS, Debug, scripts) from the **`Matched`** output.

## Ports

### Inputs
- **`Event`** (`in`, Object) — Incoming Interception stroke basket from **Interception Hub** or **Interception Device**.

### Outputs
- **`Return`** (`return`, Object) — Connect directly back to the source node's **`Return / Action`** input. Carries every stroke's `suppress` decision, replacement actions, and delayed modifier replays.
- **`Matched`** (`matched`, Object) — Optional side-tap emitted only when a rule fires. Wire this to **Debug**, **OBS**, or other non-blocking downstream nodes without affecting loop-back latency.

## Configuration & Usage

### Hotkey Rules (`rules`)
Each enabled rule defines **`From`** (incoming key, chord, or mouse button), **`To`** (replacement action), and **`Mode`**:

- **Tap (`tapOnDown`)** — Suppresses the matched key down and its matching key up, and injects a complete press-and-release of **`To`** immediately on key down (or mouse wheel tick). Ideal for shortcuts (`LWin+D -> LCtrl+X`, `X1 -> LCtrl+C`) and multi-step sequences.
- **Hold (`mirrorHold`)** — Mirrors physical hold duration: on key/button down, runs any preceding sequence steps and holds the final chord/button down; on key/button up, releases it. Ideal for push-to-talk, remapping a mouse button to a modifier or another mouse button, or holding a key in games/editors.
- **Block (`suppressOnly`)** — Suppresses the matched key down and key up completely without injecting any replacement keys. Useful for disabling keys or consuming a trigger purely for the **`Matched`** output.

### Supported Syntax Examples
- **Chords:** `LWin+D`, `LCtrl+LShift+S`, `LAlt+F4`
- **Multi-step sequences (comma-separated in `To`):** `LCtrl+K, LCtrl+D` (sends `LCtrl+K` down/up, then `LCtrl+D` down/up)
- **Mouse buttons & wheel:** `Left`, `Right`, `Middle`, `X1`, `X2`, `WheelUp`, `WheelDown`, `WheelLeft`, `WheelRight` (usable in both **`From`** and **`To`**)

### Modifier Buffering & Replay
- **Replay Unmatched Mod** (`replayUnmatchedModifier`, default `true`) — When a key used as a prefix modifier in any rule (for example `LWin`) is pressed, **Hotkey Switch** initially suppresses and buffers it so Windows does not immediately react.
  - If a matching trigger arrives (e.g. `D` for `LWin+D`), the modifier is consumed and never leaked to Windows.
  - If a non-matching key arrives (e.g. `R`), **Hotkey Switch** atomically replays `[LWin down, R down]` in order, and passes the real releases through so standard OS shortcuts still work.
  - If the modifier is tapped and released alone, a clean `[down, up]` tap is replayed.
- **Replay Window (ms)** (`chordWindowMs`, default `400`) — If a buffered prefix modifier is held longer than this timeout without a trigger, it is automatically replayed as held down (useful for hold-only modifier behaviors). Set to `0` to wait indefinitely until another key is pressed or the modifier is released.
- **Target KB Filter** (`targetKeyboardFilter`) — Optional Hardware ID substring selecting which keyboard slot injects keyboard actions when a rule is triggered from a **mouse** device (leave empty to auto-select an available keyboard slot).

### Tips
- Rules with more modifiers are automatically evaluated before less-specific rules, so `LCtrl+LShift+D` will match before `LCtrl+D` regardless of row order.
- Always wire **`Return`** back to **`Return / Action`**; do **not** wire **`Matched`** back into **`Return / Action`**, or decisions will be submitted twice.
