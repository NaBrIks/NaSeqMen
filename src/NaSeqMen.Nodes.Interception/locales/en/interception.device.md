# Interception Device

## Purpose

**Interception Device** hooks a **single** physical keyboard or mouse through the Interception kernel driver, emits its hardware stroke events on **`Stroke Event`**, and accepts loop-back decisions or direct hardware injections on **`Return / Action`**.

### Single-Device Hook vs. Interception Hub
- Use **Interception Device** when a flow only needs one dedicated device (such as a single secondary keypad or one specific mouse), when you want per-device `Default Action` or `Decision Timeout (ms)` settings, or when you want to use the built-in manual **Test Trigger Key** to simulate strokes without touching physical hardware.
- Use **Interception Hub** when coordinating multiple keyboards and mice in one workspace, or when you want the **`All Events`** monitor port for device discovery.

## Typical Scenarios

- **Dedicated secondary keypad:** Bind a single USB keypad by its `Hardware ID`, wire **`Stroke Event`** to **Hotkey Switch**, and loop **`Return`** back to **`Return / Action`** while leaving the main keyboard completely unhooked.
- **Isolated mouse button remapping:** Set **Device Kind** to `Mouse`, select the mouse in **Connected Devices**, keep **Capture Mouse Move** disabled, and remap side buttons (`X1`, `X2`) or wheel actions cleanly.
- **Flow dry-run testing:** Configure **Test Trigger Key** (for example `D` or `X1`) and manually trigger the node to test downstream routing (`Hotkey Switch`, `Stroke Action`, `Window Filter`, `Debug`) even when **Active** is off.

## Ports

### Inputs
- **`Return / Action`** (`return`, Object) — Accepts loop-back stroke decisions (`suppress`, `sendKeys`, `sendMouse`, `sendHardwareId`) matched by `correlationId`. Messages arriving without a pending `correlationId` are executed as direct hardware injections.

### Outputs
- **`Stroke Event`** (`out`, Object) — Emits each captured hardware stroke as a `FlowMessage` basket containing `correlationId`, `deviceSlot`, `deviceKind`, `hardwareId`, `key` / `button`, `phase` (`down`, `up`, `wheel`, `move`), and scan code metadata.

## Configuration & Usage

### Selecting a Device
In the Inspector, open **Connected Devices**, click **↻ Scan**, and select the target physical device. You can also configure matching manually:
- **Device Kind** (`deviceKind`) — Choose `Keyboard` or `Mouse`.
- **Hardware ID** (`hardwareIdFilter`) — Case-insensitive substring of the device Hardware ID (for example `VID_xxxx&MI_00`). Hardware IDs stay stable across USB port changes and reboots. Use `*` to match the first device of the selected kind.
- **Instance Index** (`instanceIndex`, default `1`) — 1-based index for distinguishing multiple identical physical devices that report the exact same Hardware ID (for example, two identical keyboards: `1` for the first, `2` for the second).
- **Explicit Slot** (`explicitSlot`, default `0`) — When set to `0`, matching uses `Hardware ID` + `Instance Index`. Setting `1–10` (keyboard) or `11–20` (mouse) binds directly to that raw driver slot number instead.

### Loop-Back Wiring & Runtime Parameters
Every hooked device should have a fast loop-back path returning each stroke to **`Return / Action`**:

```text
[Interception Device: Stroke Event] ──► [Hotkey Switch / Stroke Action] ──► [Interception Device: Return / Action]
```

- **Active** (`active`, default `false`) — Enables the hardware hook when deployed. Kept off by default for safety.
- **Capture Mouse Move** (`captureMove`, default `false`) — Applies only to mice; leave disabled unless pointer movement deltas are required.
- **Default Action** (`defaultAction`, default `Pass`) — Fallback (`Pass` or `Suppress`) if the loop-back times out or omits `suppress`.
- **Decision Timeout (ms)** (`decisionTimeoutMs`, default `60`) — Maximum wait time for the return decision.
- **DLL Path** (`dllPath`) — Optional explicit path to `interception.dll` (blank = auto-detect).
- **Test Trigger Key** (`testKey`, default `D`) — Key or mouse button name emitted on manual trigger.

### Tips & Safety
- Pressing **`Ctrl+Alt+Pause`** on any hooked keyboard or clicking **`Release`** on the node widget immediately releases all active Interception filters.
- Prefer **Hardware ID** (plus **Instance Index** for identical hardware) over **Explicit Slot**, because Windows may reassign Interception slot numbers after unplugging devices or rebooting.
