# A stopwatch that timed more than the probe

**Problem:** An ordinary probe on a flat board tripped the slow-probe alarm and stopped the
run for the operator.

**Cause:** `ProbeController` restarted a `Stopwatch`, sent `G38.2` and stopped it when GRBL's
`[PRB:]` reply arrived; an interval longer than 1.2x a rolling average of the previous
intervals was read as the tool hitting something soft or dragging. `RetractZAsync` and
`MoveToPointAsync` deliberately return without awaiting, so GRBL buffers the retract with the
next rapid and the traverse stays smooth, and `G38.2` then begins with a buffer synchronize.
The stopwatch therefore spanned the previous retract, the XY rapid to the next point, the
descent and the probe itself. Computed from the shipped defaults (`ProbeFeed` 20 mm/min,
`ProbeMinimumHeight` 1.0 mm), a typical interval is about 3150 ms and the threshold about
3780 ms, a margin of roughly 630 ms, while a 40 mm row-change rapid alone costs about 1200
ms. A row change was enough on its own. The detector could not have worked, and tuning the
multiplier would only have moved which geometry tripped it.

**Fix:** `ProbeGrid.GetNeighbourDeviation` returns how far a measured height sits from the
mean of that node's already-measured orthogonal neighbours, and the run compares it against
`ControllerConstants.ProbeHeightDeviationToleranceMm`. The grid owns the lattice and the
heights, so it owns the comparison. A tilted board deviates smoothly and passes; a reading
that disagrees with the copper beside it does not. The verdict is a three-valued
`HeightVerdict` — `Accepted` records, `Remeasure` leaves the point queued so the next pass
probes it again, `Cancelled` stops the run — and the method is `RetractAndJudgeHeightAsync`.
Worked through in the session after `168392e`.
`coppercli.Core/Controllers/ProbeController.cs`, `coppercli.Core/GCode/ProbeGrid.cs`,
`coppercli.Core/Controllers/ControllerConstants.cs`,
`coppercli.Core/Controllers/IProbeController.cs`, `coppercli.Tests/ProbeControllerTests.cs`,
`coppercli.Tests/ProbeGridTests.cs`; rules `monotonic-time-and-event-counts`,
`remeasure-probe-point-after-operator-resume`, `fail-safe-on-uncertainty`; interfaces `probe data lifecycle`
v1 → v2, `ui → controllers`.

**Rejected:** The first replacement returned a boolean.
`RetractAndConfirmHeightAsync` ended `return !ct.IsCancellationRequested`, and the caller
read `true` as "record it". Resuming is not cancelling, so after the operator cleared debris
and pressed Resume, the reading taken before they cleared it was written into the map and the
autosave, and the grid then reported itself complete — a bad height carried into the cut
depth. The comment directly above the call promised the opposite in three clauses, all of
which failed; two independent auditors found it and the author did not. "Keep going" cannot
distinguish "this reading was fine" from "a person intervened", and that difference decides
whether a suspect measurement is committed.

**Rule:** Compare a height with neighboring measurements before accepting it. Check every operation included in a timed interval before using its duration. Return `Accepted`, `Remeasure` or `Cancelled` so the caller can decide whether to record the reading.