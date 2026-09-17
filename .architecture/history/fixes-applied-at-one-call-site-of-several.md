# Each fix reached one of the sites that had the defect

**Problem:** Four defects from the previous round's audit were reported fixed — a leaked run
slot, a stopped run that never said the tool might still be down, a hand-rolled POST that
dropped a refusal, a modal that stranded its promise — and each fix reached only one of the
places that had it:

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

**Rule:** A defect found at one call site is a question about every call site. Before writing
the fix, grep for the shape of the defect — the missing `catch`, the unused helper — and fix
every site it matches.
