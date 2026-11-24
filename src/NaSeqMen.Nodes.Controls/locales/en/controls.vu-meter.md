# VU Meter

## Purpose

**VU Meter** displays a real-time visual level bar directly on the flow canvas with stereo support (left/right channels when provided), decibel readout, and automatic peak-hold decay. It also supports filtering by source name or topic and passes matching messages downstream through its `out` port.

## Typical Scenarios

- **Live OBS audio monitoring:** Connect **OBS Studio** (`vuLevel`) -> **VU Meter** (`in`). Set *Source Filter* to `Mic/Aux` on one meter and `Desktop Audio` on a second meter to monitor multiple OBS channels simultaneously from a single OBS node.
- **Audio-reactive triggers (clipping or voice detection):** Wire **OBS Studio** (`vuLevel`) -> **VU Meter** (`out`) -> **C# Function** to both visualize the channel level and trigger alerts or actions when the level crosses a threshold.
- **Generic numeric telemetry gauge:** Feed any normalized metric (0–100%, CPU load, sensor reading, or **Virtual Slider** output) into `in` and adjust *Min*, *Max*, and *Unit* to match the signal range.

## Ports

### Inputs

- **`in` (Level, `Number`)** — Accepts numeric level measurements.
  - If `properties["leftPercent"]` and `properties["rightPercent"]` are present (as emitted by **OBS Studio**), the meter renders independent stereo bars; otherwise both bars follow the normalized payload value.
  - If `properties["db"]` is present, the status badge displays the level in `dB` (e.g. `-12.4 dB`).

### Outputs

- **`out` (Pass-through, `Number`)** — Clones and forwards messages that pass *Source Filter* and contain a valid numeric payload. Filtered-out messages are not forwarded.

## Configuration & Tips

- **Source Filter (`sourceFilter`, default: empty):** Optional case-insensitive substring matched against `properties["inputName"]` or `message.Topic`. Leave empty to display all incoming level messages. When set, only matching messages update the widget and pass through `out`.
- **Min (`min`, default: `0`) & Max (`max`, default: `100`):** Defines how raw numeric payloads map to `0..100%` on the bar. For **OBS Studio** `vuLevel`, keep the defaults `0` and `100`.
- **Unit (`unit`, default: `"%"`):** Suffix shown in the status badge when `properties["db"]` is absent.
- **Peak hold behavior:** Peaks are held for `1.2` seconds and then decay smoothly at `80 %/s`, making transients easy to read.
- **Multiple channels from one OBS node:** Leave *Audio Input* empty on **OBS Studio** so it emits meters for all sources, then fan out `vuLevel` to several **VU Meter** nodes with different *Source Filter* values.
