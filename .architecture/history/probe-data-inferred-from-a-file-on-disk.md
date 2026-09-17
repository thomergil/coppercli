# Probe data inferred from whether a file exists

**Problem:** One cause produced a family of height-map defects, each patched separately for
months:

- Declining to trust the stored work origin at startup skipped the height-map question, so
  leftover data stayed on disk undecided and was later announced as current.
- Answering "no" to keeping a finished map did not discard it.
- Loading a different board's file offered the previous board's map, defaulting to yes.
- A map already baked into the toolpath was never re-checked, so moving the work origin
  afterwards left every cutting move carrying corrections measured somewhere else.
- A skipped probe point left a hole while the grid reported 100% complete, so the map was
  applied and either crashed or used a wrong height.

**Cause:** Probe and height-map state was inferred from whether an autosave file happened to
exist, and completeness from a stored progress count. Neither describes the map.

**Fix:** The height map carries a `ProbeContext` (source file and work origin), saved with
it, and every "do I have probe data?" question asks the map rather than the filesystem.
Completeness is measured from what was actually probed, not from a counter. Maps written
before the context existed stay `Unknown` and are questioned rather than assumed usable.
Fixed in `4698964`; earlier partial fixes `ad88ff7`, `0653589`, `9d8d842`, `ca29ee2`. Read
the lifecycle documentation at the top of `ProbeController.cs` before changing any of it.
`coppercli.Core/GCode/ProbeContext.cs`, `coppercli.Core/GCode/ProbeGrid.cs`,
`coppercli/Persistence.cs`, `coppercli/AppState.cs`; rule
`derived-artifact-records-its-context`; interfaces `probe data lifecycle`, `app → disk`.

**Rule:** A derived artifact records the setup it was derived from.
