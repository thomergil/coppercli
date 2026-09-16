# 2026-09 — The door prompt that re-asked within milliseconds of being answered

**Who:** Thomer, reporting a door prompt that reappeared the instant it was answered; fixed
with Claude Opus 5 in the session after `168392e`.

**Tried:** `MillingController.EnsureDoorClosedAsync` asked the operator to close the
enclosure, sent CycleStart, and then called `MachineWait.WaitForIdleAsync` to wait for the
machine to reach Idle.

**Believed:** waiting for Idle is how you wait for the machine to be ready again, so it is
the right helper after a door hold.

**Realized:** `WaitForIdleAsync` treats Door as `IsProblematic` and returns false on its
first poll. It can never succeed while the machine is still holding at the door — which is
the only situation in which anything calls it. The loop then saw Door again and re-prompted
within milliseconds, so the prompt appeared to be stuck rather than to have failed. The
helper returns an ordinary false and the caller's retry hides it, so nothing reports an
error.

Fixed with two waits built for the door: `MachineWait.WaitForDoorClosedAsync` waits for GRBL
to stop reporting the door open (tolerating the sub-state arriving a poll late), and
`WaitForDoorReleasedAsync` waits for the hold itself to lift after CycleStart. Neither
treats Door as a reason to give up.

**Lesson → new rule `no-bail-out-on-the-awaited-state`.** A wait helper whose bail-out
condition includes the state the caller is waiting to leave can never succeed. Before
reaching for an existing wait, check its bail-out set against the state you are starting
from; `IsProblematic` in particular is a bail-out set, and Door and Hold are in it because
most callers are not waiting them out. Pair this with
`never-auto-clear-a-safety-gate`: the fix is a wait that admits the door state, never code
that clears it.

**Touches:** `controllers → machine` (v1 → v2), rules `no-bail-out-on-the-awaited-state`,
`never-auto-clear-a-safety-gate`, `coppercli.Core/Controllers/MachineWait.cs`,
`coppercli.Core/Controllers/MillingController.cs`, `coppercli.Tests/MachineWaitTests.cs`.
