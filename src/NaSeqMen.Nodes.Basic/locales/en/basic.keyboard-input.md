# Keyboard Input

## Purpose

**Keyboard Input** passively monitors physical keyboard events on Windows using the Raw Input API and emits each matching keystroke as a message basket. Because it is strictly passive, keystrokes are never blocked from reaching the active application. The node also provides a manual trigger button on the canvas so you can inject test text directly into a flow without pressing physical keys.

## Typical Scenarios

- **Global hotkey trigger for OBS or scripts:** Disable *Printable Only*, set *Key Filter* to `F9, F10`, and connect `out` to an **OBS Studio** or **C# Function** node to react to function keys from any application.
- **Window-aware command reactions:** Wire `out` into a **Window Filter** node so keystrokes only trigger downstream actions when a specific editor or tool is in the foreground.
- **Quick flow testing without hardware:** Disable *Capture Keyboard*, enter sample text in *Manual Payload*, and click the node's trigger button to test downstream transforms and outputs repeatedly.
- **Keystroke logging & diagnostics:** Leave *Key Filter* empty and connect `out` to a **Debug** node to inspect key names, virtual key codes (`vkCode`), and modifier flags (`shift`, `ctrl`, `alt`).

## Ports

### Inputs

This node has no input ports.

### Outputs

- **`out` (Key / Text, `String`)** — Emits the typed character (if printable) or key name (e.g. `F9`, `Enter`, `Space`).
  - For raw keyboard events, `topic` is `"keyboard.raw"` and `properties` include `source` (`"raw-input"`), `key`, `vkCode`, `printable`, `phase` (`"down"` or `"up"`), `shift`, `ctrl`, and `alt`.
  - For manual triggers, `topic` is `"keyboard.manual"` and `properties` include `source` (`"manual-trigger"`) and `nodeTitle`.

## Configuration & Tips

- **Capture Keyboard (`captureGlobal`, default: `true`):** Enables global Raw Input listening while the flow is running. Turn off if you only want to use the node as a manual text injector.
- **Printable Only (`printableOnly`, default: `true`):** Filters out non-printable keys such as `Shift`, `Ctrl`, `Alt`, `F1`–`F12`, `Escape`, and arrow keys. **Uncheck this** whenever you want to filter by function keys or control keys.
- **Key Down Only (`onlyKeyDown`, default: `true`):** Ignores key-release events so each key press produces one event instead of two. Uncheck if downstream logic needs both `"down"` and `"up"` phases.
- **Key Filter (`filterKey`, default: empty):** Comma-separated list of key names or characters (case-insensitive), e.g. `A, Space, F9`. Leave empty to emit all keys matching the other options.
- **Manual Payload (`testText`, default: `"hello world"`):** Text sent when you click the trigger button on the node.
- **No self-feedback loop:** Text typed by **Keyboard Output** is tagged internally so **Keyboard Input** ignores synthetic output and does not loop back.
- **Need to block or remap keys?** Because this node is passive, use the Interception plugin nodes if you need to suppress original keystrokes before Windows sees them.
