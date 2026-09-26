# The "$22 is off" wording applies only to $H

**Date:** 2026-09-25

**Problem:** A reviewer proposed moving the `error:5` wording ("homing is disabled, set
`$22`") from `HomingOutcome.Reason` into `MachineWait.DescribeRefusal`, where the other
refusals are worded. The move broke
`WorkZeroOutcomeTests.AnOffsetRefusedForAnotherReason_ReportsWhatGrblSaid`.

**Cause:** GRBL's `error:5` means only "setting disabled". It says `$22` is off only when
it answers `$H`. Any other command that GRBL refuses with `error:5` would get a wrong
explanation.

**Fix:** The move was reverted. `HomingOutcome.Reason` keeps the `$22` wording.
`DescribeRefusal` keeps GRBL's own text for `error:5`.

**Lesson:** Word a refusal in `DescribeRefusal` only when the error code alone determines
the meaning. Otherwise word it where that command's outcome is built.

**Rule:** `grbl-answers-its-own-commands`.

Touches: `HomingOutcome.Reason`, `MachineWait.DescribeRefusal`, `GrblRejection`.
