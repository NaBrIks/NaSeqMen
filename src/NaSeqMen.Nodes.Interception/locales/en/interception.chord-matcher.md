# Chord Matcher

## Purpose

**Chord Matcher** detects a single **Modifier Key + Trigger Key** chord within an optional time window using in-memory per-device held state. When the configured **Trigger Key** is pressed while **Modifier Key** is currently held (within **Chord Window (ms)**), the node emits the matched stroke on **`Matched`** with optional suppression and replacement actions attached.

All other strokes — including modifier press/release, trigger release, and unrelated keys — are forwarded on **`Other`** so the loop-back finishes immediately without waiting for timeout.

## Typical Scenarios

- **Dedicated single chord trigger:** Detect one specific combination (for example `LWin+D` -> `LCtrl+X` or `LCtrl+F12` -> trigger an OBS scene switch) on a dedicated device or inside a branch filtered by **Window Filter**.
- **Custom branch routing:** Route **`Matched`** through custom logic (**C# Function**, **OBS Studio**, **Debug**) while wiring **`Other`** directly back to **`Return / Action`**.

### When to Prefer `Hotkey Switch` Instead
**Chord Matcher** is designed for a single modifier + trigger pair and **does not buffer or replay** the modifier key if another key is pressed.
- If **Suppress Modifier** is enabled, the modifier key is always suppressed on that device — even for other shortcuts or bare taps.
- Whenever you have **two or more rules sharing the same modifier** (for example `LWin+D` and `LWin+E`), or whenever you still want normal OS shortcuts and bare taps of that modifier to work via delayed replay, use **[Hotkey Switch](interception.hotkey-switch)** instead.

## Ports

### Inputs
- **`Event`** (`in`, Object) — Incoming Interception stroke event basket.

### Outputs
- **`Matched`** (`matched`, Object) — Emitted when **Trigger Key** is pressed while **Modifier Key** is held within **Chord Window (ms)**. Carries `chordMatched`, optional `suppress`, and optional replacement `sendKeys` / `sendMouse`.
- **`Other`** (`other`, Object) — Emitted for modifier down/up events, matching trigger release (carrying `suppress = true` when **Suppress Trigger** is enabled), and all non-matching strokes.

## Configuration & Usage

### Loop-Back Wiring
Wire **both** **`Matched`** and **`Other`** back to the source node's **`Return / Action`** port:

```text
                        ┌── Matched ──► (optional actions) ──┐
[Interception: out] ──► [Chord Matcher]                       ├──► [Interception: Return / Action]
                        └── Other ───────────────────────────┘
```

### Parameters
- **Modifier Key** (`modifierKey`, default `LWin`) — Key that must be pressed and held first (for example `LWin`, `LCtrl`, `LAlt`, `RAlt`).
- **Trigger Key** (`triggerKey`) — Second key pressed while **Modifier Key** is held to trigger the chord (for example `D`).
- **Suppress Modifier** (`suppressModifier`, default `true`) — Suppresses the original modifier key down and up strokes.
- **Suppress Trigger** (`suppressTrigger`, default `true`) — Suppresses the trigger key down and its paired release event when the chord matches.
- **Send Action** (`sendAction`) — Optional replacement key chord (`LCtrl+X`), sequence (`LCtrl+K, LCtrl+D`), or mouse button action injected as a tap when the chord matches.
- **Chord Window (ms)** (`chordWindowMs`, default `400`) — Maximum elapsed time in milliseconds between pressing **Modifier Key** and pressing **Trigger Key**. Set to `0` to allow the chord at any time while **Modifier Key** remains held.

### Tips
- Held modifier state is tracked strictly in memory per device slot and cleared automatically when the flow stops.
- If you do not want to lose normal use of a system modifier like `LWin` or `LCtrl`, prefer **Hotkey Switch** with **Replay Unmatched Mod** enabled.
