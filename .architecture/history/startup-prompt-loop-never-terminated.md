# The startup question loop never ended

**Problem:** `OfferSessionRestore` runs on every launch, after connecting, and every question
defaults to yes. The first thing an operator saw was "Reload the file you had open?", and
pressing Enter asked it again. The only exit was Escape, which quits.

**Cause:** The loop re-derived the next question after each answer, so an earlier answer
could remove a later question — which is what stops it asking about a height map after the
operator has declined to load the file it was measured for. Termination then rested on every
answer changing the state the questions are derived from, and three of the four do not.
Reloading the file writes the same path back into the session, so the file question returns.
Keeping the height map adopts a copy and leaves the autosave on disk, so the map question
returns. Trusting the work origin writes `IsWorkZeroSet`, while the question is derived from
`HasStoredWorkZero`, which nothing on the answer path touches, so that one returns on either
answer. Only "no" ends anything, and only for two of the four. The only test that covered an
answer supplied `yes: false`, the single branch that clears its own condition. Found in the
audit round after `door-retry-loop-spun-on-a-canceled-token.md`.

**Fix (superseded):** The loop carried the set of topics it had already asked. Replaced by
rule `session-questions-cleared-by-their-answer`: each answer, yes or no, makes its own
condition false, and no asked set is kept beyond one pass (the browser cannot see it, and it can disagree with the conditions). See `session-questions-asked-twice-by-the-browser.md`.

**Rejected:** Termination resting on answers that leave their question's state unchanged.

**Lesson:** Test both answers to every question.

**Rule:** `session-questions-cleared-by-their-answer`.

**Touches:** rule `session-questions-cleared-by-their-answer`, `coppercli/SessionRestore.cs`.
