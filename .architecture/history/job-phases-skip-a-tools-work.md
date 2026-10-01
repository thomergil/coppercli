# Skipping a phase of a job, to cut a board out without milling its traces again

**Date:** 2026-10-01 · **Asked for by:** Thomer (owner). After re-milling chosen sections he
needed to run only the cutout. Loading the outline file instead drops the height map, so he
proposed choosing "phases", split at the tool changes.

**Decided:** A phase is one tool's work (`JobPhase`). The first runs from the start of the
file; each later one from the last tool change (M6) between two cuts (`JobPhase.StartsIn`). The
operator chooses phases on the pre-mill screen (P in the terminal, checkboxes in the browser,
shown only when `GCodeFile.OffersAChoiceOfPhases`). `ChosenPhases` is a fifth input to
`MachineFileBuild`, applied by `KeepPart` with the sections, so it gets the version check, the
lock, the refusal during a run and the reset in `ClearJobCorrections`. A skipped phase loses
its moves (a move in machine coordinates among them), its M6, its spindle starts, dwells and
pauses; it keeps spindle stops, the program end and settings such as S and T.

**Tried and dropped:**
- An M6 always starts a phase. Fusion writes `T1 M6` before its first cut, which made an
  empty phase 1.
- Labeling a phase with its tool number. The tool-change prompt finds the tool by its own
  rule (`GCodeParser.FindToolInfo`, near the M6 line); a second rule over parsed commands
  would name a different tool in some files. The label shows the phase's number and time.
- Writing the run's rise to safe height before the file's pause right after a kept M6.
  `MillingController.FindRedundantM0` skips only an M0 that comes straight after the M6, so the
  operator was stopped a second time, by a pause whose words no longer applied. The clip writes
  nothing after a tool change until a move needs it.
- Dropping a skipped phase's spindle start while a later phase relies on it. Nothing in
  `ToolChangeController` starts the spindle, so that phase would cut with it stopped. The
  choice is refused (`ErrorPhaseSkipsTheSpindleStart`) rather than starting the spindle
  without the file's spin-up dwell.

**Also changed:** both UIs draw the sections picture from `AppState.CellsCut`, so it shows
only the chosen phases' cuts. The server makes depth, sections and phases changes through
`CncWebServer.WriteJobChange`, and the browser sends them through `mill.js` `sendJobChange`;
before, each kind of change had its own copy of that code.

**Renamed and removed:** `SectionClip` became `PartClip` and `GCodeFile.KeepSections` became
`KeepPart`, since the clip now keeps phases as well as sections. `Motion.StartTrusted` and
the unused `GCodeFile.Split`, `ArcsToLines` and `RotateCW` are gone.

**Lesson:** Skipping work must not drop what a later part of the file relies on: an offset or
probe block, or the spindle state. Refuse the choice when a skipped phase holds such a thing
and the clip cannot supply it.

**Rule:** `skipped-phase-keeps-what-later-phases-rely-on` (promoted);
`machine-gcode-built-in-one-place` (five inputs); `part-run-descends-only-over-its-work`
(after a tool change); web → browser v14.

Touches: `JobPhase`, `ChosenPhases`, `PartClip`, `GCodeFile.KeepPart`,
`GCodeFile.OffersAChoiceOfPhases`, `AppState.ChooseMillPhases`, `AppState.CellsCut`,
`SectionClip`, `GCodeFile.KeepSections`, `MillingController.FindRedundantM0`, `web → browser`,
`machine-gcode-built-in-one-place`,
`part-run-descends-only-over-its-work`, `loaded-gcode-unchanged-during-run`,
`coppercli.Tests/JobPhasesTests.cs`, `coppercli.Tests/PhasesEndpointTests.cs`.
