# The park retract was read as an open door

**Date:** 2026-09-26

**Reported by:** the owner, from `coppercli.log` of 2026-09-25, 19:18:57 to 19:19:09.

**Problem:** After the operator closed the enclosure, both the terminal and the browser still
said "Close the door." for several seconds.

**Cause:** The code and a test both assumed that `Door:2`, the park retract, is GRBL's own
move away from the work and that the enclosure is still open while it runs. So `Door:2`
fell into `DoorState.Open`. In fact GRBL reports `Door:2` until the park move ends, and on
the Nomad that takes about 11 s. It keeps reporting `Door:2` after the door has closed. In
the log, `Pn:D` disappeared 2 s into the move and `Door:2` continued for another 9 s. The
substate says the machine is moving. It says nothing about the door.

**Fix:** Added `DoorState.Retracting` and `MachineActivity.DoorRetracting`. Its message is
`ControllerConstants.DoorRetractingMessage` ("Retracting..."), which is true whether the door
is open or closed. The terminal shows `CliConstants.DoorRetractingStatus` and the browser
shows `TEXT_DOOR_RETRACTING`. The browser's copy of the name,
`MACHINE_ACTIVITY_DOOR_RETRACTING`, is published in `machineActivities` and validated in
`helpers.js`. The retract is waited out and never prompted. `IsDoorOpen` still returns
true for every substate not mapped to another state, so an unrecognized substate is treated as `Open`.

**Rejected:**
- Reading `Pn:D` to tell an open door from a closed one during `Door:2`. `Pn:D` depends on
  the board (rule `grbl-answers-its-own-commands`). The neutral message is correct on every
  board, and the door-open prompt appears once GRBL reaches `Door:1`.
- An early return in `ReleaseDoorHoldAsync` for `Retracting`. It skipped the bounded wait
  for the switch reading to catch up, which `Door:2` previously got through the `Open`
  path. A stale `Door:2` report that arrived just after the retract ended then made the
  method refuse a release it should have sent. A `WebServerSequenceTests` teardown found
  it. The method now asks the private `IsDoorReleasable(state)` (`WaitingForResume` or
  `Resuming`). In any other state it waits for the bounded catch-up, reads the state again,
  and then refuses without sending a cycle start.

**Lesson:** A GRBL substate describes what the machine is doing. Before mapping it to a
prompt, check the log for how long it lasts and what the operator can do meanwhile. Before adding a
state to a function's early exits, check what the fall-through path did for that state.
Keep that behavior in the new exit.

**Rule:** `new-state-cases-update-callers`, `door-policy-defined-in-machinewait`.

Touches: `DoorState`, `MachineActivity`, `MachineWait.GetDoorState`, `MachineWait.IsDoorOpen`,
`MachineWait.ReleaseDoorHoldAsync`, `MachineWait.IsDoorReleasable`, `ControllerConstants`,
`CliConstants`, `constants.js`, `helpers.js`, `CncWebServer.GetSharedConstants`,
`controllers → machine`.
