# Window PostText

## Purpose

**Window PostText** delivers the incoming message's text payload directly to a target Windows window in the background using Win32 `PostMessageW(WM_CHAR)` without changing the foreground window or moving focus. It automatically locates common classic Win32 text controls (or a specified child class) and works either standalone or paired with an upstream **Focus Window** node.

## Typical Scenarios

- **Background logging into Notepad or a Scintilla editor:** Set *Process* to `notepad` (or `notepad++`) and wire formatted messages from a **C# Function** or **CAPS (ToUpper)** node to append notes silently while you keep working in another window.
- **Chaining with Focus Window (silent mode):** Place **Focus Window** with *Bring to Front* unchecked (`focus = false`) before **Window PostText** (with *Process* and *Title* left empty) to reuse `properties["targetHwnd"]` cleanly across multiple steps.
- **Targeting a specific legacy dialog control:** Set *Child Class* to a Win32 control class (inspected via Spy++ or similar Win32 tool) to post characters into a particular field inside a classic Win32 dialog.

## Ports

### Inputs

- **`in` (Text, `String`)** — Message whose string payload is posted character-by-character via `WM_CHAR` (`\r` characters are skipped). Empty strings are ignored.

### Outputs

- **`sent` (Sent, `String`)** — Cloned message emitted after posting succeeds, with `properties["postedHwnd"]` set to the target window handle in hex (e.g. `"0x1A0F2"`).
- **`notFound` (Not Found, `String`)** — Cloned message emitted if no target window is found or posting fails.

## Configuration & Tips

- **Active (Post) (`active`, default: `true`):** Uncheck to mute posting (status becomes `muted`) without disconnecting wires.
- **Process (`processName`, default: empty) & Title (`titleContains`, default: empty):**
  - If either field is filled, **Window PostText** searches for the matching visible window directly.
  - If **both** are left empty, it automatically reuses `properties["targetHwnd"]` placed in the basket by an upstream **Focus Window** node.
- **Child Class (`childClass`, default: empty):** Optional Win32 child window class name (exact or substring, case-insensitive). When empty, the node auto-detects classic edit controls (`RichEditD2DPT`, `Edit`, `Scintilla`, `RichEdit20W`, `RICHEDIT50W`) and falls back to the top-level window handle if none are found.
- **Compatibility note (classic vs. modern apps):** Background `WM_CHAR` works reliably with classic Win32 controls (Notepad, Notepad++/Scintilla, legacy Win32 tools), whereas modern Electron/Chromium apps, UWP apps, and DirectX games typically ignore unfocused `WM_CHAR` messages. For those apps, use **Focus Window** (*Bring to Front* enabled) -> **Keyboard Output** instead.
- **Elevated windows (UAC):** Windows UIPI blocks `PostMessage` from a standard process to an elevated (Administrator) window.
