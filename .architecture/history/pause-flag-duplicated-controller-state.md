# A job stopped at 91%: a pause flag that duplicated controller state

**Problem:** Re-milling a board, the job stopped at 91% with no error. After the operator
escaped a tool-change prompt instead of completing it, every later job in that session could
neither finish nor detect its own tool change.

**Cause:** `MillingController` tracked pause in a private `_isPaused`, set true when an M6
tool change was detected in the stream and cleared only by `Resume()`. Escaping the prompt
meant `Resume()` never ran. Controllers are session-lifetime singletons held by `AppState`,
so the flag outlived the job and gated two checks in the monitor loop: completion detection
and M6 detection. `_isPaused` was a second copy of `ControllerState.Paused` from the day it
landed with the controller layer in v0.4.0 (`5780ca8`), when the workflows were lifted out of
the TUI and the TUI's local pause flag came with them: every write sat beside the matching
`TransitionTo`. The audit that followed found `ControllerBase.Reset()` reset `_state` and
nothing else, so each subclass had invented a partial cleanup — `MillingController` cleared 2
of its 8 per-run fields, `ToolChangeController` 1 of 6, and `ProbeController` had no `Reset()`
override at all.

**Fix:** `ControllerBase.IsPaused => State == ControllerState.Paused`, mirroring `IsActive`.
Per-run state is declared in an abstract `ResetRunState()` and cleared at the start of every
run, not only on `Reset()`, because abort paths do not all reach a reset; the method is
abstract so a new controller cannot forget to answer the question.
`ProbeController._grid` and `_currentPointIndex` are excluded deliberately: `LoadGrid` sets
the index to the grid's own progress so an interrupted board resumes where it stopped, and
both setup methods run before `StartAsync`, so clearing them would silently re-probe a
half-measured board. Fixed in the session after `eecebf4`.
`coppercli.Core/Controllers/ControllerBase.cs`,
`coppercli.Core/Controllers/MillingController.cs`,
`coppercli.Core/Controllers/ProbeController.cs`,
`coppercli.Core/Controllers/ToolChangeController.cs`,
`coppercli.Tests/ControllerBaseTests.cs`; rules `per-run-state-cleared-at-run-start`,
`one-field-per-fact`, `machine-state-single-writer`, `read-g54-explicitly`; interface
`ui → controllers` v1 → v2.

**Rejected:** The first pass also excluded `_depthAdjustmentApplied` from the reset,
reasoning that clearing it would let the next run stack a second adjustment.
`ApplyDepthAdjustmentAsync` never reads the flag; it re-reads G54 every time. Keeping a
persistent bool beside a per-run amount was itself a defect: a later run with adjustment 0
computes `restoredZ == currentZ`, passes the restore's tolerance check, and clears the flag
while the earlier shift remains in G54 permanently. The pair collapsed into one field,
`_outstandingDepthAdjustment`, where 0 means the origin is clean.

**Rule:** Anything derivable from `State` is not stored. Before excluding a field from the
per-run reset, ask what it describes: the machine and the operator's setup outlive the run;
the run's own bookkeeping does not.
