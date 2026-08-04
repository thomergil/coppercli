# 2026-08 — The job that stopped at 91%: per-run state outliving its run

**Who:** Thomer, reporting from a real milling session; fixed with Claude Opus 5 in the
session after `eecebf4`. The flag arrived with the controller layer in v0.4.0 (`5780ca8`),
when the workflows were lifted out of the TUI and the TUI's local pause flag came with them.

**Tried:** `MillingController` tracked pause in a private `_isPaused`, set true when an M6
tool change was detected in the stream and cleared only by `Resume()`.

**Believed:** A paused job is always resumed, so `Resume()` is where the flag comes back
down.

**Realized:** The operator escaped the tool-change prompt instead of completing it, so
`Resume()` never ran. Controllers are session-lifetime singletons (`AppState`); the flag
outlived the job and gated two checks in the monitor loop: completion detection and M6
detection. Every later job in that session could neither finish nor notice its own tool
change; re-milling the same board, it stopped at 91% with no error.

`_isPaused` was a second copy of `ControllerState.Paused` from the day it landed: every
write sat beside the matching `TransitionTo`. It is now `ControllerBase.IsPaused =>
State == ControllerState.Paused`, mirroring `IsActive`.

The audit that followed found the same shape everywhere: `ControllerBase.Reset()` reset `_state`
and nothing else, so each subclass had invented its own partial cleanup — `MillingController`
cleared 2 of its 8 per-run fields, `ToolChangeController` 1 of 6, `ProbeController` had no
`Reset()` override at all.

**The counter-lesson, and the wrong answer given while learning it.** Not every controller
field belongs to the run. `ProbeController._grid` and `_currentPointIndex` must survive:
`LoadGrid` sets the index to the grid's own progress so an interrupted board resumes where
it stopped, and both setup methods run *before* `StartAsync`. Clearing them would silently
re-probe a half-measured board.

The first pass put `_depthAdjustmentApplied` in that same protected category, reasoning that
clearing it would let the next run stack a second adjustment. That reasoning was false:
`ApplyDepthAdjustmentAsync` never reads the flag; it re-reads G54 every time. Worse, keeping
a persistent bool beside a per-run amount was itself a bug: a later run with adjustment 0
computes `restoredZ == currentZ`, passes the restore's tolerance check, and clears the flag
while the earlier shift stays baked into G54 permanently. The pair collapsed into one field,
`_outstandingDepthAdjustment`, where 0 means the origin is clean.

**Lesson → rules `per-run-state-cleared-at-run-start` and `one-field-per-fact`.** State that
describes one run is declared in `ResetRunState()` and cleared at the *start* of every run,
not only on `Reset()` — abort paths do not all reach a reset. The method is `abstract` so a
new controller cannot forget to answer the question. Anything derivable from `State` is not
stored. And before protecting a field from the reset, ask what it describes: the
machine and the operator's setup outlive the run; the run's own bookkeeping does not.

**Touches:** seam `ui → controllers` (v1 → v2), rules `per-run-state-cleared-at-run-start`,
`one-field-per-fact`, `machine-state-single-writer`, `read-g54-explicitly`,
`coppercli.Core/Controllers/ControllerBase.cs`,
`coppercli.Core/Controllers/MillingController.cs`,
`coppercli.Core/Controllers/ProbeController.cs`,
`coppercli.Core/Controllers/ToolChangeController.cs`,
`coppercli.Tests/ControllerBaseTests.cs`.
