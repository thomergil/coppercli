# The job-origin warning, and warnings dropped by derived files

**Date:** 2026-09-29 · **Asked for by:** Thomer (owner), after the outline trace in
`trace-confirmed-moves-by-idle` ran left of work zero. In the owner's words, a warning like
"Origin is too far from something something".

**Problem:** A job whose work zero is not at its lower-left corner was loaded, probed and
traced with no warning. Separately, no warnings at all reached the pre-mill check of a
height-mapped job, which is the usual case.

**Cause:** Nothing checked the job's extent against work zero. And the files derived from
another file (`ApplyProbeGrid`, `Split`, `ArcsToLines`, `RotateCW`) were built without the
parser's warnings, so a height-mapped file had none.

**Fix:** `GCodeFile` adds a DANGER origin warning, listed first, when the job reaches more
than `Constants.JobOriginToleranceMm` (5 mm) left of or below work zero. A derived file keeps
the warnings of the file it came from. `GCodeFile.WarningsToConfirm` (DANGER and INCHES, by
prefix) is the one filter for what the operator must confirm; `MillStartCheck.FileWarnings`
replaced `MillStartCheck.DangerousWarnings` and `MillWarning.DangerousCommands`. Both UIs
ask the operator to confirm `WarningsToConfirm` before probing, tracing and milling. The
browser also shows them at load, from `FileSummary.warningsToConfirm`.

**Left alone, deliberately:** the server does not refuse a start on unconfirmed warnings.
The confirmation happens in each UI.

**Lesson:** A value computed while parsing is lost when a transform builds a new
`GCodeFile`. Carry it through every transform, and test the warning on a derived file, not
only on the parsed one (`GCodeFileWarningsTests.ADerivedFile_KeepsTheParsersWarnings`).

**Rule:** none promoted; a derived `GCodeFile` is not checked against a setup, so
`derived-artifact-records-its-context` does not cover it.

Touches: `GCodeFile.Warnings`, `GCodeFile.WarningsToConfirm`, `GCodeFile.ApplyProbeGrid`,
`GCodeFile.Split`, `GCodeFile.ArcsToLines`, `GCodeFile.RotateCW`,
`Constants.JobOriginToleranceMm`, `MillStartCheck.FileWarnings`,
`MenuHelpers.ConfirmFileWarnings`, `FileSummary.warningsToConfirm`, `web → browser`.
