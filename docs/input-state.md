# Input state — owner decision and implementation boundary

The Windows input module tracks held keys automatically in executor memory, by interface. It distinguishes left and right Shift, Ctrl, Alt and Win and attaches a snapshot to each observed signal. Discover shows the currently observed sides next to the event cards. The Modifier is held condition supports either side or a specific side, scoped to the event's source or across observed devices. C# reads the same snapshot from `Input.Modifiers`. `SourceKnown` distinguishes observed transitions from unknown state. Synthetic API input has no physical snapshot.

The input module should own bounded, volatile modifier state: left/right Shift, Ctrl, Alt and Win. Expose both per-source state and an explicitly aggregated view. Releasing Ctrl on one keyboard must not clear Ctrl still held on another. Device removal clears only that device's contribution; session/reset boundaries clear invalid information. Unknown initial per-device state must not be presented as confirmed released.

The input module applies each down/up transition before creating the snapshot. Disconnect clears the contribution of that interface. Restarting input capture or losing the raw queue resets tracked states.

Global modifier combinations are useful for actions such as Ctrl + a mouse click. Device-scoped state is required to recognize a composite mouse without consuming another keyboard's input. The scope must be visible in the condition/recognizer configuration. Synthetic output state must not silently become physical input state.

This is current input state, not an event history, general retained-value broker, replay mechanism or disk-backed store. A sequence recognizer additionally needs its own short-lived progress, expected next step, held keys and timeout. The five-column graph and the exact sequence contract are discussed in [input-events-proposal.md](input-events-proposal.md).

Discover lists the raw key and its combinations separately. A combination starts on a non-repeated key press and uses the modifier groups held on the same interface. The Windows foreground discovery hook exposes a single logical source for all keyboards. Adding a combination discovered through that hook requires selecting a physical interface for the Event. At runtime the Event compares the exact modifier groups on that interface, accepting either side of each group.

An Event follows its matched links on success and its single optional no-match link on failure. A sequence with steps still pending follows neither outcome. A Default Event accepts input from a no-match link and passes that signal to the configured graph outputs. Existing links without an outcome use the matched path. These outcomes describe routing in the graph. They do not control delivery of physical input to Windows.
