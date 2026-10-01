# A section run brought the tool down over a section the operator had left out

**Date:** 2026-09-30 · **Found by:** Thomer (owner), at the machine. He chose the bottom-left
section; the tool came down at the bottom right, and he switched the machine off on its way
down. His words: "It should first travel X, Y before travelling down, otherwise I guarantee
you the user will abort/turn off the machine."

**Cause:** The clip held the file's opening travel and copied it when the first kept cut
started where the file's own path started. A file's opening moves come before it has said
where X and Y are (`G0 Z1` with no start), so the tool came down at wherever it was, over a
left-out section, and only then moved across.

**Tried and dropped:**
- Copying the file's opening, on the grounds that a run of the whole board does the same. The
  operator expects a run of part of the board to go to that part first, and a descent anywhere
  else reads as a fault.
- Before a spindle start or a dwell after a cut, writing all the held travel to take the tool
  out of the copper. That travel led to the next cut in the file, which could be left out, so
  the tool went to a drill hole in a left-out section.

**Fix:** If the tool's position is unknown, the tool rises to the machine's safe height
(`G53 G0 Z-1.000`, the same line as the run's own safety retract), moves in X and Y, then comes
down over the kept cut. The clip copies the file's travel only when the tool's position is known or
it keeps the whole board (`PartClip.CanCopyTheFile`). Before a command that needs the tool out
of the copper, the clip writes the held travel only up to the first move that clears the
surface (`LeaveTheCopper`). The log names each chosen section and the area it covers.

**Lesson:** A run of part of the board must look deliberate to the person watching it: up,
across, then down over work it is about to do. Never write a move whose effect depends on an
unknown start position when some sections are left out.

**Rule:** `part-run-descends-only-over-its-work` (promoted).

Touches: `PartClip`, `GCodeFile.KeepPart`, `PartClip.MachineSafeHeightLine`,
`coppercli.Tests/BoardSectionsTests.cs`, `coppercli.Tests/JobPhasesTests.cs`,
`inserted-travel-uses-the-files-heights`.
