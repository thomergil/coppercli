# 2026-08 — A fake that answered `Idle` to every pause

**Who:** Thomer, with Claude Opus 5, in the session after `eecebf4`, while adding an
operator-pause (M0/M1) feature to milling.

**Tried:** Gate the new operator pause on `MachineWait.IsIdle`, and trust the suite: 338 tests
green, the new ones among them.

**Believed:** Whatever `FakeMachine` reported after a pause line was what GRBL reports.

**Realized:** `FakeMachine` hardcoded `Idle` for every pause line. Real GRBL answers M0 and M1
with a feed hold (`Hold:0`), because those M-codes *are* sent to the controller — M6 is
swallowed by coppercli and never reaches the machine. The Idle gate could only be satisfied by
the case the fake invented: on hardware the feature would never have fired, and the suite would
have stayed green. The gate now accepts Idle **or** Hold, and the fake reports `Hold:0` for
`ProgramStop`/`OptionalStop` only.

**Lesson → rule `fake-answers-like-the-machine`.** This is the second time a double's
convenience hid a real defect here; the first was `FakeMachine` silently discarding every
`G53`, which made untested safety retracts look tested
(`2026-08-a-test-suite-that-had-not-compiled-since-february`). A double must reproduce the
machine's *observable answer* to each command under test. One status hardcoded across a family
of commands erases the distinction the code is deciding on, and the tests then agree with the
code because both read the same invention.

**Still open — do not read this entry as "M0/M1 is done".** A review of the finished change
scored it 5/10, with the root-cause work at 8/10. Known defects in the operator-pause feature
as landed:

- The browser's `isMilling` switch (`wwwroot/js/mill.js`) has no `WaitingForUserInput` case —
  the client never names that state, though the server serves it in
  `GetSharedConstants()`. A job parked mid-cut at an M0 reports "Milling complete!" and drops
  the operator on the dashboard.
- The M0 prompt overlay is erased by the next status broadcast.
- The M0 pause neither retracts Z nor stops the spindle, unlike the tool-change pause sitting
  next to it in the same controller.

**Touches:** seam `controllers → machine`, seam `ui → controllers`, rule
`fake-answers-like-the-machine`, `coppercli.Tests/Fakes/FakeMachine.cs`,
`coppercli.Core/Controllers/MillingController.cs`,
`coppercli/WebServer/wwwroot/js/mill.js`.
