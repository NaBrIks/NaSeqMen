# Window Filter

## Purpose

**Window Filter** inspects the currently active Windows foreground window whenever a message arrives and routes the message to either `match` or `noMatch` based on the process name and/or window title. It also enriches the message basket with active window metadata so downstream scripts or debug nodes can see which application is in focus.

## Typical Scenarios

- **Application-specific hotkeys & macros:** Place **Window Filter** (e.g. *Process* = `code` or `blender`) after **Keyboard Input** or an Interception node so a shortcut performs one action in your IDE and a different fallback action (`noMatch`) everywhere else.
- **Per-tab or per-document routing:** Match a browser or editor by *Process* plus a *Title* substring (for example, `Meet` or `Production`) to guard sensitive actions.
- **Exclude list with Invert Match:** Set *Process* to a game or video player and check *Invert Match* so global shortcuts are automatically suppressed while that application is active.
- **Inspecting active window info:** Route messages through **Window Filter** into **Debug** (with *Full Basket* enabled) to discover the exact `process` and `title` strings of any foreground window.

## Ports

### Inputs

- **`in` (Message, `Any`)** — Any incoming message basket.

### Outputs

- **`match` (Match, `Any`)** — Emitted when the active foreground window satisfies the filter (or does not satisfy it when *Invert Match* is checked).
- **`noMatch` (No Match, `Any`)** — Emitted for the complementary branch.

Both outputs carry a clone of the message with `properties["activeWindow"] = { hwnd, process, title }` attached whenever a foreground window exists.

## Configuration & Tips

- **Process (`processName`, default: empty):** Process name with or without `.exe` (e.g. `notepad` or `notepad.exe`). Matched case-insensitively by exact match or substring.
- **Title (`titleContains`, default: empty):** Case-insensitive substring checked against the foreground window's title bar text.
- **Combining Process and Title:** When both fields are filled, **both** must match (logical AND). When **both** fields are empty, the filter never matches (routes to `noMatch`, or `match` if *Invert Match* is enabled).
- **Invert Match (`invert`, default: `false`):** Reverses the match decision before routing to `match` / `noMatch`.
- **Chaining multiple apps:** Chain the `noMatch` output of one **Window Filter** into the `in` port of the next to build an `if / else if / else` router across several applications.
