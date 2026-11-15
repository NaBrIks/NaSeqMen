# Sources, event selection and sequence recognition — proposal

This document records the original design discussion. The five-column graph, modifier snapshots and a keyboard sequence recognizer are now available. See [Event sequences](event-sequences.md) for the implemented behavior.

## Graph vocabulary

Proposed flow: **Source → Events → Conditions → Processing → Actions**.

A connection owns the protocol session. A source binds that connection to an optional device/interface or substream. They are not always identical: a single Windows input connection can contain several mice and keyboards. An OBS connection can emit several event types.

An event selector specifies what to accept from that source: a control code, event type and phase. A sequence recognizer can be another event-stage operation. Contextual conditions (active application, running process) follow recognition. Ordinary value transforms and C# processing remain in the processing column.

The current `source` node combines source binding and event selection. Splitting it requires a versioned configuration migration that preserves connection, source, control and phase filters and outgoing links. Do not silently insert new runtime semantics merely to draw an extra column.

Discovery shows observed event identities grouped by exact source/interface. These groups do not assert that two HID interfaces belong to one physical mouse. A future physical-device binding needs reliable OS identity or explicit user confirmation, not matching VID/PID alone. Adding an observed event currently opens a prefilled source-node form and saves only after the user reviews it.

## Composite mouse action

For `Win down → E down → E up → Win up`, the recognizer must check order, phases, device scope and that Win remains held while E is tapped. The recognition instance has a bounded lifetime and resets on a mismatch, timeout or disconnect. Repeats should not advance an expected down step unless explicitly configured. Other devices must neither advance nor invalidate that instance.

Elapsed time is a constraint, not proof that the events came from one physical button. Firmware may emit keyboard reports indistinguishable from a person typing the same chord. Restricting recognition to a confirmed mouse keyboard interface avoids confusing it with the regular keyboard, but cannot recover information the device never transmits. A learning UI should record a sample temporarily, show the proposed sequence, and let the user confirm scope, phases and limits.

Recognition necessarily holds temporary progress and held-control information. With literally no memory between events, sequence recognition is impossible. The owner has now explicitly accepted current held-key state, including Shift/Ctrl/Alt/Win, as useful input state for conditions and processing. This does not authorize telemetry storage, replay or disk persistence. Sequence matching and its timeout/consumption policy remain a proposal; see [input-state.md](input-state.md).

Emit one semantic event only after the agreed completion point. For the example, completing on final Win-up avoids firing on a partial match. If the owner needs a held logical action rather than a single completed event, its down/up contract and cancellation cleanup must be chosen separately.

## Recognition versus suppression

Passive Raw Input observation cannot stop the original Win+E reaching Windows. Recognizing the chord after its last event does not undo the Explorer action. Suppression requires a separately selected interception mechanism and a policy for unmatched/partial sequences.

There are two materially different policies: consume the selected device's original controls entirely, or buffer potential sequences and release unmatched input. Buffering adds latency and introduces replay/ordering hazards. The owner's problematic mouse may suit the former policy, scoped to that device; it must not affect the regular keyboard or pointer movement implicitly. No driver, interception backend or suppression policy is installed or enabled by this discussion.

The debugger should show each recognized step, remaining expected step, timeout/mismatch reason and generated semantic event. Dry run suppresses resulting output actions through the host API; it does not automatically suppress physical input in Windows.
