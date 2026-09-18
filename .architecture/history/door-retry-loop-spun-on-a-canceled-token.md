# Door retry loop skipped its wait after cancellation

**Problem:** On a run whose token was already canceled, the door-clearing loop produced
nineteen million progress messages in two seconds, both screens received every one, and the
run never reached a terminal state.

**Cause:** The door-clearing loop was lifted into `MachineWait.ClearDoorHoldAsync` so a run,
a terminal screen and the connect flow all followed one policy. Every path through it was
believed to ask the operator, wait for the door state to change, or give up after
`MachineClearAttempts`. It had a fourth path with no bound: `WaitUntilAsync` does not throw
on a canceled token — its `while` guard skips the body, so the only `await` is never reached
and it returns `false` synchronously. At an open door there is nothing to ask, so the loop
announced, called the wait, got `false` at once, and went round again. Two of the three
callers hid it: a run has a token and a terminal screen polls for Escape, while the connect
flow passed neither, so it could never leave a door that stayed open. Each copy had carried
its own exit and none was in the loop. Found in the audit round after
`one-door-policy-two-implementations.md`.

**Fix:** The loop checks its own token, and `onPoll` ends the wait on the poll it returns
true on the same poll instead of waiting for the timeout, which it had never done.

**Rule:** Check whether each call in a retry loop can return immediately, especially with a
canceled token. Preserve each caller's cancellation and exit conditions when moving the
loop into a shared function.
