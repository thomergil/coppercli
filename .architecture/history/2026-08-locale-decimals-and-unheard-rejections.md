# 2026-08 — Comma decimals, and nobody listening for the rejection

**Who:** Thomer, with Claude Opus 4.8, in `4698964`.

**Tried:** Controller-generated G-code used ordinary interpolated `{value:F3}` formatting —
the current culture's number format. Sends were fire-and-forget.

**Believed:** Formatting was a display concern, and sending a line to GRBL was equivalent
to executing it.

**Realized:** On any decimal-comma locale — German, French, Dutch, Spanish, most of Europe
and Latin America — every coordinate went out as `Z-1,000`. GRBL rejects that. Because
nothing listened for a rejection, **the safety retract reported success while the tool
never moved, and milling continued.** Two independent defects that were only lethal
together.

**Lesson → rules `culture-invariant-gcode` and `fail-safe-on-uncertainty`.** A command
that was *sent* is not a command that was *done*. GRBL rejections must reach the caller as
an event (`CommandRejected`), because waiting for Idle cannot detect one — a refused
command never leaves Idle, so the wait succeeds immediately. And formatting for a machine
is not formatting for a human: every generated line goes through `GCodeFormat.Inv`. The
side effect (numbers and dates now display invariant regardless of the operator's
locale) was accepted deliberately.

**Touches:** seam `machine → GRBL`, rule `culture-invariant-gcode`, rule
`fail-safe-on-uncertainty`, `coppercli.Core/Util/GCodeFormat.cs`,
`coppercli.Core/Communication/GrblRejection.cs`,
`coppercli.Tests/CultureInvariantGCodeTests.cs`.
