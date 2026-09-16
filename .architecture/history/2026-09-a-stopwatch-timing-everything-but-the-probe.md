# 2026-09 — A stopwatch that timed everything except the probe, and a verdict that could not say why it was continuing

**Who:** Thomer, after an ordinary probe on a flat board tripped the slow-probe alarm;
worked through with Claude Opus 5 in the session after `168392e`.

**Tried:** `ProbeController` restarted a `Stopwatch`, sent `G38.2`, and stopped it when
GRBL's `[PRB:]` reply arrived. An interval longer than 1.2x a rolling average of the
previous intervals meant the tool had hit something soft or was dragging, and the run
stopped for the operator.

**Believed:** the interval between sending `G38.2` and reading `[PRB:]` measures the probe.

**Realized:** it measures four things. `RetractZAsync` and `MoveToPointAsync` deliberately
return without awaiting, so GRBL buffers the retract with the next rapid and the traverse
stays smooth; `G38.2` then begins with a buffer synchronize. The stopwatch therefore spanned
the previous retract, the XY rapid to the next point, the descent, and the probe itself.
Computed from the shipped defaults (`ProbeFeed` 20 mm/min, `ProbeMinimumHeight` 1.0 mm), a
typical interval is about 3150 ms and the threshold about 3780 ms, a margin of roughly
630 ms — while a 40 mm row-change rapid alone costs about 1200 ms. A row change was enough
on its own. The detector could not have worked, and tuning the multiplier would only have
moved which geometry tripped it.

Replaced with `ProbeGrid.GetNeighbourDeviation`, which returns how far a measured height
sits from the mean of that node's already-measured orthogonal neighbours; the run compares
that deviation against `ControllerConstants.ProbeHeightDeviationToleranceMm`. The grid owns
the lattice and the heights, so it owns the comparison. A board that is tilted deviates smoothly and
passes; a reading that disagrees with the copper beside it does not.

**The mistake made while fixing it.** The replacement recorded the height it had just
rejected. `RetractAndConfirmHeightAsync` ended `return !ct.IsCancellationRequested`, and the
caller read `true` as "record it". Resuming is not cancelling. After the operator cleared the
debris and pressed Resume, the reading taken *before* they cleared it was written into the
map and the autosave. The grid then reported itself complete — a bad height silently carried
into the cut depth. The comment directly above the call promised the opposite in
three clauses, all of which failed. Two independent auditors found it; the author did not.

The boolean was the defect, not the expression that filled it. "Keep going" cannot
distinguish "this reading was fine" from "a person intervened", and that difference
decides whether a suspect measurement is committed. It is now a three-valued
`HeightVerdict` — `Accepted` records, `Remeasure` leaves the point queued so the next pass
probes it again, `Cancelled` stops the run — and the method is named
`RetractAndJudgeHeightAsync` for what it returns.

**Lesson → rule `monotonic-time-and-event-counts` (extended) and new rule
`resume-is-not-approval`.** A question about the workpiece is answered from the measurement,
never from elapsed time; the store already said this for peer liveness, and the two are one
rule. Before timing a code path, ask what else is inside the interval — in this layer the
deliberate returns without awaiting mean an interval almost always contains more than the
call that opened it. And a return value that decides whether to commit a
suspect measurement names why it is continuing; a boolean cannot.

**Touches:** `probe data lifecycle` (v1 → v2), `ui → controllers`,
rules `monotonic-time-and-event-counts`,
`resume-is-not-approval`, `fail-safe-on-uncertainty`,
`coppercli.Core/Controllers/ProbeController.cs`, `coppercli.Core/GCode/ProbeGrid.cs`,
`coppercli.Core/Controllers/ControllerConstants.cs`,
`coppercli.Core/Controllers/IProbeController.cs`,
`coppercli.Tests/ProbeControllerTests.cs`, `coppercli.Tests/ProbeGridTests.cs`.
