# Event sequences

The graph has five columns: Source → Events → Conditions → Processing → Actions. Previously saved source filters still work. Adding an observed event from Devices and events creates an event selector and connects a source bound to that interface.

For a composite mouse button, find its keyboard interface in Devices and events and select **Add sequence…**. The form starts with left Win+E. Select **Record sequence** and press and release the button once to replace the example with the observed steps. Review the interface, the steps, the timeout and the output event name before saving. The saved event is ready to connect to conditions and actions.

Steps have one raw key code and a phase per line. A sequence uses one Windows input connection and exactly one interface. The recognizer emits one named numeric event (value 1, phase change) after the final release. Partial matches expire and reset after an unexpected key, input loss or removal. Repeated down for an already held step does not advance the sequence. A normal event selector may still independently match the original steps.

Use **Modifier is held** in a condition to check Shift, Ctrl, Alt or Win, including explicitly left/right variants. Source scope checks the interface that emitted the signal. Global scope combines the observed held keys by interface. C# sees the same event snapshot via `Input.Modifiers`.

This release uses passive Windows Raw Input. Windows can still act on the original Win+E. Device-specific system suppression and the former KOE output rules require a separate interception integration and explicit mappings.
