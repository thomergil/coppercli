# 2026-09 — One height under three names

**Who:** Thomer, with Claude Opus 5, on a sweep for duplicate logic and duplicate state
across everything changed since v0.4.2.

**Tried:** give each derived answer about the machine one owner in Core, and have the
terminal and the browser read it. That work is in `2026-09-a-view-model-the-browser-could-
not-draw` and `2026-09-a-two-state-door-in-ten-places`.

**Believed:** the answers had been consolidated, so only the wording was left.

**Realized:** the same duplication was one level down, in the constants and moves those
answers are built from.

One clearance height existed as three constants, `MillCompleteZ`, `MillStartSafetyZ` and
`ToolChangeClearanceZ`, all `-1.0` and all documented as "1mm below the top, clear of the
limit switch". Nothing recorded whether they were meant to hold the same value, so none of
them could be changed on purpose. They are now `SafeClearanceZ`.

The move to that height was written five times: twice as a confirmed retract, three times
as a raw `G53 G0 Z` with its own wait. The rule that a machine holding at the door must not
be sent one — GRBL keeps the move in its planner and runs it when the hold is released —
was in two of them and missing from `MillingController.CleanupAsync`. Stopping a job at the
enclosure retracted the tool as the operator cleared the door.
`ControllerBase.RetractToSafeZAsync` holds the rule now, and `MachineWait.StopAndResetAsync`
returns whether the machine was at the door, because its own soft reset clears that state
and the caller cannot read it afterwards.

Two checks asked whether the machine could start a job and listed different states: probing
refused an alarm or a sleeping machine, milling only an alarm. `GetMachineBlocker` answers
it once for both.

Whether a prompt was about the enclosure was recovered by matching its text.
`UserInputRequest.IsDoorPrompt` now carries it from where the prompt is raised.

**Lesson:** an answer with one owner can still be built from facts that have several.
Ownership has to reach the constants a rule is expressed in and the operations that carry
it out, or the duplicates move down a level where nothing flags them: three constants with
one value read as three separate decisions, and the one caller missing a rule looks like a
caller that never needed it. The check that found these: for each literal and each move in
the changed code, ask whether it could legitimately differ from the others, and name it
where it could not.

Two practices came out of it. An operation that destroys state a later step needs returns
that state rather than leaving the caller to read it first. And where one rule has several
call sites with different budgets or targets, the rule takes them as parameters instead of
being copied per caller.

**Touches:** `coppercli.Core/Util/Constants.cs` (`SafeClearanceZ`),
`coppercli.Core/Controllers/ControllerBase.cs` (`RetractToSafeZAsync`),
`coppercli.Core/Controllers/MachineWait.cs` (`StopAndResetAsync`),
`coppercli.Core/Controllers/MillingController.cs` (`CleanupAsync`),
`coppercli.Core/Controllers/UserInputRequest.cs` (`IsDoorPrompt`),
`coppercli/Helpers/MenuHelpers.cs` (`GetMachineBlocker`), rule `one-field-per-fact`.
