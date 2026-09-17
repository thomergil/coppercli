# 2026-09 — The fix that reached one of three

**Who:** Thomer, with Claude Opus 5, in the audit round after
`2026-09-a-prompt-loop-with-no-memory`.

**Tried:** fix a set of defects the previous round's audit found — a leaked run slot, a
stopped run that never said the tool might still be down, a hand-rolled POST that dropped a
refusal, a modal that stranded its promise.

**Believed:** each was fixed.

**Realized:** each fix reached one of the places that had the defect:

- the release-on-failed-setup guard went into the grid probe, not the outline trace or the
  single Z probe;
- `LiftAfterStopAsync` went into the probe and the mill, not the tool change;
- seven hand-rolled POSTs were converted and the eighth, the one that drops the serial port,
  was left;
- the promise fix went into `showConfirm` and not `showPremillModal` one function away;
- a CSS class was named at the write and left as a literal at the two reads.

The audit listed these as separate findings, which is how they were fixed. The pattern showed
up only when the next audit reported the second sites, and every one was a site the first
audit had also named.

**Lesson:** a defect found at one call site is a question about every call site. Before
writing the fix, grep for the shape of the defect — the missing `catch`, the unused helper —
and fix every site it matches. A fix applied only where the report pointed leaves the rest for
the next audit to find.
