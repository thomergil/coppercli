# The same defects remained at other call sites

**Problem:** Four defects from the previous audit were reported fixed, but the changes missed
other sites with the same defects: a probe run left active, a stop that omitted the
unconfirmed lift, a POST that lost a refusal, and a modal whose promise never resolved:

- the release-on-failed-setup guard went into the grid probe, not the outline trace or the
  single Z probe;
- `LiftAfterStopAsync` went into the probe and the mill, not the tool change;
- seven hand-rolled POSTs were converted and the eighth, the one that drops the serial port,
  was left;
- the promise fix went into `showConfirm` and not `showPremillModal` one function away;
- a CSS class was named at the write and left as a literal at the two reads.

**Cause:** The audit listed these as separate findings, which is how they were fixed. The
pattern showed up only when the next audit reported the second sites, and every one was a site
the first audit had also named. Found in the audit round after
`startup-prompt-loop-never-terminated.md`.

**Fix:** The second sites were fixed in the following round.

**Rule:** Search every call site for the same defect before editing. Include sites with the
same missing `catch` or unused helper in the fix.
