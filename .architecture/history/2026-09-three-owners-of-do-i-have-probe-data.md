# 2026-09 — Three owners of "do I have probe data?"

**Who:** Thomer, with Claude Opus 5, in a defect sweep of the probe subsystem after he hit
four user-visible probe defects in a row that the previous pass had missed.

**Tried:** three places answered whether the operator had usable probe data.
`Persistence.GetProbeState()` answered from whether the autosave file parses;
`AppState.ProbePoints` answered from whatever happened to be loaded; the web status
composed a third answer by hand. Each gate read one of them.

**Believed:** the three describe the same thing, so it does not matter which one a gate
reads.

**Realized:** `MenuHelpers.ValidateMillPreflight` gated on `AppState.ProbePoints != null`.
A finished height map sitting in the autosave and not yet loaded made that null, so the
preflight found no map to apply and cleared the job to run with no height correction. The
probe screen, reading the file, reported the map complete at the same time. Three auditors
reached that independently and a skeptic proved it with a failing test,
`WebServerSequenceTests.ACompleteMapTheStatusReports_StopsTheMillUntilApplied`.

It is one owner now. `AppState.ReadUsableAutosave()` is the only place the `ProbeContext`
applicability test runs, and it reads without adopting, so `GET /api/probe/status` can call
it (rule `no-side-effect-on-get`). `AppState.CurrentProbeGrid` — the grid in memory, or
that autosave when nothing is loaded — is the one answer to "is there probe data", read by
the mill preflight and `/api/probe/save`. `/api/probe/apply` goes through
`ReadUsableAutosave` and adopts, because applying is an operator action and a status read
is not. `Persistence.GetProbeState()` and its `ProbeState` enum are deleted. One raw read
of the file is left on purpose: `SessionRestore.DescribeStoredMap` parses it again to write
the text of the prompt that asks about it. No gate reads what it returns.

**Dead end: satisfying `no-side-effect-on-get` by deleting the call that carried the check.**
`GET /api/probe/status` called `EnsureProbeDataLoaded()`, which loads. Removing it and
reading the raw file state instead also removed the applicability test that call carried,
and the status began announcing a map measured on another board as current data. Reading
and adopting are two operations, and the checks belong to the read.

**Dead end: a cache in `Persistence` keyed on the file's timestamp and length,** added to
stop the map being parsed several times a second. It handed out the cached object. Three
call sites adopt what that method returns as the live grid and then probe into it, so the
method that claimed to describe the file described memory instead. It also cached parse
failures, and the key repeats on a filesystem with coarse timestamps, which this tree has
(`2026-08-the-working-tree-is-dropbox-synced`). Deleting the second parser removed the need
for the cache: the cost was reading the file twice.

**Dead end: making `HandleMillStopAsync` call `ReleaseAsync()` unconditionally.** The run's
own teardown had overrun the stop budget and was still stopping the machine and lifting the
tool, and the second feed-hold and soft reset wiped the lift it had queued (rule
`closing-the-port-does-not-stop-grbl`). Keep the guard HEAD had: await the tracked run's own
teardown, and release only what has none.

**Also settled:**
- A `.pgrid` decides the commanded Z of every cutting move, so `ProbeGrid.Load` enforces
  what the constructor enforces (`RequireUsableShape`): finite ordered extents, at least
  two nodes per axis, point indices inside the grid, heights that are numbers.
- A map that names a source file must carry an origin that can be read. A non-finite origin
  compares false against every tolerance, so the map drops to `Unknown`. No gate refuses
  `Unknown`, so every gate would accept such a file for any job.
- `/api/probe/start` applies the gate the terminal applies,
  `MenuHelpers.GetProbeDisabledReason`. Grid positions are work coordinates, so probing
  from an origin nobody set drives the tool to arbitrary XY.
- Changing the toolpath — `apply`, `load`, `discard`, `setup` — is refused while a run owns
  the machine. `Machine.SetFile` rewinds the file a paused run would resume from, and a
  setup deletes the autosave a running probe is still writing into.

**Lesson → `one-field-per-fact` extended, and new rule
`a-loader-enforces-the-constructors-invariants`.** A question answered in three places has
three answers, and the gate reads one while the screen reads another. Name the owner of the
question, not only of the field; every gate and every display then derives from that owner.
A loader that turns a file into an object which decides machine motion enforces what the
constructor enforces.

**Left open:** sixteen call sites outside `AppState` still read `AppState.ProbePoints`
directly and its setter is still public, so new code can still read it instead of
`AppState.CurrentProbeGrid`. Recorded as a GAP on the `probe data lifecycle` interface
rather than fixed, because it is a second change.

**Touches:** interface `probe data lifecycle` v3, rules
`a-loader-enforces-the-constructors-invariants`, `one-field-per-fact`,
`derived-artifact-records-its-context`, `no-side-effect-on-get`, `one-way-back-to-idle`,
`fail-safe-on-uncertainty`, `a-test-must-be-able-to-fail`, `coppercli/AppState.cs`,
`coppercli/Persistence.cs`, `coppercli/Helpers/MenuHelpers.cs`,
`coppercli/Menus/ProbeMenu.cs`, `coppercli/WebServer/CncWebServer.cs`,
`coppercli.Core/GCode/ProbeGrid.cs`, `coppercli.Core/Controllers/ProbeController.cs`,
`coppercli.Tests/ProbeGridLoadTests.cs`, `coppercli.Tests/WebServerSequenceTests.cs`,
`coppercli.Tests/ProbeLoadDoubleApplyTests.cs`.
