# After a block written verbatim, the parser and the G-code writer kept using values from before it

**Date:** 2026-10-01 · **Found by:** the real outline files in `/mnt/cnc` run through the clip
with sections chosen, and the skeptic audit of the phases change.

**Problem:** A `PassThrough` block (G53, G92, G10, G43.1, G38.x) moves the machine or changes
its frame in ways the parser does not model. Three places kept using values from before it:
- The zero-length check dropped a move whose start equaled its end. After a block the parser's
  `Start` still held the Z from before it. With Z35 before the block, the check read the second
  line of `G0 X1 Y1` then `G0 X1 Y1 Z35` as a move to where the tool already was, and dropped
  it. `StartTrusted` marked only the first move after the block as having an unknown start.
- A `G0` or `G1` that names no axis (`G01 F600`) became a move with no axes. The zero-length
  check had dropped it, because its start was taken as known and equal to its end. Once the
  check required a start known on every axis, the move reached the machine as a bare `G1`.
- `GetGCode` writes F only when the feed changes. A probe block carries its own F
  (`G38.2 Z-5 F200`), so the next cut at F100 was written without F and ran at F200.

**Fix:** A move is dropped as zero-length only when its start is known on every axis
(`Line.StartValid`); `StartTrusted` is gone. The parser makes no move for a G0 or G1 that
names no axis. `GetGCode` forgets the feed after a `PassThrough`, so the next feed move names
its own.

**Lesson:** After a block written verbatim, treat every value the parser or the writer held
before it as unknown, not as still true.

Touches: `inserted-travel-uses-the-files-heights`, `GCodeParser`, `GCodeFile` (constructor,
`GetGCode`), `Line.StartValid`, `coppercli.Tests/GCodeParserPassThroughTests.cs`,
`coppercli.Tests/JobPhasesTests.cs`.
