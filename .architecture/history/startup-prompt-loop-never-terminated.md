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

**Fix:** The loop carries the set of topics it has already put to the operator and asks each
one once. Re-deriving the next question after each answer is kept, because that is what makes
declining the file suppress the map question; termination no longer depends on it.

**Rejected:** Re-deriving the work list on each pass as the termination rule, for the reason
above.

**Rule:** Track topics already asked in `SessionRestore.AskPendingSteps` so each startup question appears once. Test both answers to a question; the answer need not change the state used to derive the pending list.
