# An ETA that could not rise

**Problem:** The milling ETA fell steadily to zero while the job kept cutting.

**Cause:** Three estimators in succession. (1) Elapsed-since-you-pressed-go divided by
lines-done, with the clock started before homing. (2) A blend of the toolpath's own duration
model with the measured rate, weighted by fraction-complete. (3) An EMA of the measured pace,
smoothed over a share of the job rather than a sample count, with current-line dwell as its
own term. The blend in (2) cannot rise: for a machine running k times slower than the model,
remaining time is (1−f)(1 + f(k−1)), so the slope at f=0 is (k−2) and the figure falls unless
the machine is more than twice as slow as predicted. The unit tests asserted "an early
measurement should barely move the estimate" — the defect restated as the requirement — so
they passed throughout. Smoothing over a sample count also made responsiveness depend on the
caller's redraw rate.

**Fix:** Estimator (3). `coppercli.Core/Controllers/EtaEstimator.cs`,
`coppercli.Core/Controllers/MillingController.cs`, `coppercli/Menus/MillMenu.cs`; interface
`ui → controllers`.

**Rejected:** Estimator (2), adopted as the principled fix for (1) on the reasoning that the
model guess dominates early and the measurement dominates late, giving a gradual monotonic
handover with nothing to tune. The algebra above shows the weighting cancels the measurement
instead.

**Rule:** For any scheme that blends a prior with a measurement, do the algebra or simulate
before trusting the intuition. Write the test against the behavior you want — a mid-job
slowdown must raise the estimate — and confirm it fails against the old code first.
