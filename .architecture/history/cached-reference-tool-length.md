# A cached measurement of a tool a human can swap

**Problem:** `ToolChangeController` could apply a Z offset derived from a tool that was no
longer in the spindle, and that offset sets the depth the next cut plunges to.

**Cause:** The controller carried `_referenceToolLength` and `_hasReferenceToolLength`, plus
`SetSessionState`/`GetSessionState`, so a measured reference could be persisted and reused
instead of re-measured at every tool change. The operator can change the tool by hand between
jobs, or between tool changes, and nothing in the system observes that. Tool change (M6)
arrived in v0.3.0 (`9245487`, `dac85b4`).

**Fix:** The persistence API was removed; the reference is always measured. Fixed in
`4698964`. `coppercli.Core/Controllers/ToolChangeController.cs`,
`coppercli.Tests/ToolChangeControllerTests.cs`; rule `no-cached-physical-measurement`;
interface `controllers → machine`. OpenCNCPilot has no M6 support, so the tool-change logic
has no reference implementation to compare against and every decision here was made from
first principles.

**Rule:** Do not cache a physical measurement across any point where a human can change the
physical thing without the software seeing it.
