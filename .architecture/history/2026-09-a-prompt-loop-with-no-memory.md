# 2026-09 — A prompt loop with no memory

**Who:** Thomer, with Claude Opus 5, in the audit round after
`2026-09-a-retry-loop-with-nothing-to-wait-on`.

**Tried:** ask the startup questions one at a time. The old code took the list of pending
questions once and walked it, which asked about a height map after the operator had declined
to load the file it was measured for. The fix re-derived the next question after each answer,
so an earlier answer could remove a later question.

**Believed:** the sequence would end because every answer changes the state the questions are
derived from.

**Realized:** it does not. Three of the four questions are derived from state the answer does
not change. Reloading the file writes the same path back into the session, so the file
question returns. Keeping the height map adopts a copy and leaves the autosave on
disk, so the map question returns. Trusting the work origin writes `IsWorkZeroSet`, while the
question is derived from `HasStoredWorkZero`, which nothing on the answer path touches — so
that one returns on either answer. Only "no" ends anything, and only for two of the four.

`OfferSessionRestore` runs on every launch, after connecting, and every question defaults to
yes. The first thing an operator saw was "Reload the file you had open?", and pressing Enter
asked it again. The only exit was Escape, which quits.

The only test that covered an answer supplied `yes: false` — the single branch that does
clear its own condition.

The loop now carries the set of topics it has already put to the operator, and asks each one
once. Deriving the next question after each answer is kept, because that is what makes
declining the file suppress the map question. Termination no longer depends on it.

**Lesson:** a loop that re-derives its own work list needs a termination rule that does not
depend on the work changing the state the list is derived from. Deriving each step from live
state is correct, but it is not a bound: either prove the work list shrinks on each pass, or
make the sequence monotone. A test that exercises only the branch that terminates proves
nothing about the loop.
