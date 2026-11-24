# Stroke Action

## Purpose

**Stroke Action** is a lightweight, composable single-rule filter and action node for Interception stroke baskets. It matches one or more configured key/button names (or `*` for any stroke) and a stroke phase (`Any`, `Down`, or `Up`), sets the basket's `suppress` decision, and optionally attaches replacement keyboard chords, multi-step sequences, or mouse button actions.

Unlike **Hotkey Switch** (which manages an entire multi-rule table and shared prefix modifiers), **Stroke Action** is stateless and ideal for building custom visual pipelines together with **Window Filter**, **C# Function**, or other logic nodes.

## Typical Scenarios

- **Context-aware per-app remapping:** Chain `Interception Hub -> Window Filter -> Stroke Action -> Return / Action` so a key or mouse button (for example `X1 -> LCtrl+C`) is remapped only when a specific application window is active, while passing through unchanged everywhere else.
- **Custom C# post-processing:** Match a key or button with **Stroke Action**, then pass **`Matched`** through a **C# Function** node to dynamically modify `sendKeys`, toggle state, or trigger external integrations before returning to **`Return / Action`**.
- **Simple single-button remap or block:** Remap a single button (such as `X2` to `LCtrl+K, LCtrl+D`, or suppress `CapsLock` on `Any` phase) without needing a full rule table.

## Ports

### Inputs
- **`Event`** (`in`, Object) — Incoming Interception stroke event basket.

### Outputs
- **`Matched`** (`matched`, Object) — Emitted when both **Match Key** and **Phase** match, carrying the updated `suppress`, `sendKeys`/`sendMouse`, and optional `sendHardwareId` properties.
- **`Other`** (`unmatched`, Object) — Emitted unchanged when the stroke does not match. Wire **`Other`** either to the next rule in a chain or directly back to **`Return / Action`** so non-matching strokes resolve immediately.

## Configuration & Usage

### Matching Parameters
- **Match Key** (`matchKey`) — Canonical key or mouse button name to match (case-insensitive). Accepts a single token (`D`, `LWin`, `X1`, `WheelUp`), a comma-separated list (`X1, X2`), or `*` to match any stroke.
- **Phase** (`matchPhase`, default `Any`) — Filters which stroke phase matches:
  - **Any** — Matches down, up, and wheel strokes (recommended when using `Suppress` or `Mirror Hold` so both press and release are handled consistently).
  - **Down** — Matches key/button down and mouse wheel ticks (`wheel`).
  - **Up** — Matches key/button release only.

### Decision & Replacement Parameters
- **Suppress** (`suppress`, default `Suppress`) — Sets how the original hardware stroke is handled:
  - **Suppress** — Sets `suppress = true` so Windows does not receive the original stroke.
  - **Pass** — Sets `suppress = false` so Windows receives the original stroke (useful for pass-through logging or adding extra actions alongside the real key).
  - **Unchanged** — Leaves any existing `suppress` property on the basket untouched.
- **Send Action** (`sendAction`) — Optional replacement key chord (`LCtrl+X`), comma-separated multi-step sequence (`LCtrl+K, LCtrl+D`), or mouse button/wheel action (`Left`, `Middle`, `X1`, `WheelUp`). Leave empty to only suppress or pass.
- **Send Mode** (`sendMode`, default `Tap on Down`) — Controls when replacement strokes are generated:
  - **Tap on Down (`tapOnDown`)** — Injects a complete press-and-release when the incoming stroke phase is `down` (or `wheel`).
  - **Mirror Hold (`mirrorHold`)** — Presses and holds the replacement on incoming `down`, and releases it on incoming `up` (requires **Phase** set to `Any`).
  - **Tap on Up (`tapOnUp`)** — Injects a complete press-and-release when the incoming stroke is released (`up`).
- **Target KB Filter** (`targetKeyboardFilter`) — Optional Hardware ID substring selecting which keyboard slot injects replacement keyboard strokes (useful when remapping a mouse button to keyboard keys).

### Tips
- When using **Mirror Hold** or suppressing a physical key completely, set **Phase** to **`Any`** so both the `down` and `up` events are matched and suppressed — otherwise the unmatched `up` event will leak through **`Other`**.
- When chaining multiple **Stroke Action** nodes in series (`Other -> next Event`), always connect the final **`Other`** and every **`Matched`** back to **`Return / Action`** to avoid hitting `Decision Timeout (ms)`.
