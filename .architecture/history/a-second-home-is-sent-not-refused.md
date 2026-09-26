# A second Home is sent, not refused

**Date:** 2026-09-25 · **Decided by:** Thomer (owner)

**Decision:** In the owner's words: "When the user says Home, you Home, even if it comes
right after the first home. The user may have their reasons. Don't try to be smarter than
the user." A Home sent while another homing cycle runs is passed to GRBL, which queues it.

**Cause of the change:** `IMachine.IsHoming` was a flag. A second `HomeAsync` finishing first
cleared it while the other cycle still ran, so the machine read as free during a cycle.

**Fix:** `IsHoming` is derived from a count. `BeginHoming` and `EndHoming` are called only
from `HomeAsync`'s `try`/`finally`, so the machine reads as homing until the last cycle ends.
On connect and disconnect, `AbandonOutstanding` completes any pending `$H`, so its `finally`
runs and the count returns to zero. Tests:
`SafetyCheckTests.Home_DuringAnotherCycle_IsSentAndHomingLastsUntilBothEnd`,
`Machine_StaysHoming_UntilTheLastCycleEnds`, `Home_WhoseRetryQuestionThrows_IsNoLongerHoming`.

**Rejected:**
- An atomic claim that refused the second Home. The owner rejected it (above).
- A `ClearHoming` reset on connect and disconnect. An `EndHoming` from a cycle that started
  before the reset could then run late and cancel out a new cycle's `BeginHoming`, leaving
  the new cycle marked not homing. Completing the pending `$H` makes each cycle lower the
  count for itself.

**Lesson:** Do not refuse a command the operator asked for because coppercli thinks it is
redundant. Track concurrent cycles with a count that each cycle lowers in its own
`finally`, never with a flag or an external reset.

Touches: `IMachine.IsHoming`, `IMachine.BeginHoming`, `IMachine.EndHoming`,
`MachineWait.HomeAsync`, `Machine.AbandonOutstanding`, `controllers → machine`.
