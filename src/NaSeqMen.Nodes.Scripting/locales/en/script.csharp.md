# C# Function

## Purpose

**C# Function** compiles a C# snippet once on flow start using Roslyn and executes it for every message arriving on `in`. It gives you full .NET expression power for filtering, stateful counters/toggles, payload formatting, Interception action scripting (`Suppress`, `SendKeys`, `SendMouse`), and building structured requests for nodes such as **OBS Studio**.

## Typical Scenarios

- **Stateful debounce, counter, or toggle:** Store values in `state` across messages to count presses, rate-limit high-frequency events, or alternate outputs without external nodes.
- **Custom Interception remap logic:** Inspect incoming event properties in `props`, call `Suppress(true)` to block the original input, and invoke `SendKeys("LCtrl+C")` or `SendMouse(...)` for conditional remapping.
- **Conditional filtering & routing:** Inspect `text`, `topic`, or `props`, call `Drop()` to stop messages that do not meet a condition, and emit transformed payloads for matching ones.
- **Building raw OBS WebSocket requests:** Return a `Dictionary<string, object?>` with `requestType` and `requestData` wired into **OBS Studio** (`command`) to trigger any OBS WebSocket v5 action.

## Ports

### Inputs

- **`in` (Input, `Any`)** — Incoming message basket. A cloned working copy is passed to the script as `msg`.

### Outputs

- **`out` (Output, `Any`)** — Emits the resulting message unless `Drop()` was called or an error occurred.
- **`error` (Error, `String`)** — Emits error text when script compilation failed (`properties["errorKind"] = "compile"`) or an unhandled exception was thrown (`properties["errorKind"] = "runtime"`, along with `exceptionType` and `stackTrace`). Errors are also published to the Debug sidebar.

## Script API (`ScriptGlobals`)

Every script runs with the following global members and default namespaces (`System`, `System.Linq`, `System.Collections.Generic`, `System.Text`, `System.Text.Json`, `System.Text.RegularExpressions`, `NaSeqMen.Core.Messages`):

| Member | Type / Signature | Description |
| :--- | :--- | :--- |
| `msg` | `FlowMessage` | Cloned working copy of the incoming message basket. |
| `payload` | `object?` (get/set) | Shorthand for `msg.Payload`. |
| `text` | `string` (get) | Shorthand for `msg.PayloadAsString()`. |
| `topic` | `string?` (get/set) | Shorthand for `msg.Topic`. |
| `props` | `Dictionary<string, object?>` | Shorthand for `msg.Properties`. |
| `state` | `Dictionary<string, object?>` | Per-node persistent dictionary kept across messages until the flow stops/restarts. |
| `suppress` | `bool?` (get/set) | Reads or writes `msg.Properties["suppress"]` (set `null` to remove). |
| `Suppress(bool value = true)` | `void` | Sets `msg.Properties["suppress"] = value` (used by Interception pipelines). |
| `SendKeys(string sequence)` | `void` | Sets `msg.Properties["sendKeys"] = sequence` (e.g. `"LCtrl+X"`). |
| `SendMouse(string action)` | `void` | Sets `msg.Properties["sendMouse"] = action`. |
| `Drop()` | `void` | Prevents emitting to `out` for the current message. |
| `Log(string message)` | `void` | Writes a diagnostic line to the Debug sidebar tagged `"script"`. |
| `Clone(object? newPayload = null)` | `FlowMessage` | Returns a clone of `msg`, optionally replacing its payload. |

### Return Value Rules

- `return msg;` (or returning another `FlowMessage`) — emits that `FlowMessage` on `out`.
- `return someValue;` (any non-null non-`FlowMessage`) — emits `msg` with its payload replaced by `someValue`, preserving `topic` and `props`.
- No `return` statement (or `return null;`) — emits the modified working `msg`.
- `Drop(); return null;` — emits nothing on `out`.

## Configuration & Tips

- **C# Script (`code`):** Compiled once when the flow starts (not on every message), so execution is fast and predictable.
- **Thread-safe `state`:** Incoming executions on a single **C# Function** node are serialized automatically, so mutating `state` does not require manual locking.
- **Reset on restart:** `state` is cleared whenever the flow stops or restarts.
- **Example — stateful threshold gate:**
  ```csharp
  if (!double.TryParse(text, out var level) || level < 70)
  {
      Drop();
      return null;
  }

  var hits = state.TryGetValue("hits", out var v) ? (int)v! : 0;
  state["hits"] = ++hits;
  Log($"Peak hit #{hits}: {level}");
  return $"PEAK {level:0.0}%";
  ```
- **Debugging tip:** Wire `error` to a **Debug** node while developing scripts so both compilation diagnostics and runtime stack traces appear clearly in the Debug sidebar.
