# Phase enums restated the run lifecycle, and extremes that could only widen

**Problem:** A pause overwrote `MillingPhase.ToolChange`, the value `Resume` reads to decide
whether the stop after a tool change is redundant. Re-probing the node that held an extreme
left the old value standing, and `InterpolateZ` clamps to `MaxHeight`, so on a corrected board
it could return an out-of-bounds cut depth.

**Cause:** Each controller's `*Phase` enum carried lifecycle members alongside its steps —
`MillingPhase.Paused`, `WaitingForOperator` and `Completing`, `ProbePhase.Complete`,
`Cancelled` and `Failed`, `ToolChangePhase.Complete`. The phase members and `ControllerState`
were assigned by different lines, so the two could disagree. `ProbeGrid` kept `MinHeight` and
`MaxHeight` as running extremes widened by each `RecordMeasurement`, which can only widen.

**Fix:** A phase names only the step of work; whether the run is paused, waiting on a person,
finishing, cancelled or failed is read from `ControllerState` through the `ControllerBase`
predicates. `MillingPhase.Initializing` was renamed `ConfiguringMachine`: a name collision
with the lifecycle rather than a duplicate of it, but it read as one. The extremes are derived
from the points on read. Also deleted in the sweep, each a fact stored where nothing needed
it: `Machine.LastProbePosWork` (computed, stored, never read); `AppState.SingleProbing` and
`AppState.SingleProbeCallback` (dead state propping up a macro `probe z` that set a callback
nothing in the tree invoked, so that command could only ever time out — it now calls
`ProbeController.ProbeZSingleAsync`); and `ProbePhase.CreatingGrid` and
`MachineCommands.ProbeZ`, both unreferenced. Two helpers moved to `ControllerBase`:
`WaitWhilePausedAsync`, which replaced three hand-rolled pause loops, and `TryTransitionTo`,
which tests and sets under one lock for a transition another thread may already have made, so
a pause arriving from a UI thread does not fail the run by racing the controller into the same
state. Swept in the session after `168392e`, prompted by
`pause-flag-duplicated-controller-state.md`. `coppercli.Core/Controllers/ControllerBase.cs`,
`coppercli.Core/Controllers/MillingPhase.cs`, `coppercli.Core/Controllers/ProbePhase.cs`,
`coppercli.Core/Controllers/ToolChangePhase.cs`, `coppercli.Core/GCode/ProbeGrid.cs`,
`coppercli.Core/Communication/Machine.cs`, `coppercli/AppState.cs`,
`coppercli/Macro/MacroRunner.cs`, `coppercli/Helpers/MachineCommands.cs`; rules
`one-field-per-fact`, `per-run-state-cleared-at-run-start`, `machine-state-single-writer`;
interface `ui → controllers` v2 → v3.

**Rule:** An enum member that answers a question another type already owns is a duplicate
even though it is not a field, and a running aggregate beside the collection it summarizes is
a duplicate that cannot narrow. Derive both. Ask of any summary: can the underlying data
change in a direction this value is unable to follow?
