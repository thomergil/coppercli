# One clearance height under three names

**Problem:** Stopping a job at the enclosure retracted the tool as the operator cleared the
door. One clearance height existed as three constants, `MillCompleteZ`, `MillStartSafetyZ`
and `ToolChangeClearanceZ`, all `-1.0` and all documented as "1mm below the top, clear of the
limit switch"; nothing recorded whether they were meant to hold the same value, so none of
them could be changed on purpose. Two checks asking whether the machine could start a job
listed different states: probing refused an alarm or a sleeping machine, milling only an
alarm.

**Cause:** The derived answers about the machine had been given one owner in Core
(`browser-status-handler-threw-on-every-message.md`, `door-state-checked-in-ten-places.md`),
but the constants and moves those answers are built from had not. The move to the clearance
height was written five times: twice as a confirmed retract, three times as a raw `G53 G0 Z`
with its own wait. The rule that a machine holding at the door must not be sent one — GRBL
keeps the move in its planner and runs it when the hold is released — was in two of them and
missing from `MillingController.CleanupAsync`. Whether a prompt was about the enclosure was
recovered by matching its text. Found on a sweep for duplicate logic and duplicate state
across everything changed since v0.4.2.

**Fix:** The three constants are `SafeClearanceZ`. `ControllerBase.RetractToSafeZAsync` holds
the door rule, and `MachineWait.StopAndResetAsync` returns whether the machine was at the
door, because its own soft reset clears that state and the caller cannot read it afterwards.
`MenuHelpers.GetMachineBlocker` answers the start check once for both.
`UserInputRequest.IsDoorPrompt` carries the enclosure fact from where the prompt is raised.
`coppercli.Core/Util/Constants.cs`, `coppercli.Core/Controllers/ControllerBase.cs`,
`coppercli.Core/Controllers/MachineWait.cs`,
`coppercli.Core/Controllers/MillingController.cs`,
`coppercli.Core/Controllers/UserInputRequest.cs`, `coppercli/Helpers/MenuHelpers.cs`; rule
`one-field-per-fact`.

**Rule:** An answer with one owner can still be built from facts that have several. For each
literal and each move in changed code, ask whether it could legitimately differ from the
others, and name it where it could not. An operation that destroys state a later step needs
returns that state rather than leaving the caller to read it first. Where one rule has
several call sites with different budgets or targets, the rule takes them as parameters
instead of being copied per caller.
