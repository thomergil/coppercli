# The outline trace confirmed its moves by Idle

**Date:** 2026-09-29

**Reported by:** the owner, who powered the machine off during an outline trace.

**Problem:** The trace moved up, then 60 mm left into the machine frame, then started to
descend.

**Cause:** Two faults.
- The file was a pcb2gcode back side with an empty outline layer. It spanned X -59.7..1.3,
  so work zero was at the board's right edge and the trace went left of it. See
  `job-origin-warning-dropped-by-derived-files`.
- `ProbeController.TraceOutlineCoreAsync` confirmed each move with
  `MachineWait.WaitForIdleAsync`. That wait passes at once after a move is sent, because
  GRBL reports Idle until it starts the move. The log shows the descent sent 6 ms after the
  rapid. Every wait's result was also ignored. After the port dropped, the trace logged all
  four corners into a dead port and failed later, at the retract, with the wrong message.
  The grid probe's move to its first point and `ToolChangeController`'s setter, work-area
  and return moves had the same unconfirmed wait.

**Fix:** A controller confirms a move it sends by arrival: Idle and at the target on the axes
the move names. `MoveTarget` holds the target; `ToGCode` and `Send` build the line from it,
so the line sent and the position waited for cannot disagree.
`MachineWait.WaitForArrivalAsync` waits for it. `ControllerBase.MoveAndConfirmAsync` sends
the move and waits. When the wait fails, it acts on what the machine reports:
- Door: `EnsureDoorClosedAsync`, then wait again, because GRBL finishes the move after the
  release.
- Feed hold: `MachineWait.WaitForHoldToEndAsync`. This wait has no timeout by design; the
  operator either resumes or stops.
- Still `Run`, and `StatusReportCount` has advanced since the last check: wait again.
- Anything else: throw `ErrorMachineNotResponding` if `MachineWait.IsUnavailable`,
  otherwise `ErrorMoveNotConfirmed`.

Two callers keep a bool result and do not throw: `MachineWait.SafetyRetractZAsync`, the Z-only
first retract of a run, whose callers throw on false; and a controller's lift after a stop
(`ProbeController.RaiseZToSafeHeightAsync`), where the machine has been reset. Every lift
inside a run goes through `MoveAndConfirmAsync` (`ProbeController.LiftToSafeHeightAsync`).
`MachineWait.WaitForZHeightAsync` was removed.

**Rejected:**
- `WaitForStatusChangeAsync(Idle)` before `WaitForIdleAsync`, which the trace's corner loop
  did. It races the first status report, and its result was ignored too.
- A per-move timeout from distance divided by feed. An operator who lowers the feed override
  then aborts the trace. "Wait again while GRBL reports `Run` and keeps reporting" replaced it.
- Making `WaitForArrivalAsync` return early on Hold while bool-returning lifts inside a run
  still called it directly. A feed hold during the final retract then failed the run at
  once. That is why every lift inside a run now goes through `MoveAndConfirmAsync`.

**Left alone, deliberately:** `ToolChangeController`'s first `WaitForIdleAsync`, which waits
for the file's own buffered moves before reading the return position. An `M0` in the file can
leave GRBL in Hold, so making that wait throw could break tool changes. Machine-travel
limits are not checked.

**Lesson:** Idle does not confirm a move: it is also the state before the move starts.
Confirm by position and state together, and never discard a wait's result. A move timeout
cannot be derived from feed, because the operator changes the feed override during the move.
Track whether GRBL is still reporting instead.

**Rule:** `moves-confirmed-by-arrival`, `fail-safe-on-uncertainty`,
`waits-do-not-abort-on-awaited-state`.

Touches: `MoveTarget`, `MachineWait.WaitForArrivalAsync`, `MachineWait.WaitForHoldToEndAsync`,
`MachineWait.WaitForZHeightAsync` (removed), `MachineWait.SafetyRetractZAsync`,
`ControllerBase.MoveAndConfirmAsync`, `ProbeController.TraceOutlineCoreAsync`,
`ProbeController.LiftToSafeHeightAsync`, `ProbeController.RaiseZToSafeHeightAsync`,
`ToolChangeController`, `ControllerConstants.ErrorMoveNotConfirmed`,
`controllers → machine`.
