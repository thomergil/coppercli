# The settle before a job was a fixed five seconds

**Date:** 2026-09-30 · **Asked for by:** Thomer (owner): "cut the 5-second settling. But do
not compromise safety."

**Problem:** Every job waited 5 s before it moved, however still the machine already was.

**Cause:** The settle waited for Idle and then slept `PostIdleSettleMs` in case GRBL was only
Idle between two moves. The delay stood in for the question "has the machine stayed
stopped".

**Fix:** `SteadyIdle` answers that question directly: the machine has reported Idle for
`IdleSettleMs` (1 s) without a break, and any other reading starts the count again.
`MachineWait.WaitForSteadyIdleAsync` waits for it and stops at a door hold (the operator is
asked through `EnsureDoorClosedAsync`). It waits out an alarm or sleep until
`SettleTimeoutMs`, then reports which of the two the machine is in. The check that a job has
finished reads the same `SteadyIdle`.

**Lesson:** A fixed delay after a state is reached is a guess at how long the state must
hold. Measure the hold instead. Every check that asks whether the state has held reads that
one definition.

**Rule:** none promoted. The new wait is `WaitUntilAsync` with a predicate, as the helper
list in CLAUDE.md requires, and it stops at the door rather than treating it as done, per
`waits-do-not-abort-on-awaited-state`.

Touches: `SteadyIdle`, `MachineWait.WaitForSteadyIdleAsync`, `MillingController.SettleAsync`,
`Constants.IdleSettleMs`.
