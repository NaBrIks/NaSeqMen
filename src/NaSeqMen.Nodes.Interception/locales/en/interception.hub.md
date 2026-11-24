# Interception Hub

## Purpose

**Interception Hub** is the central multi-device entry point for the Interception kernel driver. Instead of placing a separate device node for every physical keyboard or mouse, a single hub exposes up to **4 keyboard channels (`KB 1`–`KB 4`)**, **4 mouse channels (`Mouse 1`–`Mouse 4`)**, a universal **`All Events`** monitor output, and a shared **`Return / Action`** loop-back input.

The hub coordinates hardware capture through a **loop-back decision architecture**:

```text
[Interception Hub: KB 1 / Mouse 1]
          │  (captured hardware stroke + correlationId)
          ▼
[Hotkey Switch / Stroke Action / Input Probe / Window Filter / C# Function]
          │  (suppress / pass + optional replacement sendKeys / sendMouse)
          ▼
[Interception Hub: Return / Action]
```

When a hooked device produces a stroke, the driver loop holds it briefly (up to `Decision Timeout (ms)`), sends a `FlowMessage` basket out the matching channel port, and waits for a decision returning to **`Return / Action`**. If a decision arrives, the driver suppresses or passes the original stroke and injects any attached replacement actions directly at the driver level.

## Typical Scenarios

- **Multi-device remapping:** Route a primary keyboard on `KB 1`, a secondary keypad on `KB 2`, and a mouse on `Mouse 1`, each wired to its own **Hotkey Switch** or processing chain, all returning decisions to the hub's **`Return / Action`**.
- **Hardware discovery & diagnostics:** Enable **Monitor All Devices**, wire **`All Events`** into **Input Probe**, and loop **Input Probe `Return`** back to **`Return / Action`** to discover which physical device slot and key/button names any button generates.
- **Direct hardware injection:** Send a message without a pending `correlationId` (containing `sendKeys` or `sendMouse`) into **`Return / Action`** to inject synthetic hardware input directly from another part of the graph.

## Ports

### Inputs
- **`Return / Action`** (`return`, Object) — Receives loop-back stroke decisions (`suppress`, `sendKeys`, `sendMouse`, `sendHardwareId`, `blockActions`) correlated by `correlationId`, or executes direct hardware injection when no pending stroke matches.

### Outputs
- **`All Events`** (`all`, Object) — Receives events from all active channels. When **Monitor All Devices** is enabled, captures events from every connected Interception keyboard and mouse.
- **`KB 1` – `KB 4`** (`kb1`–`kb4`, Object) — Dedicated stroke outputs for up to 4 configured keyboard channels.
- **`Mouse 1` – `Mouse 4`** (`mouse1`–`mouse4`, Object) — Dedicated button and wheel (and optionally movement) outputs for up to 4 configured mouse channels.

## Configuration & Usage

### Devices & Hub Channels Checklist
In the Inspector, the **Devices & Hub Channels** panel lists every detected keyboard and mouse reported by the Interception driver (click **↻ Scan** to refresh).
- Check a device to automatically assign it to the next free `KB 1–4` or `Mouse 1–4` channel.
- Or use the dropdown on any device row to bind it to a specific channel port (selecting an occupied channel swaps assignments cleanly).
- Identical devices sharing the same Hardware ID automatically get a `#N` instance suffix (for example, `HID\VID_xxxx&PID_xxxx#2`).
- After changing assignments or toggling **Active**, click **Deploy** to apply the new hardware filters.

### Key Parameters
- **Active** (`active`, default `false`) — Master switch for enabling kernel hooks on this hub. Disabled by default so importing or editing a flow never locks input unexpectedly.
- **Monitor All Devices** (`monitorAll`, default `false`) — Hooks all connected keyboards and mice and streams their strokes to **`All Events`**. Useful during setup and probing; disable during normal operation to keep overhead minimal and hook only the devices you need.
- **Default Action** (`defaultAction`, default `Pass`) — Fallback behavior if a stroke's loop-back times out or returns without an explicit `suppress` flag:
  - **Pass** — Lets the original stroke through to Windows (safest default).
  - **Suppress** — Drops the original stroke unless the flow explicitly marks `suppress = false`.
- **KB 1–4 / Mouse 1–4 Filters** (`kb1Filter`..`mouse4Filter`) — Channel selectors filled automatically by the device checklist, or entered manually as a Hardware ID substring, `HWID#N` instance selector, or explicit slot number (`1–10` for keyboards, `11–20` for mice).
- **Capture Mouse Move** (`captureMove`, default `false`) — Enables high-frequency relative cursor movement events on mouse channels. Leave off unless a flow specifically inspects pointer deltas.
- **Decision Timeout (ms)** (`decisionTimeoutMs`, default `60`) — Maximum wait time for a loop-back decision before applying **Default Action**.
- **DLL Path** (`dllPath`) — Optional explicit path to `interception.dll`. Leave blank to auto-detect.

### Safety & Emergency Recovery
- **Emergency Panic Combo (`Ctrl+Alt+Pause`):** Monitored directly inside the native driver loop on any hooked keyboard. Pressing left or right `Ctrl` + left or right `Alt` + `Pause` immediately clears all Interception filters and passes all input straight through to Windows.
- **`Release` button:** Available right on the node widget to trigger the same emergency filter release with one click.
- **Tip:** Always wire a return path (for example, `Hotkey Switch -> Return` or `Input Probe -> Return`) back to **`Return / Action`** so strokes resolve immediately instead of waiting for `Decision Timeout (ms)` on every keypress.
