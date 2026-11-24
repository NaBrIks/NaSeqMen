# Focus Window

## Purpose

**Focus Window** searches visible top-level Windows windows by process name and/or title substring, optionally restores and activates the matched window in the foreground, and attaches its handle (`targetHwnd`) and metadata (`targetWindow`) to the message basket before forwarding it downstream.

## Typical Scenarios

- **Reliable typing into a specific app:** Chain **Virtual Button** -> **Focus Window** (`found`) -> **Keyboard Output** (`in`). Even if the target app is behind other windows or minimized, it is restored and focused before text is typed.
- **Silent background targeting for Window PostText:** Uncheck *Bring to Front* (`focus = false`) and connect `found` -> **Window PostText** (`in`) with *Process* left blank on **Window PostText**. **Focus Window** resolves the window handle and passes `targetHwnd` downstream without stealing focus.
- **Presence check & fallback branch:** Use the `found` and `notFound` outputs to verify whether an application is currently open before running a macro or logging a warning to **Debug**.
- **One-click app switcher:** Wire a **Virtual Button** or hotkey into **Focus Window** to jump straight to a frequently used terminal, browser window, or editor.

## Ports

### Inputs

- **`in` (Message, `Any`)** — Incoming message that triggers the window lookup and optional activation.

### Outputs

- **`found` (Found, `Any`)** — Cloned message emitted when a matching visible window is located (and after focusing + `waitAfterFocusMs`, if *Bring to Front* is enabled). Attaches:
  - `properties["targetHwnd"]` — Numeric Win32 `HWND` (`long`), consumed automatically by **Window PostText**.
  - `properties["targetWindow"]` — Object `{ hwnd, process, title }`.
- **`notFound` (Not Found, `Any`)** — Cloned message emitted if no matching visible window is open (or if both *Process* and *Title* are empty).

## Configuration & Tips

- **Process (`processName`, default: `"notepad"`):** Target process name with or without `.exe`. Case-insensitive exact or substring match.
- **Title (`titleContains`, default: empty):** Optional case-insensitive substring matched against the window title. Useful when a process has multiple open windows (e.g. different browser profiles or editor workspaces).
- **Bring to Front (`focus`, default: `true`):** Restores minimized windows (`SW_RESTORE`) and brings the matched window to the foreground. Uncheck if you only need to locate the window handle for background operations.
- **Focus Wait (ms) (`waitAfterFocusMs`, default: `40`):** Pause in milliseconds (`0`–`2000`) after activating the window so it finishes redrawing and is ready to accept keystrokes. Increase to `80`–`150` ms for heavy Electron/web apps if the first character is ever dropped.
- **Visible windows only:** Minimized windows on the taskbar are found and restored, while hidden/tray-only windows and NaSeqMen's own process windows are skipped.
