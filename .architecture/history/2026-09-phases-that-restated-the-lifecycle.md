# 2026-09 — Phases that restated the lifecycle, and extremes that could only widen

**Who:** Thomer, with Claude Opus 5, in the session after `168392e` — a deliberate sweep for
duplicate state, prompted by the `_isPaused` defect recorded in
`2026-08-a-pause-flag-that-outlived-its-job`.

**Tried:** each controller's `*Phase` enum carried lifecycle members alongside its steps —
`MillingPhase.Paused`, `WaitingForOperator` and `Completing`, `ProbePhase.Complete`,
`Cancelled` and `Failed`, `ToolChangePhase.Complete`. `ProbeGrid` kept `MinHeight` and
`MaxHeight` as running extremes, widened by each `RecordMeasurement`.

**Believed:** a phase enum describes everything the run is doing, the lifecycle included;
and an extreme is cheap to keep up to date as measurements arrive.

**Realized:** both are second copies written on a separate path from the fact they restate.
The phase members and `ControllerState` were assigned by different lines, so the two could
disagree, and once did: a pause overwrote `MillingPhase.ToolChange`, the value `Resume`
reads to decide whether the stop after a tool change is redundant. A phase now names only
the step of work; whether the run is paused, waiting on a person, finishing, cancelled or
failed is read from `ControllerState` through the `ControllerBase` predicates.
`MillingPhase.Initializing` was renamed `ConfiguringMachine` — a name collision with the
lifecycle rather than a duplicate of it, but it read as one.

The running extremes were worse than redundant, because they could only widen. Re-probing
the node that held an extreme left the old value standing, and `InterpolateZ` clamps to
`MaxHeight`, so on a corrected board it could still return an out-of-bounds cut depth. They are
now derived from the points on read.

Also deleted in the sweep, each a fact stored where nothing needed it: `Machine.LastProbePosWork`
(computed, stored, never read); `AppState.SingleProbing` and `AppState.SingleProbeCallback`
(dead state propping up a macro `probe z` that set a callback nothing in the tree invoked, so
that command could only ever time out — it now calls `ProbeController.ProbeZSingleAsync`);
`ProbePhase.CreatingGrid` and `MachineCommands.ProbeZ`, both unreferenced.

Two helpers came out of the same sweep and belong on `ControllerBase`, not in each
controller. `WaitWhilePausedAsync` replaced three hand-rolled pause loops.
`TryTransitionTo` tests and sets under one lock, for a transition another thread may already
have made: a pause arriving from a UI thread must not fail the run by racing the controller
into the same state.

**Lesson → rule `one-field-per-fact` (extended) and the `ui → controllers` contract v2 → v3.** An
enum member that answers a question another type already owns is a duplicate even though it
is not a field, and a running aggregate beside the collection it summarizes is a duplicate
that cannot narrow. Derive both. Ask of any summary: can the underlying data change in a
direction this value is unable to follow?

**Touches:** `ui → controllers` (v2 → v3), rules `one-field-per-fact`,
`per-run-state-cleared-at-run-start`, `machine-state-single-writer`,
`coppercli.Core/Controllers/ControllerBase.cs`,
`coppercli.Core/Controllers/MillingPhase.cs`, `coppercli.Core/Controllers/ProbePhase.cs`,
`coppercli.Core/Controllers/ToolChangePhase.cs`, `coppercli.Core/GCode/ProbeGrid.cs`,
`coppercli.Core/Communication/Machine.cs`, `coppercli/AppState.cs`,
`coppercli/Macro/MacroRunner.cs`, `coppercli/Helpers/MachineCommands.cs`.
