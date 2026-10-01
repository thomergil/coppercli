# The depth adjustment lowered travel, and the tool cut traces between cuts

**Date:** 2026-09-30 · **Asked for by:** Thomer (owner): "the modified depth (by pressing
up/down) should NOT affect the travel height... I just saw it cut traces during travel after
I had lowered it." He reversed the recorded decision to shift the work origin ("not shifting
it sounds like the right call").

**Problem:** After the operator lowered the depth in the pre-mill window, the tool dragged
across copper on moves between cuts.

**Cause:** Two things together. The depth adjustment wrote G54 Z (`G10 L2 P1`), so it lowered
every move, travel included. And the height map corrected only feed moves: rapids kept the
file's flat travel Z over copper that sat up to 1 mm above work zero. On a board that high,
travel at 1 mm less 0.36 mm of adjustment ran below the highest copper.

**Fix:** The depth is a transform of the G-code, not of the origin.
`GCodeFile.OffsetCutDepth` offsets feed moves below Z0 and leaves rapids alone, and nothing
writes G54 for it. With a map applied, `ApplyProbeGrid` raises rapids by the map's highest
point (none when that is below zero), and splits a rapid that has to rise into a climb in Z
then the move in X and Y. The machine's G-code is rebuilt in memory from the file as loaded,
so there is no longer a disk reload, and no reload can fail and leave a map in the G-code.

**Tried and dropped:** keeping the origin shift and lifting travel by the same amount. Every
restore path (stop, alarm, disconnect) had to undo the shift, and a failed restore left the
origin wrong for the next job.

**Lesson:** A correction meant for one kind of move must be applied to those moves, not to
a frame every move shares. Check each correction against travel as well as cuts.

**Rule:** `machine-gcode-built-in-one-place` (promoted). `reload-original-gcode-before-discarding-map`
is retired: nothing reloads now.

Touches: `GCodeFile.OffsetCutDepth`, `GCodeFile.ApplyProbeGrid`, `AppState.PutFileOnMachine`,
`AppState.DepthAdjustment`, `MillingController`, `read-g54-explicitly`.
