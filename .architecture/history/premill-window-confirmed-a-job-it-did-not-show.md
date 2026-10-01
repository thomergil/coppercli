# The pre-mill window could start a job it did not show

**Date:** 2026-09-30 · **Found by:** the audit panel reviewing the depth change.

**Problem:** The browser's Start could run a file, a height map or a depth the operator had
not checked. Another tab could load a file or change the depth between the window opening
and Start. A depth press in the window could then confirm a depth that included the other
tab's change. Two Starts at once could cancel each other.

**Cause:** The start checked the job before it took `FileLock`, which every change to the job
takes. It confirmed only the depth, read from the status stream, which follows every change
on the server.

**Fix:** `AppState.MachineFileVersion` changes with every build of the machine's G-code. The
window takes it from `/api/mill/can-start` (with the depth it shows) and from the replies to
its own depth changes, which it sends one at a time. Both a depth change and a start name it.
The server refuses a stale one with `409` and a missing one with `400`. The start claims the
run first (`TryClaimMillRun`, as the probe run does), and checks and starts under `FileLock`;
the run releases the claim only after its own teardown.

**Lesson:** A request that acts on what the operator saw must name what they saw, and the
server must compare it under the same lock the action takes.

**Rule:** web → browser v12 records the contract.

Touches: `AppState.MachineFileVersion`, `AppState.WithFileLocked`, `CncWebServer.StartMilling`,
`CncWebServer.WhyJobChangedSince`, `mill.js`, `web → browser`.
