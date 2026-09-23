# A status report read as the answer to a command

**Problem:** An operator opened the enclosure while the machine was homing before a mill
run, pressed Escape, closed the door and went back into the Mill screen. Three things went
wrong. The status still read DOOR OPEN. A fresh run asked "Door closed. Continue?" before
anything had started. Starting the mill then failed at once.

**Cause:** Each bug came from reading `Status` to learn whether a command had worked.
GRBL answers no status query during a homing cycle or while it restarts after a soft reset,
so the reading taken after a command was often older than the command. The stop sent during
homing raised ALARM:6. Nothing cleared the alarm, so every line sent after it was refused.
The door prompt came from an enclosure answer that was never passed into the run.

**Fix:** `IMachine.SendAsync` returns GRBL's own answer to each line (`GrblReply`). Each
answer is matched to its line by queue order. `HomeAsync` sets `IsHomed` only on the `ok` for
`$H`. `StopAndResetAsync` sends `$X` every time, held until GRBL's banner shows it is back.
GRBL's banner abandons whatever it held and clears `IsHomed`. An alarm raised by that reset
leaves the queued `$X` in place, because the reset already abandoned everything queued before
it. The terminal passes the operator's enclosure answer to the run through
`MillingOptions.EnclosureConfirmed`. The web server does not receive the browser's answer, so
it passes false. `ControllerBase.StopAsync` cancels a live run and waits for it rather than
cleaning up alongside it. Files: `coppercli.Core/Communication/Machine.cs`,
`coppercli.Core/Communication/GrblReply.cs`, `coppercli.Core/Controllers/MachineWait.cs`,
`coppercli.Core/Controllers/ControllerBase.cs`, `coppercli.Tests/FakeGrblTests.cs`. Rule:
`grbl-answers-its-own-commands`. Interfaces: `controllers → machine` v5,
`machine → GRBL` v4, `ui → controllers` v6.

**Rejected:** Waiting for a number of fresh status reports after a command. GRBL sends none
while it is busy, so the wait either times out or reads a report from before the command.

**Rule:** Read whether a command ran from GRBL's answer to that command, never from a status
report taken afterwards.
