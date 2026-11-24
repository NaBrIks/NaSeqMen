# Debug

## Purpose

**Debug** taps into messages traveling across wires and streams structured entries to the editor's **Debug** sidebar so you can inspect payloads, topics, and basket properties in real time. Because it also forwards every incoming message unchanged through its `out` port, you can drop it either at the end of a branch or inline between two nodes.

## Typical Scenarios

- **Inspecting hardware or OBS events:** Attach **Debug** to **Keyboard Input**, **OBS Studio** (`status`, `sceneChanged`), or **Virtual Slider** to see exact payload types, topics, and property keys before writing a **C# Function**.
- **Inline wire probe:** Place **Debug** between a **Window Filter** and an output node to monitor traffic without breaking the flow, and toggle *Active* off once verified.
- **Comparing before & after transformation:** Place two **Debug** nodes with distinct *Tag* values (e.g. `raw` and `transformed`) before and after a transform node to trace how payload and properties change.
- **Capturing script or OBS errors:** Wire the `error` port of **C# Function** or the `status` port of **OBS Studio** to a tagged **Debug** node so issues stand out immediately.

## Ports

### Inputs

- **`in` (Message, `Any`)** — Accepts any message basket regardless of payload type.

### Outputs

- **`out` (Pass-through, `Any`)** — Emits a clone of the incoming message unconditionally, even when *Active* is unchecked.

## Configuration & Tips

- **Active (`active`, default: `true`):** Controls whether entries are sent to the Debug sidebar. Unchecking *Active* mutes logging (and stops updating the node's status preview), while **continuing to forward messages** on `out`.
- **Tag (`label`, default: empty):** Short custom label displayed alongside each log entry in the Debug sidebar. Helpful when several **Debug** nodes are active in the same workspace.
- **Full Basket (`fullBasket`, default: `true`):** When checked, includes the full `properties` dictionary together with `topic` and `payload`. Uncheck for compact payload-only inspection on high-frequency streams.
- **High-rate streams:** Avoid wiring high-frequency streams like raw **OBS Studio** `vuLevel` directly into an active **Debug** node unless you temporarily increase `meterThrottleMs` or mute *Active* after checking the format.
