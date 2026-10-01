# Every stop made the next job home first

**Date:** 2026-09-30 · **Asked for by:** Thomer (owner): "cut Homing when there's been no
traumatic event." For a crash or stall he chose to be asked "Home again?" at the next start,
yes by default.

**Problem:** Stopping a job, for any reason, cost a full homing cycle before the next one.

**Cause:** `Machine` cleared `IsHomed` on every soft reset, including the one a stop sends.
GRBL keeps its position through a reset while the axes are stopped (Idle, `Hold:0`, a
stopped door hold); it loses it only when the reset lands during motion, and then it prints
`ALARM:3` before its banner. Checked against grbl 1.1 `mc_reset` and against the owner's
logs, which showed 11 clean resets after `Hold:0` or `Door:0`. The stop sent its reset
straight after the feed hold, so the axes were often still decelerating.

**Fix:** `StopAndResetAsync` waits up to `StopHoldTimeoutMs` for a report that shows the axes
stopped and that GRBL made after it took the hold, then resets. `IsHomed` survives a reset
coppercli sends and is cleared on an `ALARM` line, on `[MSG:'$H'|'$X' to unlock]` (GRBL's sign after a
reset out of alarm or sleep), on a banner coppercli did not cause, and on connect and
disconnect. A stop or error while cutting (the Milling phase, running or paused by the
operator) sets `StoppedWhileCutting`, which `Machine` keeps in one field with `IsHomed`. The
next start asks whether to home first. `MillingController` decides whether to home when the
settle ends, so if GRBL restarts during the settle and loses the position, the job still
homes.

**Lesson:** Clear a "position known" flag on the machine's own sign that the position was
lost, not on the command that might cause it. A stall GRBL cannot see is the operator's
call, so ask rather than force a cycle.

**Rule:** `machine-state-single-writer` updated with the signs that clear `IsHomed`.

Touches: `Machine.IsHomed`, `IMachine.StoppedWhileCutting`, `MachineWait.StopAndResetAsync`,
`MillingController.CleanupAsync`, `MillingOptions.HomeFirst`, `CliConstants.HomeFirstByDefault`,
`GrblProtocol.ResponseAlarmLock`.
