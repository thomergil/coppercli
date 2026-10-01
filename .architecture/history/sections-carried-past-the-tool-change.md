# A section re-mill also clipped the drills and outline after the tool change

**Date:** 2026-10-01 · **Found by:** Thomer (owner), at the machine. He chose the bottom-left
section to re-mill; after the tool change the run drilled and cut the outline only in that
section, and at the adjusted depth (log: "phases all, sections 2x3: column 0 row 0", depth
-0.100, M6 at line 1196).

**Cause:** Sections and depth applied to every phase that runs. A section re-mill only makes
sense for the milling phase; the next tool's work is meant for the whole board.

**Decided (with the owner):** At a tool change after milling with sections or a depth
adjustment, the run asks Keep, Clear or Abort. Clear mills the rest of the job on the whole
board at the file's depth, with the height map still applied. coppercli clears the sections
and depth adjustment when the run ends, so the next run also mills the whole board at the
file's depth.

**How:** The machine's G-code is rebuilt while the run waits at the tool change, with the
sections and depth ending there. `JobInputs.SectionsAndDepthEnd` holds that tool change, the
cutoff, and `KeepPart` and `OffsetCutDepth` take it. Everything before the cutoff is built the same way, so the
lines already sent come out identical. `PutFileOnMachine(inputs, keepStreamed)` checks that
before the new file replaces the old, and `Machine.SetFile` continues the stream after those
lines. The milling controller calls `ISectionsAndDepth` to find out whether to ask and to end
the sections and depth, so Core holds no reference to `AppState`.

**Tried and dropped:**
- Splicing the new build's lines after the tool change onto the old lines. The machine's lines
  would then match no inputs, against `machine-gcode-built-in-one-place`.
- Deciding whether to ask from the positions the run sampled below the surface. A fast cut
  can pass between samples, and reading the G-code for a cut before the tool change gives an
  exact answer.
- Reading those cuts from the machine's G-code. A height map lifts a shallow cut above the
  surface on a board higher than its zero point, and the question was skipped. The cuts are
  read from the chosen part before the depth and the map.
- Asking before the run paused, while it was still in `MillingPhase.Milling`. A refused rebuild then counted as a stop while
  cutting, and an operator's pause during the rebuild threw. The run pauses at the tool
  change first, then asks.

**Found on the way:** the parser treated a tool change as not moving the tool. pcb2gcode
writes `G00 Z35 ( safety retract )` after the M6, at the height the file was already at, so
it was dropped as a move of zero length. Without a tool setter the tool waits just above the
surface after the operator sets Z0, and the next rapid crossed the board there instead of at
35 mm. `GCodeFile.GetGCode` also left out
the first feed after a tool change when it matched the one before, although the tool
setter's probe had changed the machine's feed. A tool change now makes the position unknown,
as a G53 block does, and `GetGCode` writes the feed again after it. A feed move with an
unknown start got no height-map correction; it now has the map's height added at its end. A tool change
on a line with G28, G30, G53, a probe or an offset is refused at load. The machine holds back
a tool change line whole, so a move or probe on it would never run, and the parser drops a
G28 or G30 line, taking the tool change with it. With a map, the first rapid after a tool
change still started where the tool was before it, so the writer left out the axes that had
not changed; the map now takes an unknown start from the move itself.

**Found in review:** a pause and resume while the machine still ran the moves sent before a
tool change restarted the stream past it, so the next tool's work ran with the old tool.
`Resume` now releases the hold only when the stream stopped at a pause line the run has not
handled, and the terminal's resume key follows the same rule as the browser's
(`MillingController.OperatorMayResume`). A stop during the rebuild no longer starts the tool
change. The clear at the run's end also runs when the tool change's own run ends, since a
stop can end the milling run first.

**Lesson:** A choice made for one tool's work must not silently carry to the next tool's
work; ask at the tool change. A tool change can move the tool and change the machine's feed,
so nothing after it may rely on the position or the feed from before it.

**Rule:** `loaded-gcode-unchanged-during-run` (one exception added);
`machine-gcode-built-in-one-place` (the cutoff input); ui → controllers v7 (prompt options,
`ISectionsAndDepth`, `OperatorMayResume`); web → browser v15 (choice buttons).

Touches: `MillingController`, `MillingController.Resume`, `MillingController.OperatorMayResume`,
`ISectionsAndDepth`, `UserInputRequest.Options`, `CncWebServer` `ApiMillResume`, `MillMenu`,
`AppState.EndSectionsAndDepthAt`, `AppState.SectionsOrDepthApplyAfter`,
`AppState.ClearEndedSectionsAndDepth`, `AppState.PutFileOnMachine`, `JobInputs`,
`Machine.SetFile`, `GCodeFile.KeepPart`, `GCodeFile.OffsetCutDepth`,
`GCodeFile.CutsBeforeToolChange`, `GCodeFile.ApplyProbeGrid`, `GCodeFile.GetGCode`, `PartClip`,
`GCodeParser`, `MenuHelpers.ShowPromptOverlay`, `DisplayHelpers.ShowOverlayChoice`,
`mill.js` `renderUserInputPrompt`, `ui → controllers`, `web → browser`,
`loaded-gcode-unchanged-during-run`, `machine-gcode-built-in-one-place`,
`coppercli.Tests/SectionsAndDepthAtToolChangeTests.cs`, `coppercli.Tests/MillingControllerTests.cs`,
`coppercli.Tests/GCodeParserM6Tests.cs`, `coppercli.Tests/MachineSetFileTests.cs`.
