# 2026-08 — Caching a measurement of a thing a human can swap

**Who:** Thomer, with Claude Opus 4.8, in `4698964`. Tool change (M6) arrived in v0.3.0
(`9245487`, `dac85b4`).

**Tried:** `ToolChangeController` carried `_referenceToolLength` and
`_hasReferenceToolLength`, plus `SetSessionState`/`GetSessionState` so a measured reference
could be persisted and reused instead of re-measuring on every tool change.

**Believed:** Re-probing the tool setter for a tool already measured is wasted motion.

**Realized:** The operator can change the tool by hand between jobs, or between tool
changes, and nothing in the system observes that. A cached length is a number about a tool
that may no longer be in the spindle — and it feeds directly into the Z offset the next cut
plunges to. The persistence API was removed entirely; the reference is now always measured.

**Lesson → rule `no-cached-physical-measurement`.** Do not cache a physical measurement
across any boundary where a human can silently change the physical thing. Re-measuring
costs seconds; a stale number costs the workpiece and the bit.

**Note on risk:** this is the highest-risk subsystem in the project.
OpenCNCPilot has no M6 support, so coppercli's tool-change logic has **no reference
implementation to compare against** — every decision here was made from first principles.

**Touches:** seam `controllers → machine`, rule `no-cached-physical-measurement`,
`coppercli.Core/Controllers/ToolChangeController.cs`,
`coppercli.Tests/ToolChangeControllerTests.cs`.
