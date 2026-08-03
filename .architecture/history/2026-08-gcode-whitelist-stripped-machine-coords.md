# 2026-08 — The parser stripped the command word and kept the axis words

**Who:** Thomer, with Claude Opus 4.8, in `4698964`. The defect was inherited from
OpenCNCPilot's parse loop.

**Tried:** The parser handled G-codes it could not model as toolpath geometry — `G53`,
`G10`, `G92`, `G43.1`, `G38.x`, `G28`, `G30`, and anything unknown — by removing the G-word
from the block and letting the rest fall through to the motion handler.

**Believed:** Those were "valid GRBL commands — pass through without warning". Dropping the
word the parser did not model seemed harmless, because GRBL would still see the line.

**Realized:** GRBL does not see the line. It sees whatever the parser **re-emits** —
`AppState` streams `GCodeFile.GetGCode()`, the regenerated toolpath, not the operator's
original file. So `G53 G0 Z-1` (retract to near the top of machine travel) was re-emitted
as `G0 Z-1` in *work* coordinates: a rapid to 1 mm below the copper. `G28`/`G30` warned
"may crash into workpiece" and then let the command through anyway. An unrecognized code
left its parameters behind: a pcb2gcode header line `G64 P0.01`, which appears before any
motion command, aborted the whole file with "no motion mode active".

A second-order failure followed. After an unmodelled block the parser's modelled
position was stale, so the file's own `G0 Z5` recovery move looked like a no-op and was
deleted as a zero-length move — leaving the next cut at retract depth.

**Lesson.** In a G-code rewriter, axis words belong to their command. A block you cannot
model must be preserved verbatim (`PassThrough`) or refused outright — never partially
consumed. "The parser only ignored it" is never a defense when the regenerated toolpath is
what cuts. And any unmodelled block must invalidate the modelled position, or the *next*
move gets optimized away.

**Related:** the same commit fixed loading a replacement height map without first reloading
the original G-code, so probe corrections stacked additively and the second mill cut
deeper than asked. Same shape: a transformation applied to already-transformed output.

**Touches:** seam `machine → GRBL`, rule `fail-safe-on-uncertainty`,
`coppercli.Core/GCode/GCodeParser.cs`, `coppercli.Core/GCode/GCodeCommands/PassThrough.cs`,
`coppercli.Core/GCode/GCodeFile.cs`, `coppercli/AppState.cs`.
