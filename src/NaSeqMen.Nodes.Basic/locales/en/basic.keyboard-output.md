# Keyboard Output

## Purpose

**Keyboard Output** types the incoming message's string payload into whichever Windows window currently has keyboard focus using Win32 `SendInput` with Unicode events. Synthetic keystrokes are tagged internally so they will not re-trigger **Keyboard Input** nodes in the same flow.

## Typical Scenarios

- **Text expansion & snippet insertion:** Connect a **Virtual Button** or **C# Function** node to `in` to type canned responses, timestamps, email signatures, or formatted templates into any editor.
- **Targeted typing into a specific app:** Chain **Focus Window** (`found`) -> **Keyboard Output** (`in`) so the destination application is brought to the foreground before text is typed.
- **Normalized uppercase typing:** Place **CAPS (ToUpper)** before **Keyboard Output** to ensure incoming text is trimmed, wrapped with a prefix/suffix, and typed in uppercase.
- **Multi-step macros with pass-through:** Wire `out` (`Sent`) to a **Debug** node or subsequent action to confirm completion or trigger a follow-up step after typing finishes.

## Ports

### Inputs

- **`in` (Text, `String`)** — Message whose string payload will be typed into the focused window. Empty strings are ignored.

### Outputs

- **`out` (Sent, `String`)** — Cloned pass-through of the incoming message emitted after typing completes, with `properties["sentBy"] = "basic.keyboard-output"`.

## Configuration & Tips

- **Active (Send) (`active`, default: `true`):** Master enable switch. Uncheck to temporarily mute typing (status displays `muted`) without removing wires while testing a flow.
- **Delay (ms) (`delayMs`, default: `0`):** Optional wait before sending input (clamped to `0`–`5000` ms). Useful when the user or a preceding step needs a moment to release modifier keys or switch focus.
- **Focus matters:** Because `SendInput` targets the active foreground window, be careful when triggering flows from the editor—either add a short `delayMs`, trigger via a global hotkey, or place **Focus Window** right before this node.
- **Unicode & layouts:** Unicode `SendInput` types characters independently of the current keyboard layout in most modern Windows applications (browsers, Electron apps, editors, terminals).
- **Elevated windows (UAC):** Windows UIPI prevents non-elevated processes from sending input to administrator-elevated windows. Run the host with matching privileges only if you must type into an elevated app.
