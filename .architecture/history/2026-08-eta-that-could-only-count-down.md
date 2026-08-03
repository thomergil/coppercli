# 2026-08 — An ETA that was algebraically incapable of rising

**Who:** Thomer, across three successive estimators.

**Tried:** (1) elapsed-since-you-pressed-go ÷ lines-done, with the clock started before
homing. (2) A blend of the toolpath's own duration model with the measured rate, weighted
by fraction-complete. (3) An EMA of the measured pace, smoothed over a *share of the job*
rather than a sample count, with current-line dwell as its own term.

**Believed:** (2) was the principled fix for (1) — the model guess dominates early (steady,
immune to measurement noise), the measurement dominates late (lands on the truth):
"gradual and monotonic, with no jumps and nothing to tune".

**Realized:** The blend cannot rise. For a machine running k times slower than the model,
remaining time is (1−f)(1 + f(k−1)); the slope at f=0 is (k−2), so unless the machine was
more than *twice* as slow as predicted, the figure fell steadily to zero while the job kept
cutting. And the unit tests asserted "an early measurement should barely move the
estimate" — the defect restated as the requirement — so they passed throughout. Smoothing
over a sample count also made responsiveness depend on the caller's redraw rate.

**Lesson.** For any "blend a prior with a measurement" scheme, do the algebra or simulate
before trusting the intuition: a weighting that looks like a smooth handover can cancel the
measurement outright. And when a test encodes the *symptom* as the expectation it can never
go red — write the test against the behavior you want (a mid-job slowdown must raise the
estimate) and confirm it fails against the old code first.

**Touches:** seam `ui → controllers`, `coppercli.Core/Controllers/EtaEstimator.cs`,
`coppercli.Core/Controllers/MillingController.cs`, `coppercli/Menus/MillMenu.cs`.
