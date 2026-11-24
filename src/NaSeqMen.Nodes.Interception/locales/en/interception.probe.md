# Input Probe

## Purpose

**Input Probe** is an interactive diagnostic mini-terminal and optional blocking trap for inspecting hardware strokes flowing from **Interception Hub** or **Interception Device**. It shows the exact timestamp, device slot (`KB#n` / `M#n`), phase (`↓` down, `↑` up, `↕` wheel), canonical key or mouse button name, and IBM Set 1 scan code (`SC:xx`, `E0`) of every incoming event.

Because some programmable mice, macro pads, or composite USB keyboards emit unexpected key chords or report buttons on a separate hidden keyboard interface, **Input Probe** lets you safely discover what a physical button actually sends before writing remap rules.

## Typical Scenarios

- **Discovering hidden hardware mappings:** Enable **Monitor All Devices** on **Interception Hub**, connect **`All Events`** to **Input Probe**, and press any special button on a mouse or keypad to see both the exact slot/device that fired and the exact key/button sequence it emitted.
- **Inspecting disruptive keys safely:** Click **Arm Capture** (with **Block When Armed** enabled) to inspect system keys such as `LWin`, `LWin+D`, `LAlt+F4`, or media/macro buttons without triggering Windows shortcuts while testing.
- **Forwarding to Debug:** Wire the **`Observed`** output into a **Debug** node to inspect the full `FlowMessage` JSON payload (`hardwareId`, `deviceSlot`, `scanCode`, `rawState`).

## Ports

### Inputs
- **`Event`** (`in`, Object) — Incoming stroke event basket from **Interception Hub** or **Interception Device**.

### Outputs
- **`Return`** (`return`, Object) — Connect back to the source node's **`Return / Action`** input. Passes all strokes through in normal live-monitoring mode, and marks strokes with `suppress = true` (and `blockActions = true`) while armed with blocking enabled.
- **`Observed`** (`observed`, Object) — Optional tap that forwards every observed stroke event (for example, to **Debug**).

## Configuration & Usage

### Live Monitoring vs. `Arm Capture` Blocking Trap
- **Live Monitoring (Disarmed, default):** Every incoming stroke is logged to the live mini-terminal on the node card and immediately returned with `suppress = false`, so Windows receives normal input while you watch events stream by.
- **Blocking Trap (`Arm Capture`):** Click **`Arm Capture`** on the node widget. When **Block When Armed** is enabled, incoming strokes are logged in the mini-terminal as blocked and suppressed before reaching Windows, so you can safely test keys or chords that would otherwise minimize windows, open Start, or close apps. Click again to disarm manually, or click **`Clear`** to wipe the terminal history.

### Built-In Safety Failsafes
Blocking live hardware input is protected by three independent automatic safeguards:
1. **`Esc` instant disarm:** Pressing `Esc` on any hooked keyboard immediately disarms capture and passes `Esc` through normally.
2. **Idle Auto-Off (`idleTimeoutSec`, default `15` s, range `1–120` s):** Automatically disarms if no events arrive for the configured number of seconds.
3. **Max Armed (`maxDurationSec`, default `45` s, range `2–180` s):** Hard upper time limit on how long capture can stay armed, even if events keep arriving continuously.

*(Additionally, the driver-level emergency panic combo `Ctrl+Alt+Pause` and the Hub/Device `Release` button remain active at all times.)*

### Parameters
- **Block When Armed** (`blockWhenArmed`, default `true`) — Suppresses incoming strokes while armed. Turn off if you want timed arming without suppression.
- **Idle Auto-Off (s)** (`idleTimeoutSec`, default `15`) — Inactivity auto-disarm timeout in seconds.
- **Max Armed (s)** (`maxDurationSec`, default `45`) — Hard maximum armed duration in seconds.
- **Terminal Lines** (`maxLines`, default `8`, range `3–25`) — Number of recent event rows kept in the mini-terminal.

### Tips
- Remember to wire **`Return`** back to **`Return / Action`** if you want **Arm Capture** to actually suppress input. Without the return wire, **Input Probe** still logs events, but cannot block them.
- Once you identify the exact device slot and key/button names in the terminal, use those names directly in **Hotkey Switch**, **Stroke Action**, or **Chord Matcher**, and turn off **Monitor All Devices** on the Hub.
