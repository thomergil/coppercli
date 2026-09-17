# A wait that bailed out on the state it was waiting out

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
rules `no-bail-out-on-the-awaited-state`, `never-auto-clear-a-safety-gate`; interface
`controllers → machine` v1 → v2.
Names have since changed: `IsProblematic` is `MachineWait.IsUnavailable`, and Hold is not in
it — `NeedsAttention` is the door states, Alarm and Sleep. `WaitForDoorClosedAsync` is gone;
the door waits are `ReleaseDoorHoldAsync` and `WaitForDoorStateChangeAsync`.

**Rule:** A wait helper whose bail-out condition includes the state the caller is waiting to
leave can never succeed. Check a wait's bail-out set against the state you are starting from
before reusing it; `IsUnavailable` in particular is a bail-out set, and the door states are in
it because most callers are not waiting them out. The fix is a wait that admits the door
state, never code that clears it.
