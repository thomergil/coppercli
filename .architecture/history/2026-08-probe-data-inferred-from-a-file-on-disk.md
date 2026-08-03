# 2026-08 — "Do I have probe data?" answered by whether a file exists

**Who:** Thomer, reporting from real sessions; fixed with Claude Opus 4.8 in `4698964`.
Earlier partial fixes: `ad88ff7`, `0653589`, `9d8d842`, `ca29ee2`.

**Tried:** Probe/height-map state was inferred from whether an autosave file happened to
exist, and completeness from a stored progress count.

**Believed:** The autosave file's existence and its point count were a sufficient
description of the height map.

**Realized:** One cause produced a whole family of bugs, and each was patched separately
for months before the cause was named:
- Declining to trust the stored work origin at startup skipped the height-map question,
  so leftover data stayed on disk undecided and was later announced as current.
- Answering "no" to keeping a finished map did not actually discard it.
- Loading a different board's file offered the previous board's map, defaulting to yes.
- A map already baked into the toolpath was never re-checked, so moving the work origin
  afterwards left every cutting move carrying corrections measured somewhere else.
- A skipped probe point left a hole while the grid still reported 100% complete — the map
  was applied and either crashed or silently used a wrong height.

**Lesson → rule `derived-artifact-records-its-context`.** A derived artifact must record
the setup it was derived from. The height map now carries a `ProbeContext` (source file and
work origin), saved with it, and every "do I have probe data?" question asks the map itself
rather than the filesystem. Completeness is measured from what was actually probed, not
from a counter. Maps written before the context existed stay `Unknown` and are *questioned*
rather than assumed usable.

**Note:** this concept is the most-revisited in the project. Read the lifecycle diagram at
the top of `ProbeController.cs` before touching any of it.

**Touches:** seam `probe data lifecycle`, seam `app → disk`, rule
`derived-artifact-records-its-context`, `coppercli.Core/GCode/ProbeContext.cs`,
`coppercli.Core/GCode/ProbeGrid.cs`, `coppercli/Persistence.cs`, `coppercli/AppState.cs`.
