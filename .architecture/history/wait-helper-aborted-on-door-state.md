# The wait helper aborted while the machine reported Door

**Problem:** A door prompt reappeared within milliseconds of being answered, so it looked
stuck rather than failed, and nothing reported an error.

**Cause:** `MillingController.EnsureDoorClosedAsync` asked the operator to close the
enclosure, sent CycleStart, then called `MachineWait.WaitForIdleAsync`. `WaitForIdleAsync`
treats Door as `IsProblematic` and returns false on its first poll, so it can never succeed
while the machine is still holding at the door, which is the only situation in which anything
calls it. The loop then saw Door again and re-prompted. The helper returns an ordinary false
and the caller's retry hides it.

**Fix:** Two waits built for the door. `MachineWait.WaitForDoorClosedAsync` waits for GRBL to
stop reporting the door open, tolerating the substate arriving a poll late;
`WaitForDoorReleasedAsync` waits for the hold itself to lift after CycleStart. Neither treats
Door as a reason to give up. Fixed in the session after `168392e`.
`coppercli.Core/Controllers/MachineWait.cs`,
`coppercli.Core/Controllers/MillingController.cs`, `coppercli.Tests/MachineWaitTests.cs`;
rules `waits-do-not-abort-on-awaited-state`, `manual-door-release-and-required-homing`; interface
`controllers → machine` v1 → v2.
Names have since changed: `IsProblematic` is `MachineWait.IsUnavailable`, and Hold is not in
it — `NeedsAttention` is the door states, Alarm and Sleep. `WaitForDoorClosedAsync` is gone;
the door waits are `ReleaseDoorHoldAsync` and `WaitForDoorStateChangeAsync`.

**Rule:** Compare a wait helper's abort conditions with the state it is waiting to leave. A wait for the door to release must permit Door until GRBL reports another state.
