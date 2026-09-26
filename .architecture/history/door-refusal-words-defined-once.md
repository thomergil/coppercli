# The words for a door refusal are defined once

**Date:** 2026-09-26

**Problem:** Adding `DoorState.Retracting` meant every place that refused a resume or a door
release at the door had to choose words for the new state. The terminal resume, the mill
restart and several web endpoints each made that choice separately. `GetResumeBlocker`
was one of these; it was an alias and added no mapping of its own.

**Fix:** `MachineWait.GetDoorRefusal`, with a `DoorState` overload and an `IMachine`
overload, is the one mapping from a door state to refusal words:
- `Open` returns `ErrorDoorBlocksResume` ("Holding at the door. Close it.").
- `WaitingForResume` returns `ErrorDoorClosedStillHolding` ("Door closed, but the machine is
  still holding.").
- `Retracting` and `Resuming` return their progress message from `GetDoorMessage`.
- `None` returns null.

Its callers are the resume in `ControllerBase`, `MillingController.DescribeRestartFailure`,
and the resume, mill-resume and door-release paths in `CncWebServer`. `GetResumeBlocker`
was removed.

**Rejected:** Wording the `Door:0` refusal as "Answer the door prompt to continue". That is
false for a paused mill run, because no door prompt exists then (see the GAP about a door
hold during a pause under `controllers → machine`). The `Door:0` refusal must not tell the operator
to do anything.

**Lesson:** Refusal words are part of the door policy. Map each `DoorState` to them in one
function in `MachineWait`, next to `GetDoorMessage`. A refusal that names an action is
wrong for any caller where that action does not exist.

**Rule:** `door-policy-defined-in-machinewait`.

Touches: `MachineWait.GetDoorRefusal`, `ControllerBase`, `MillingController`,
`CncWebServer`, `ControllerConstants`, `controllers → machine`.
