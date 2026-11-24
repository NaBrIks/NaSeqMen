# OBS Studio

## Purpose

**OBS Studio** connects to OBS Studio (version 28+ built-in WebSocket v5 server) with automatic reconnect. It provides bidirectional control over scenes, audio volumes, mute states, recording, streaming, and arbitrary WebSocket v5 requests, while streaming live audio VU meters and state-change events back into your flow. An inline status widget displays connection state, OBS version, and interactive scene switching.

## Typical Scenarios

- **Live studio mixer on the canvas:** Wire a **Virtual Slider** (`value`) -> `volume`, **Virtual Button** (`press`) -> `mute`, and loop `volumeChanged` / `muteChanged` back to the controls' `set` inputs while sending `vuLevel` to a **VU Meter**.
- **Global hotkey scene & recording control:** Trigger `scene`, `record`, or `stream` from **Keyboard Input** or Interception hotkey nodes so you can switch scenes or start/stop recording while another application is full-screen.
- **Multi-source audio monitoring:** Leave *Audio Input* empty, enable *Audio VU Meters*, and fan out `vuLevel` to multiple **VU Meter** nodes, each configured with a different *Source Filter* (e.g. `Mic/Aux`, `Desktop Audio`).
- **Advanced automation via Raw Requests:** Use a **C# Function** to send `{ "requestType": "SetSceneItemEnabled", "requestData": { ... } }` into `command` and inspect the response on `status`.

## Ports

### Inputs

- **`scene` (Set Scene, `String`)** — Switches the current program scene (`SetCurrentProgramScene`). If the payload is empty, falls back to the *Default Scene* parameter.
- **`volume` (Set Volume, `Number`)** — Sets volume (`SetInputVolume`) for the target input (`properties["inputName"]` if provided, otherwise *Audio Input*) using the selected *Volume Scale*.
- **`mute` (Mute / Toggle, `Any`)** — Targets `properties["inputName"]` or *Audio Input*. Sends `SetInputMute` when the payload is an explicit boolean (`true`/`false`), or `ToggleInputMute` for any other payload (e.g. `"toggle"`).
- **`record` (Record, `Any`)** — `true` or `"start"` starts recording; `false` or `"stop"` stops it; any other value toggles recording.
- **`stream` (Stream, `Any`)** — `true` or `"start"` starts streaming; `false` or `"stop"` stops it; any other value toggles streaming.
- **`command` (Raw Request, `Object`)** — Accepts either a plain request type string (e.g. `"GetStats"`) or an object/dictionary `{ "requestType": "...", "requestData": { ... } }`. Emits the full response on `status`.

### Outputs

- **`vuLevel` (VU Level (0..100), `Number`)** — Emits peak audio level scaled `0..100` (`-60..0 dB`) with `topic = inputName` and `properties`: `inputName`, `mul`, `db`, `leftPercent`, `rightPercent`.
- **`sceneChanged` (Scene Changed, `String`)** — Emits the new scene name (`topic = "obs.scene"`, `properties.sceneName`).
- **`muteChanged` (Mute Changed, `Boolean`)** — Emits `true`/`false` (`topic = inputName`, `properties.inputName`, `properties.inputMuted`).
- **`volumeChanged` (Volume Changed, `Number`)** — Emits the updated volume in the configured *Volume Scale* (`topic = inputName`, `properties`: `inputName`, `mul`, `db`, `percent`).
- **`status` (Status / Response, `Object`)** — Emits connection updates (`obs.connection`), stream/record state events (`obs.stream`, `obs.record`), raw command responses (`obs.response`), and errors (`obs.error`).

## Configuration & Tips

- **OBS setup:** In OBS Studio, open **Tools -> WebSocket Server Settings**, enable the server (default port `4455`), and copy the server password into *Password* (`password`).
- **Connect (`enabled`, default: `true`):** Disable temporarily if OBS is not installed or running and you want to silence reconnect attempts.
- **Host (`host`, default: `"127.0.0.1"`) & Port (`port`, default: `4455`):** Supports both local and LAN OBS instances.
- **Audio Input (`audioSource`, default: empty):** Sets both the default target for `volume`/`mute` inputs and the filter for `vuLevel`/`muteChanged`/`volumeChanged` outputs. Leave empty to receive events for all inputs and specify `props["inputName"]` per message when controlling multiple channels through one node.
- **Default Scene (`sceneName`, default: empty):** Optional fallback scene name when `scene` receives an empty payload.
- **Audio VU Meters (`subscribeMeters`, default: `true`) & VU Interval (`meterThrottleMs`, default: `50`):** Disable `subscribeMeters` if you do not need VU meters to reduce WebSocket traffic. Increase `meterThrottleMs` (e.g. `100` ms) for lighter UI updates.
- **Volume Scale (`volumeScale`, default: `"percent"`):**
  - `percent` (`0..100%`) — Linear map of `-60..0 dB` (`0%` sets multiplier to `0`).
  - `mul` (`0..1 Multiplier`) — Raw OBS linear multiplier (`0..20` allowed on input).
  - `db` (`-60..0 dB`) — Direct decibel scale (`<= -100 dB` sets multiplier to `0`).
