# CAPS (ToUpper)

## Purpose

**CAPS (ToUpper)** transforms the string representation of an incoming message's payload to uppercase (`ToUpperInvariant`), optionally trims leading/trailing whitespace, and wraps the result with a configurable prefix and suffix. All other fields of the message basket (topic and properties) are preserved intact.

## Typical Scenarios

- **Standardizing labels or codes:** Convert user-typed text, tags, or identifiers into consistent uppercase format before sending them to **Keyboard Output** or **Window PostText**.
- **Decorating messages with markers:** Enable *Trim Whitespace* and set *Prefix* to `[ALERT] ` and *Suffix* to ` !` to format notifications or chat callouts without writing a script.
- **Normalizing input before downstream matching:** Ensure incoming strings have consistent casing and no accidental outer spaces before passing them into a **C# Function** or **Debug** log.

## Ports

### Inputs

- **`in` (Input, `String`)** — Incoming message whose payload is converted to a string via `PayloadAsString()`.

### Outputs

- **`out` (UPPER, `String`)** — Cloned message carrying the transformed string payload. Original `topic` and `properties` are preserved, and `properties["transformedBy"] = "basic.to-upper"` is added.

## Configuration & Tips

- **Trim Whitespace (`trim`, default: `false`):** Removes leading and trailing whitespace from the incoming payload *before* uppercasing and adding the prefix/suffix.
- **Prefix (`prefix`, default: empty):** Text prepended directly before the uppercased string. The prefix itself keeps its original capitalization.
- **Suffix (`suffix`, default: empty):** Text appended directly after the uppercased string. The suffix itself keeps its original capitalization.
- **Order of operations:** `prefix + (trim ? text.Trim() : text).ToUpperInvariant() + suffix`.
- **Whitespace in prefix/suffix:** Spaces inside *Prefix* or *Suffix* (for example `"ID: "`) are preserved verbatim, even when *Trim Whitespace* is checked.
