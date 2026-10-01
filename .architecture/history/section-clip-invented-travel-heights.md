# The section clip crossed the board at heights the file never used

**Date:** 2026-09-30 · **Found by:** the security review and the skeptic audit of the sections
change, and the owner's merged pcb2gcode files (e.g. `/mnt/cnc/piano-led_000_all.ngc`).

**Problem:** When a section is left out, the tool must travel from the end of one kept stretch
to the start of the next, over copper the file never crossed at that point. `SectionClip`
adds that travel, and each height it first used was wrong for some file.

**Tried and dropped, in order:**
1. Crossing at the file's latest level rapid. A CAM file with low links between cuts and a
   high clearance plane would cross the board low (security review).
2. Crossing at the highest crossing so far. That crosses a left-out region lower than a
   height the file uses later with the same tool (skeptic).
3. Crossing at the whole file's highest crossing. The owner's merged pcb2gcode files cross at
   Z35 after each tool change, so every return to an isolation cut would climb 34 mm.
4. Coming down from where the file's last plunge started. That can be Z0, so the tool would
   rapid down onto the copper.
5. Before a command that is not a move (M5, M6, end of file), traveling to the file's X and
   Y. That took the tool to drill holes in left-out sections.
6. Writing a move whose start the file never gave (after a pass-through block or a refused
   G28) as the file wrote it, even when it ends below Z0. Nothing says which sections that
   move crosses.

**Fix:** Every height comes from the file, one stage at a time. A stage ends at a tool change
(M6) or a pass-through block (G53, G10, G92, G43.1, G38).
- Crossing: the stage's highest level move across the board that does not cut, whether
  rapid or feed; with none, the stage's highest Z. It must be above Z0, or the clip is refused
  (`ErrorSectionsNoTravelHeight`). The tool never crosses below the Z of the point it travels to.
- Approach: the end of the file's last rapid down in the stage that ended above Z0. The tool comes down
  at rapid speed to there and feeds the rest of the way at the file's plunge feed.
- Before a command that is not a move, the tool rises in place to the file's Z. A pass-through
  block acts where the tool is, so the tool travels to the file's position first, and the clip
  is refused when that position is inside a left-out cut (`ErrorSectionsBlockInLeftOutCut`).
- A move with no known start that ends below Z0 is refused
  (`ErrorSectionsCutWithUnknownStart`).

**Also learned:** a random-board property test with one crossing height could not tell these
rules apart; every candidate passed it. `KeepSections_HoldsOnRandomBoards` now retracts to
more than one height (`RandomRetractHeights`).

**Lesson:** A transform that adds travel takes its crossing and approach heights from the
file's own moves in the current stage, and refuses when the file gives none. It never picks a
height of its own. Test it on boards that cross at more than one height.

**Rule:** `inserted-travel-uses-the-files-heights` (promoted).

Touches: `SectionClip`, `GCodeFile.KeepSections`, `Constants.ErrorSectionsNoTravelHeight`,
`Constants.ErrorSectionsBlockInLeftOutCut`, `Constants.ErrorSectionsCutWithUnknownStart`,
`coppercli.Tests/BoardSectionsTests.cs`, `inserted-travel-uses-the-files-heights`,
`machine-gcode-built-in-one-place`, `tests-detect-plausible-defects`.
