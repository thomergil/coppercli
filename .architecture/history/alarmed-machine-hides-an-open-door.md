# An alarmed machine with the door open never reports Door

**Date:** 2026-09-25

**Problem:** At startup the terminal asked "Home machine?". With the enclosure open, it
returned within about a second without homing. The log showed five
"GRBL rejected $X: error:13" lines in that second.

**Cause:** GRBL 1.1 does not enter `Door` from `Alarm`. It stays in `Alarm` and refuses
both `$X` and `$H` with `error:13` until the door closes. In GRBL's `system.c`, both
commands check `system_check_safety_door_ajar`; in `protocol.c`, the door event is skipped
when the state is `STATE_ALARM`. The only reliable sign of the open door is that answer.
`Pn:D` in the status report depends on the board. The flow sent `$X` in a retry loop
bounded by `MachineClearAttempts`, with no delay beyond `CommandDelayMs`, then homed. It
used up every attempt on `error:13` before anyone could close the door. The unlock was
also unnecessary, because GRBL accepts `$H` directly from `Alarm`.

**Fix:** `GrblRejection.DoorOpen = 13`. `MachineWait.UnlockAsync` reads the `GrblReply` for
`$X`. `MachineWait.DescribeRefusal` is the one place a refusal becomes the operator's
words. `HomingOutcome` stores the `GrblRejection` and derives `DoorOpen`, `Reason` and
`FailureMessage` from it. `HomeAsync` takes an optional `retryAfterDoorCloses` callback that
asks the operator and sends `$H` again. The terminal's startup flow homes without unlocking
first. It waits out an actual `Door` state with
`MenuHelpers.WaitForDoorClear(machine, DoorPrompts.ScrollingConsole)` and asks its yes/no
questions with `MenuHelpers.ConfirmOrExit`. Full-screen views use `DoorPrompts.Overlay`, the
default. Tests: `SafetyCheckTests.Home_RefusedAtAnOpenDoor_*`,
`SafetyCheckTests.Unlock_RefusedAtAnOpenDoor_SaysSo`,
`WebServerSequenceTests.UnlockAndHome_WhileAlarmedWithTheDoorOpen_SayTheDoorIsOpen`.

**Rejected:** Retrying `$X` on a count. Every retry gets the same `error:13` until a person
closes the door, so a count only sets how fast the attempts run out.

**Lesson:** Read the error code of a refused command. A status report says nothing
about an open door while the machine is alarmed.

**Rule:** `grbl-answers-its-own-commands` (extended).

Touches: `GrblRejection`, `MachineWait.UnlockAsync`, `MachineWait.DescribeRefusal`,
`MachineWait.HomeAsync`, `HomingOutcome`, `DoorPrompts`, `MenuHelpers.WaitForDoorClear`,
`ConnectionMenu`, `controllers → machine`, `door-policy-defined-in-machinewait`.
