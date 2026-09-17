# 2026-09 — A retry loop with nothing to wait on

**Who:** Thomer, with Claude Opus 5, in the audit round after
`2026-09-one-door-policy-two-implementations`.

**Tried:** lift the door-clearing loop into `MachineWait.ClearDoorHoldAsync` so a run, a
terminal screen and the connect flow all followed one policy.

**Believed:** the loop was safe because every path through it either asks the operator, waits
for the door state to change, or gives up after `MachineClearAttempts`.

**Realized:** it had a fourth path with no bound. `WaitUntilAsync` does not throw on a
cancelled token: its `while` guard skips the body, so the only `await` is never reached and
it returns `false` synchronously. At an open door there is nothing to ask, so the loop
announced, called the wait, got `false` at once, and went round again. Measured on a run
whose token was already cancelled: nineteen million progress messages in two seconds, both
screens receiving every one, and the run never reaching a terminal state.

Two of the three callers hid it. A run has a token, and a terminal screen polls for Escape;
the connect flow passed neither, so it could never leave a door that stayed open. Extracting
the loop made that visible: each copy had carried its own exit, and none was in the loop.

The loop now checks its own token, and `onPoll` ends the wait on the poll it returns true
rather than at the end of the budget, which it had never done.

**Lesson:** a loop that retries is only bounded if every call inside it can block. A helper
that returns early without awaiting turns the retry into a spin, and a cancelled token is the
common way that happens. When lifting a loop out of its callers, check what each caller
relied on to leave it, and move that into the loop.
