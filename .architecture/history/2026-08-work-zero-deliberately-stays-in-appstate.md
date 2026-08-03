# 2026-08 — Why `IsWorkZeroSet` deliberately does *not* live in `Machine`

**Who:** Thomer, overruling an auditor's recommendation during the `4698964` sweep.

**Context:** rule `machine-state-single-writer` says a fact about the machine belongs to
`Machine`, owned in one place. An audit applied that rule to `AppState.IsWorkZeroSet` and
recommended moving it into `Machine`, or deriving it from GRBL's reported G54 offset.

**Rejected, deliberately.** GRBL persists G54 in **EEPROM**. It survives a power cycle, so
after a reconnect the controller will report a work origin that was set for a
different board, in a different session, possibly weeks ago. Deriving "is the work zero
set?" from GRBL's own state therefore answers *"is a number stored?"* — never
*"does that number still describe the board currently on the table?"* It would always
answer yes, which is the unsafe answer.

`IsWorkZeroSet` is not a fact about the machine. It is a fact about **this session's
operator having asserted an origin for the workpiece in front of them**. That is why it
lives in `AppState`, why startup asks "trust the previous session's zero?" rather than
reading it, and why a disconnect clears it (see
`2026-08-stale-work-zero-and-height-map`) — a disconnect means the machine may be
repositioned or power-cycled before it returns, so the operator's assertion expires even
though GRBL's EEPROM value does not.

**Lesson.** `machine-state-single-writer` governs facts the *machine* owns. A fact the
*operator* asserted about the *workpiece* is session state, and moving it into `Machine`
would replace a question the operator answers with a value the firmware remembers. Before
applying the rule to a new flag, ask which of the two it is. The same reasoning covers the
height map: it is trusted only against a recorded `ProbeContext`, never because a file
exists.

**Touches:** rule `machine-state-single-writer`, `coppercli/AppState.cs`,
`coppercli/SessionRestore.cs`, `coppercli.Core/GCode/ProbeContext.cs`.
