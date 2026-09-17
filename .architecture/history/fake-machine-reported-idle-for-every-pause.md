# FakeMachine reported Idle for every pause line

**Problem:** The operator pause (M0/M1) added to milling was gated on `MachineWait.IsIdle`.
All 338 tests passed, the new ones among them, but on hardware the pause would never have
fired and the suite would have stayed green.

**Cause:** `FakeMachine` hardcoded `Idle` as the status after every pause line. Real GRBL
answers M0 and M1 with a feed hold (`Hold:0`), because those M-codes are sent to the
controller; M6 is consumed by coppercli and never reaches the machine. The Idle gate could
only be satisfied by the case the fake invented, so the code and the tests agreed with each
other.

**Fix:** The gate accepts Idle or Hold. `FakeMachine` reports `Hold:0` for `ProgramStop` and
`OptionalStop` only. `coppercli.Tests/Fakes/FakeMachine.cs`,
`coppercli.Core/Controllers/MillingController.cs`; rule `fake-answers-like-the-machine`;
interfaces `controllers → machine`, `ui → controllers`.
Still open in the operator-pause feature as landed: the browser's `isMilling` switch in
`coppercli/WebServer/wwwroot/js/mill.js` has no `WaitingForUserInput` case although the
server publishes that state in `GetSharedConstants()`, so a job parked mid-cut at an M0
reports "Milling complete!" and drops the operator on the dashboard; the M0 prompt overlay is
erased by the next status broadcast; and the M0 pause neither retracts Z nor stops the
spindle, unlike the tool-change pause in the same controller.

**Rule:** A test double reproduces the machine's observable answer to each command under
test. One status hardcoded across a family of commands erases the distinction the code is
deciding on. Second occurrence here: `FakeMachine` also discarded every `G53`
(`test-project-stopped-compiling-and-no-ci-ran-it.md`).
