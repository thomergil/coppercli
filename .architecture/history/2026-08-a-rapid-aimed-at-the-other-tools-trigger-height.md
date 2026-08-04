# 2026-08 — A rapid aimed at the *other* tool's trigger height

**Who:** Thomer, with Claude Opus 5, in the session after `eecebf4`.

**Tried:** `ToolChangeController._lastToolSetterZ` cached the Z at which the tool setter last
triggered, and drove a `G53 G0` rapid to just above it so the slow probe only had to cover
the final `ToolSetterApproachClearance` (20 mm).

**Believed:** The setter sits at a fixed place, so the height it last triggered at is a safe
place to rapid to.

**Realized:** `ProbeToolSetterAsync` runs **twice per tool change** — once with the old tool,
once with the new one. The cached number always described a different tool than the one being
driven at the setter. Fit a tool more than 20 mm longer than its predecessor and the rapid
drives it into the setter at full speed. The optimization can only be *applied*
across a tool swap, which is precisely the boundary that invalidates it, so it was deleted
outright along with the now-unused constant.

**Note on the review that missed it.** Two audits disagreed. One checked only that the branch
was reachable and pronounced it safe. Reachability was never the question; the geometry was.

**Lesson → rule `no-cached-physical-measurement`, second violation site.** The boundary the
rule protects is not the session and not the job — it is any moment the physical thing can
change. Inside a tool change, that moment *is* the operation. Ask what a cached number
describes and whether it still describes the thing about to be moved. (First site:
`_referenceToolLength`, `2026-08-cached-reference-tool-length`.)

**Touches:** seam `controllers → machine`, rule `no-cached-physical-measurement`,
`coppercli.Core/Controllers/ToolChangeController.cs`.
