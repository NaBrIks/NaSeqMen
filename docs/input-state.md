# Input state — owner decision and implementation boundary

The Windows input module tracks held keys automatically in executor memory, by interface. It distinguishes left and right Shift, Ctrl, Alt and Win internally and attaches a snapshot to each observed signal. There is no tracking switch in node settings. The explicit Modifier is held condition offers Shift, Ctrl, Alt and Win, with either side satisfying the check. Its scope can be the event's source or all observed devices. C# reads individual sides from `Input.Modifiers`. `SourceKnown` distinguishes observed transitions from unknown state. Synthetic API input has no physical snapshot.

The input module should own bounded, volatile modifier state: left/right Shift, Ctrl, Alt and Win. Expose both per-source state and an explicitly aggregated view. Releasing Ctrl on one keyboard must not clear Ctrl still held on another. Device removal clears only that device's contribution; session/reset boundaries clear invalid information. Unknown initial per-device state must not be presented as confirmed released.

The input module applies each down/up transition before creating the snapshot. Disconnect clears the contribution of that interface. Restarting input capture or losing the raw queue resets tracked states.

Global modifier combinations are useful for actions such as Ctrl + a mouse click. Device-scoped state is required to recognize a composite mouse without consuming another keyboard's input. The scope must be visible in the condition/recognizer configuration. Synthetic output state must not silently become physical input state.

This is current input state, not an event history, general retained-value broker, replay mechanism or disk-backed store. A sequence recognizer additionally needs its own short-lived progress, expected next step, held keys and timeout. The five-column graph and the exact sequence contract are discussed in [input-events-proposal.md](input-events-proposal.md).
