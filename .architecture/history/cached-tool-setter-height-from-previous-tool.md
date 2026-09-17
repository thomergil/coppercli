# A rapid aimed at the previous tool's trigger height

**Problem:** Fitting a tool more than `ToolSetterApproachClearance` (20 mm) longer than its
predecessor drove it into the tool setter at rapid speed.

**Cause:** `ToolChangeController._lastToolSetterZ` cached the Z at which the tool setter last
triggered and drove a `G53 G0` rapid to just above it, so the slow probe covered only the
final 20 mm. `ProbeToolSetterAsync` runs twice per tool change, once with the old tool and
once with the new one, so the cached number always described a different tool than the one
being driven at the setter.

**Fix:** The cache, the rapid and the now-unused constant were deleted. The optimization can
only be applied across a tool swap, which is the moment that invalidates it.
`coppercli.Core/Controllers/ToolChangeController.cs`; rule
`no-cached-physical-measurement`; interface `controllers → machine`.

**Rejected:** One of two audits checked only that the branch was reachable and called it
safe. Reachability was not the question; the geometry was.

**Rule:** Second site for `no-cached-physical-measurement` (first: `_referenceToolLength`,
`cached-reference-tool-length.md`). The rule covers any moment the physical thing can change,
not the session or the job. Inside a tool change that moment is the operation itself.
