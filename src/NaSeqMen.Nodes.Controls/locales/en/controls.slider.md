# Virtual Slider

## Purpose

**Virtual Slider** provides an interactive numeric fader right on the flow canvas. Dragging the slider emits numeric messages within your configured `[min, max]` range, while its `set` input accepts external numeric feedback so the slider stays synchronized with remote targets without creating feedback loops.

## Typical Scenarios

- **Two-way OBS volume fader:** Keep *Min* = `0`, *Max* = `100`, *Unit* = `%`. Wire `value` -> **OBS Studio** (`volume`) and connect **OBS Studio** (`volumeChanged`) back to `set`. Moving the fader adjusts OBS, and dragging the mixer inside OBS updates the canvas slider.
- **Decibel gain control:** Set *Min* = `-60`, *Max* = `0`, *Step* = `0.5`, *Initial* = `-12`, *Unit* = ` dB`, and pair it with an **OBS Studio** node configured with *Volume Scale* = `-60..0 dB`.
- **Interactive script parameter tuning:** Connect `value` to a **C# Function** node (storing the value in `state` or scaling outgoing messages) to tune thresholds, delays, or multipliers live while a flow runs.
- **Testing a VU Meter:** Wire `value` directly into a **VU Meter** (`in`) to verify scaling, range bounds, and peak-hold behavior without needing a live audio source.

## Ports

### Inputs

- **`set` (Set Value, `Number`)** — Accepts any numeric payload (or numeric string), clamps it to `[min, max]`, and updates the slider widget and status badge **without** emitting on `value`.

### Outputs

- **`value` (Value, `Number`)** — Emits the clamped numeric value each time the user moves the slider. Includes `topic` (from parameter) and `properties`: `min`, `max`, and `unit`.

## Configuration & Tips

- **Min (`min`, default: `0`) & Max (`max`, default: `100`):** Defines the slider bounds. If `min > max`, the node automatically swaps them so the range stays valid.
- **Step (`step`, default: `1`):** Granularity of slider movement. Non-positive or invalid values fall back to `1`.
- **Initial (`defaultValue`, default: `75`):** Starting value applied (clamped to `[min, max]`) whenever the flow starts.
- **Unit (`unit`, default: `"%"`):** Cosmetic label suffix displayed on the widget and node status; does not alter the numeric payload.
- **Topic (`topic`, default: `"control.slider"`):** Topic attached to emitted messages.
- **Loop safety:** Because incoming messages on `set` never trigger an emission on `value`, bidirectional feedback loops with **OBS Studio** or external hardware controllers are safe by design.
