# A two-state door checked in ten places

**Problem:** GRBL reports three door substates, and about ten places across three
controllers, three UIs and three test doubles branched two ways. Every one read `Door:3` as
"the machine is holding", so each offered to release a hold that was already releasing, while
the tool moved.

**Cause:** `Door:3` is the restore from the park: the tool is moving back to where it parked,
and the spindle restarts if one was running. Each screen decided which door state it was in
by asking one question whose two arms were assumed to cover the door. No type owned the
question. Follows the defect in
`readiness-check-refused-closed-door-hold.md`; this entry records what
correcting the door then required everywhere else.

**Fix:** `MachineWait.GetDoorState` owns the question and returns a `DoorState`. `IsDoorOpen`
is defined as the remainder of `IsDoorWaitingForResume` and `IsDoorResuming`, so the three
predicates cover `Door` by construction and a substate GRBL adds falls into Open, the case
that prompts the operator. Every screen that shows door text reads `GetDoorState`. The places
corrected over three audit rounds: the tool-change prompts, both UIs' door displays, the
browser's Resume command, the settle phase, the monitor loop, the M0/M1 continue path, the
tool-change cleanup, the web prompt-recovery payload and the test doubles. The enclosure
prompt moved to `ControllerBase.EnsureDoorClosedAsync`, because the tool-change prompts
released a hold on an answer to "change the tool and press Continue", which does not mention
the enclosure. The tool-change cleanup queued a retract into a machine holding at the door,
which GRBL runs when the hold is released, so an abort at the enclosure prompt now queues
nothing.
`FakeMachine.SimulateMoveAsync` reported `Run` for every move, so the fake kept moving with
the enclosure open and no door test could catch a caller that moves while GRBL holds.
`FakeMachine` and `MockMachine` both resumed `Door:0` to `Idle`, where GRBL returns to the
state the door interrupted, which is `Run` for a streaming job; that hid a real defect for a
round, because a feed hold re-asserted after a door release appeared to work while the fake
was Idle and the machine is cutting. Both doubles now queue behind the hold, ignore a cycle
start while the switch reads ajar, report `Door:3` while restoring, and return to `Run` when
a file is streaming. `FakeMachineDoorTests` checks this.
`WebServerSequenceTests.ADoorHoldDoesNotBlockTheMill_TheControllerPromptsInstead` pins rule
`controllers-check-machine-readiness`: it drives the real HTTP API against a real `Machine`
over a loopback `FakeGrbl` and asserts the run reaches the enclosure prompt. The same round
found that every grep in `check-layering.sh` tolerated a missing path, so the script returned
0 from any directory holding none of the code; it now checks its own paths first.
A door test that opened the enclosure after waiting for `Running` observed a prompt from a
different code path in 13 runs out of 13, so the branch the test was written for could be
deleted and the test stayed green; it now opens the door from the stream's own
`FilePositionChanged` at a named line, so the door opens mid-cut every run.
grbl does not miss a close that happens during the parking retract: it reads the door switch
inside the `while (sys.suspend)` loop of `protocol_exec_rt_suspend`, so closing before the
park finishes and closing after both end at `Door:0` waiting for a cycle start. There is no
ordering to defend against.
`coppercli.Core/Controllers/MachineWait.cs`, `coppercli.Core/Controllers/DoorState.cs`,
`coppercli.Core/Controllers/ControllerBase.cs`,
`coppercli.Core/Controllers/MillingController.cs`,
`coppercli.Core/Controllers/ToolChangeController.cs`, `coppercli/WebServer/CncWebServer.cs`,
`coppercli/WebServer/wwwroot/js/screens.js`, `coppercli/WebServer/wwwroot/js/mill.js`,
`coppercli/Menus/MillMenu.cs`, `coppercli/Menus/JogMenu.cs`,
`coppercli/Menus/ConnectionMenu.cs`, `coppercli.Tests/Fakes/FakeMachine.cs`,
`coppercli.Tests/Fakes/MockMachine.cs`, `coppercli.Tests/Fakes/FakeGrbl.cs`,
`coppercli.Tests/FakeMachineDoorTests.cs`, `coppercli.Tests/MillingControllerTests.cs`,
`coppercli.Tests/WebServerSequenceTests.cs`, `.architecture/rules/check-layering.sh`; rules
`new-state-cases-update-callers`, `behavior-rules-require-behavior-tests`,
`record-unresolved-design-decisions`, `controllers-check-machine-readiness`,
`test-doubles-reproduce-grbl-responses`, `tests-detect-plausible-defects`, `one-field-per-fact`;
interface `controllers → machine` v3.

**Rejected:**

- Splitting the predicate in Core and leaving the call sites alone. `IsDoorResuming` sat in
  `MachineWait` for a whole round with no caller outside Core while three UIs still branched
  two ways. It compiled, the suite was green, and the defect it was written to fix was still
  on every screen. An auditor found it by looking for callers. A predicate defined in Core
  with no caller looks like a handled case, so treat one as unfinished work.
- Deciding what the door does to a paused run. GRBL has one resume, so the cycle start that
  releases a door hold releases a feed hold with it, and a run that was paused when the door
  opened continues cutting while `ControllerState` still reads `Paused`. Two fixes were
  attempted: re-assert the feed hold immediately after the cycle start (the tool moves for
  GRBL's reaction time), and let the release end the pause (which is what the prompt the
  operator answered says it does). Neither follows from the code; it is a product decision
  about what the operator is promised. Recorded as an undecided GAP on `controllers → machine`
  for the owner to pick, rather than shipped with one of the two picked.
- Proving rule `controllers-check-machine-readiness` by grep. The check greps for
  `EnsureMachineReady` and for hand-rolled idle waits; it was proved bypassable by
  re-introducing the gate as `if (MachineWait.IsUnavailable(machine)) return ...`, which
  passes every grep and restores the whole defect. The grep catches one spelling; the test
  catches the behavior however it is written.

**Rule:** Define every door case in `MachineWait.GetDoorState` and use that result in each
screen. Review every caller when adding a case; updating one screen leaves the others with
the old classification.
