# The parser stripped the command word and kept the axis words

**Problem:** `G53 G0 Z-1`, a retract to near the top of machine travel, was streamed to GRBL
as `G0 Z-1` in work coordinates: a rapid to 1 mm below the copper. `G28` and `G30` warned
"may crash into workpiece" and then let the command through. A pcb2gcode header line
`G64 P0.01`, which appears before any motion command, aborted the whole file with "no motion
mode active".

**Cause:** The parser handled G-codes it could not model as toolpath geometry — `G53`, `G10`,
`G92`, `G43.1`, `G38.x`, `G28`, `G30` and anything unknown — by removing the G-word from the
block and letting the rest fall through to the motion handler, on the assumption that GRBL
would still see the line. GRBL sees only what the parser re-emits: `AppState` streams
`GCodeFile.GetGCode()`, the regenerated toolpath, not the operator's original file. An
unrecognized code left its parameters behind. Second order: after an unmodelled block the
parser's modelled position was stale, so the file's own `G0 Z5` recovery move looked like a
no-op and was deleted as a zero-length move, leaving the next cut at retract depth. Inherited
from OpenCNCPilot's parse loop.

**Fix:** A block the parser cannot model is preserved verbatim (`PassThrough`) or refused
outright, and an unmodelled block invalidates the modelled position. Fixed in `4698964`. The
same commit fixed loading a replacement height map without first reloading the original
G-code, which stacked probe corrections additively so the second mill cut deeper than asked.
`coppercli.Core/GCode/GCodeParser.cs`, `coppercli.Core/GCode/GCodeCommands/PassThrough.cs`,
`coppercli.Core/GCode/GCodeFile.cs`, `coppercli/AppState.cs`; rule
`fail-safe-on-uncertainty`; interface `machine → GRBL`.

**Rule:** In a G-code rewriter, axis words belong to their command; never consume a block
partially. "The parser only ignored it" is no defense when the regenerated toolpath is what
cuts.
