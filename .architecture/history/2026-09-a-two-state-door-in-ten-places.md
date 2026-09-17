# 2026-09 — A two-state door in ten places

**Who:** Thomer, with Claude Opus 5, over the three audit rounds and two whole-change
reviews that followed the readiness gate. That defect and its cause are in
`2026-09-a-readiness-gate-in-front-of-the-controller`; this entry records what
correcting the door then required everywhere else.

**Tried:** fix the door wherever a round found it wrong, and ship when the round came
back clean.

**Believed:** the door is two states — open, or closed and holding — so each screen
decides which by asking one question, and its two arms cover the door.

**Realized:** GRBL reports three. `Door:3` is the restore from the park: the tool is moving
back to where it parked, and the spindle restarts if one was running. About ten places
branched two ways, across three controllers, three UIs and three test doubles. Every one
read `Door:3` as "the machine is holding", so each offered to release a hold that was
already releasing, while the tool moved.

Nothing owned the question. `MachineWait.GetDoorState` owns it now and returns a
`DoorState`. `IsDoorOpen` is defined as the remainder of `IsDoorWaitingForResume` and
`IsDoorResuming`, so the three predicates cover `Door` by construction, and a substate GRBL
adds falls into Open, the case that prompts the operator. Every screen that shows door text
reads `GetDoorState`.

Each round's fix was correct and uncovered the next place that decided the door for
itself: the tool-change prompts, both UIs' door displays, the browser's Resume command, the
settle phase, the monitor loop, the M0/M1 continue path, the tool-change cleanup, the web
prompt-recovery payload, and the test doubles. The tool-change prompts released a hold on
an answer to "change the tool and press Continue", which does not mention the enclosure, so
the enclosure prompt moved to `ControllerBase.EnsureDoorClosedAsync`. The tool-change
cleanup queued a retract into a machine holding at the door, which GRBL runs when the hold
is released, so an abort at the enclosure prompt now queues nothing.

**Dead end: splitting a predicate in Core and leaving the call sites alone.**
`IsDoorResuming` sat in `MachineWait` for a whole round with no caller outside Core while
three UIs still branched two ways. It compiled, the suite was green, and the defect it was
written to fix was still on every screen. An auditor found it by looking for callers. A
predicate defined in Core with no caller looks like a handled case, so treat one as
unfinished work.

**Dead end: deciding what the door does to a paused run.** GRBL has one resume, so the
cycle start that releases a door hold releases a feed hold with it, and a run that was
paused when the door opened carries on cutting while `ControllerState` still reads
`Paused`. Two fixes were attempted — re-assert the feed hold immediately after the cycle
start (the tool moves for GRBL's reaction time), and let the release end the pause
(which is what the prompt the operator answered says it does). Neither decided the
question, and neither follows from the code: it is a product call about what the
operator is promised. Recorded as an undecided GAP on `controllers → machine` for the
owner to pick, rather than shipped with one of the two picked.

**Also found: a test double that accepts more than the machine hides the defect it was
built to catch.** `FakeMachine.SimulateMoveAsync` reported `Run` for every move, so the
fake kept moving with the enclosure open and no door test could catch a caller that moves
while GRBL holds. `FakeMachine` and `MockMachine` both resumed `Door:0` to `Idle`, where
GRBL returns to the state the door interrupted, which is `Run` for a streaming job. That
hid a real defect for a round: a feed hold re-asserted after a door release appeared to
work because the fake was Idle where the machine is cutting. Both doubles now queue behind
the hold, ignore a cycle start while the switch reads ajar, report `Door:3` while
restoring, and return to `Run` when a file is streaming. `FakeMachineDoorTests` checks
this.

**Also found: a rule check that greps an identifier proves the spelling, not the
behavior.** The check for `machine-readiness-is-the-controllers` greps for
`EnsureMachineReady` and for hand-rolled idle waits. It was proved bypassable by
re-introducing the gate as `if (MachineWait.IsUnavailable(machine)) return ...`, which
passes every grep and restores the whole defect.
`WebServerSequenceTests.ADoorHoldDoesNotBlockTheMill_TheControllerPromptsInstead`
pins the rule: it drives the real HTTP API against a real `Machine` over a loopback
`FakeGrbl` and asserts the run reaches the enclosure prompt. The grep catches one spelling;
the test catches the behavior however it is written. The same round found that every grep
in `check-layering.sh` tolerates a missing path, so the script returned 0 from any
directory holding none of the code. It now checks its own paths first.

**Also found: a test synchronized on a delay exercises whichever path is fastest.** A door
test opened the enclosure after waiting for `Running`. An auditor measured that the prompt
it observed came from a different code path in 13 runs out of 13, so the branch the test was
written for could be deleted and the test stayed green. It now opens the door from the
stream's own `FilePositionChanged` at a named line, so the door opens mid-cut every run.

**Also settled:** grbl does not miss a close that happens during the parking retract. It
reads the door switch inside the `while (sys.suspend)` loop of
`protocol_exec_rt_suspend`, so closing it before the park finishes and closing it after
both end at `Door:0`, waiting for a cycle start. There is no ordering to defend against.

**Lesson → rules `a-new-distinction-lands-with-its-callers`, `a-grep-is-not-the-guard`,
and `an-undecided-choice-is-recorded-not-shipped`.** When a state has more cases than the
code branches on, give the cases one owner and have every screen read it. Fixing the
branches one screen at a time leaves the next screen wrong. A change that keeps reaching
new places needs another audit round before release.

**Left open:** the undecided pause question above, recorded as a GAP on
`controllers → machine`.

**Touches:** interface `controllers → machine` v3, rules
`a-new-distinction-lands-with-its-callers`, `a-grep-is-not-the-guard`,
`an-undecided-choice-is-recorded-not-shipped`, `machine-readiness-is-the-controllers`,
`fake-answers-like-the-machine`, `a-test-must-be-able-to-fail`, `one-field-per-fact`,
`coppercli.Core/Controllers/MachineWait.cs`,
`coppercli.Core/Controllers/DoorState.cs`,
`coppercli.Core/Controllers/ControllerBase.cs`,
`coppercli.Core/Controllers/MillingController.cs`,
`coppercli.Core/Controllers/ToolChangeController.cs`,
`coppercli/WebServer/CncWebServer.cs`, `coppercli/WebServer/wwwroot/js/screens.js`,
`coppercli/WebServer/wwwroot/js/mill.js`, `coppercli/Menus/MillMenu.cs`,
`coppercli/Menus/JogMenu.cs`, `coppercli/Menus/ConnectionMenu.cs`,
`coppercli.Tests/Fakes/FakeMachine.cs`, `coppercli.Tests/Fakes/MockMachine.cs`,
`coppercli.Tests/Fakes/FakeGrbl.cs`, `coppercli.Tests/FakeMachineDoorTests.cs`,
`coppercli.Tests/MillingControllerTests.cs`,
`coppercli.Tests/WebServerSequenceTests.cs`, `.architecture/rules/check-layering.sh`.
