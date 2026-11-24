# Virtual Button

## Purpose

**Virtual Button** renders an interactive push-button or latching toggle directly on the flow canvas. It lets you trigger actions manually, build soft control panels for live testing or desktop control, and reflect external on/off state via its loop-safe `set` feedback input.

## Typical Scenarios

- **OBS mute toggle with live LED sync:** Set *Mode* to `Toggle`, *On / Press Payload* to `true`, and *Off Payload* to `false`. Wire `press` -> **OBS Studio** (`mute`) and loop **OBS Studio** (`muteChanged`) back into `set` so the button always matches OBS even if muted inside OBS itself.
- **One-click scene switcher:** Use *Mode* = `Momentary`, set *Button Text* to `Intro Scene` and *On / Press Payload* to `Intro`, and connect `press` to **OBS Studio** (`scene`).
- **Push-to-talk / hold action:** Use *Mode* = `Momentary`, set *On / Press Payload* to `false` (unmute) and *Off Payload* to `true` (mute), and wire both `press` and `release` into **OBS Studio** (`mute`).
- **Triggering scripts or text macros:** Wire `press` to a **C# Function** or **Keyboard Output** node to test or fire multi-step automation on demand.

## Ports

### Inputs

- **`set` (Set State, `Boolean`)** — Updates the visual ON/OFF state of the button without emitting any output message (prevents feedback loops). Accepts booleans, numbers (`0` = OFF, non-zero = ON), and strings (`"true"`, `"false"`, `"1"`, `"0"`, `"on"`, `"off"`).

### Outputs

- **`press` (Press, `Any`)** — Emits on button press in *Momentary* mode (`payload`), or on every click in *Toggle* mode (alternating between `payload` when ON and `offPayload` when OFF). Adds `properties`: `mode`, `state`, and `action`.
- **`release` (Release, `Any`)** — Emits `offPayload` when the button is released in *Momentary* mode (`properties.action = "release"`, `properties.state = false`). Not emitted in *Toggle* mode.

## Configuration & Tips

- **Button Text (`buttonLabel`, default: `"Press"`):** Label rendered on the canvas button.
- **Mode (`mode`, default: `"momentary"`):**
  - `momentary` — Active only while held; emits on `press` and `release`.
  - `toggle` — Flips state on each click; emits both ON and OFF transitions on `press`.
- **On / Press Payload (`payload`, default: `"toggle"`):** Automatically parsed into a typed value: `"true"`/`"false"` become `bool`, numeric strings become numbers, and any other text stays a `string`.
- **Off Payload (`offPayload`, default: `"false"`):** Parsed with the same typed rules as `payload`.
- **Topic (`topic`, default: `"control.button"`):** Topic placed on every emitted message basket.
- **Initial state:** On flow start, the button resets to OFF until clicked or updated via `set`.
