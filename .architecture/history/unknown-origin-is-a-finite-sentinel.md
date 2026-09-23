# A check reorder assumed "origin unknown" was non-finite, and it is not

**Date:** 2026-09-22

**Problem:** With no machine connected, a saved height map reads `OriginMoved` for the same
file and `DifferentFile` for another file, never `Unknown`. A reorder of the checks in
`ProbeGrid.GetApplicability` was made to fix that. It changed nothing and was reverted.

**Cause:** `AppState.CurrentSetup` represents "origin unknown" as `Vector3.MinValue`, which
is finite. The `!IsFinite(currentWorkOrigin)` check in `GetApplicability` therefore never
fires for it, so the order of the checks around it does not matter. The reorder assumed
"unknown" was NaN.

**Lesson:** Before reasoning from an "unknown" value, find how it is represented and
follow that value through the check.

**Rule:** `derived-artifact-records-its-context`.

Touches: `ProbeGrid.GetApplicability`, `AppState.CurrentSetup`, `probe data lifecycle`.
