# 2026-09 — Two Resume windows, one door

**Who:** Thomer, with Claude Opus 5, after a probe run on the machine showed two different
windows asking the same question about the enclosure.

**Tried:** give the enclosure question one owner in Core and have every screen draw what it
is handed. `2026-09-a-two-state-door-in-ten-places` named the three door states,
`2026-09-a-view-model-the-browser-could-not-draw` shipped the answers as values, and this
change was meant to be the wording pass on top.

**Believed:** the question already had one owner, because `EnsureDoorClosedAsync` is the
only place a workflow asks about the enclosure and `ReleaseDoorHoldAsync` the only place a
hold is released.

**Realized:** the question had one owner, and more than one channel carried it to the
screen. The run raised the prompt and also published it as a progress message, and the
screens polled the door themselves and drew a second window from what they found. The
operator saw a yellow box with nothing to press and a blue box with a y/n, for the same
door. `ProbeMenu.OnProgressChanged` was an empty handler, so the run's message went nowhere
and the screen drew its own window instead.

A door state now reaches the operator on exactly one channel, decided by whether there is
anything to answer: `WaitingForResume` goes out as a prompt, `Open` and `Resuming` as a
progress message, never both. A screen with a run behind it draws what the run published
and never reads the door.

**Lesson:** when a fact has one owner but several ways to reach a screen, the screens will
disagree exactly as if the fact had several owners. Count the channels as well as the
owners.

Four things the same sweep turned up, each a place where the channel had been missed:

- `MenuHelpers.WaitForDoorClear` released the hold from the jog screen's draw loop with no
  operator action. That is a regression against `never-auto-clear-a-safety-gate`, and no
  test covered it because the screen and the release lived in the same function.
- `/api/status` recovered a pending prompt from the milling controller only, so a probe run
  waiting on the enclosure prompt was invisible to a browser that had reloaded. The browser
  then fell through to its no-run door overlay, whose Continue sends a raw cycle start to
  the machine without telling the run, while the run is still waiting. The status now reads
  `PendingPrompt`, the slot all three runs publish into, and `/api/door/release` refuses
  while any run is in progress.
- In the terminal, the thread that takes the answer also resumes the run, so the run can
  raise its next prompt before the answer returns. A second keypress answered a question
  the operator had not read. Every prompt drawn in place of another drains the keyboard
  first.
- The browser's door overlay covers the whole page. With only Continue on it, an operator
  who wanted to abandon the job could not reach Stop. It carries the run's Abort now, and
  with nothing to answer it draws as a banner rather than a layer.

**Also learned, about the doubles.** The three test doubles each had their own copy of
GRBL's door behavior and they had already drifted: `FakeGrbl` executed moves while holding
at the door and never alarmed on a soft reset. `FakeGrbl` is what the real `Machine` and
the whole web suite run against, so no door test could fail on this bug — a retract wrongly
queued at the door would have moved the fake and passed. `Fakes/DoorModel.cs` owns those
rules now and the three doubles call it. `MockMachine` now follows a `G53 G0 Z`, which also
exposed that the tool-change tests waited out a 65-second timeout on a retract that never
confirmed; the suite went from four minutes to two.

**Also learned, about the retract's return value.** `RaiseZToClearanceAsync` returned
whether the tool reached the height and `ToolChangeController` discarded it at all three
call sites, each followed by an XY rapid. The probe and milling controllers both check it.
Every caller has to check a value that says "the tool may still be down"; where a caller
discards it, the value only reaches the log.

**Also learned, about who owns the loaded file.** `AppState.LoadGCodeIntoMachine` is the
one way G-code reaches the machine, and it had no guard: `Machine.SetFile` refused only
while `Mode` is `SendFile`, which a job paused at a tool change is not. Setting Z0 there —
the step the tool change exists to ask for — re-applied the height map, which reloads the
G-code, which resets `FilePosition` to 0. Continuing re-cut the whole board with the new
tool. Uploading a file, loading a height map and restoring a session reached the same
place. One guard at the funnel closes all of them, and zeroing X or Y is refused outright
while a run is in progress.

A rule enforced at a lower layer is only as wide as what that layer can see. `Machine`
cannot see a controller, so its `Mode` check was the closest thing to "a run owns this
file" it could express, and it was too narrow. The guard belongs where the runs are
visible.

**Touches:** interface `web → browser` v7 (`canReleaseDoor`, `doorMessage`,
`machineUnavailable`; `DetectPendingPrompt` replaces `DetectOperatorPause`), rules
`never-auto-clear-a-safety-gate`, `one-field-per-fact`,
`a-new-distinction-lands-with-its-callers`, `resume-is-not-approval`,
`the-browser-draws-what-it-was-handed`, `a-test-must-be-able-to-fail`,
`fake-answers-like-the-machine`, `a-redrawn-control-settles-before-it-answers`,
`coppercli.Core/Controllers/ControllerBase.cs`,
`coppercli.Core/Controllers/MachineWait.cs`,
`coppercli.Core/Controllers/ControllerConstants.cs`,
`coppercli.Core/Controllers/ProbeController.cs`,
`coppercli.Core/Controllers/ToolChangeController.cs`,
`coppercli.Core/Controllers/MillingController.cs`,
`coppercli.Core/Communication/Machine.cs`,
`coppercli/Menus/ProbeMenu.cs`, `coppercli/Menus/MillMenu.cs`,
`coppercli/Menus/ConnectionMenu.cs`, `coppercli/Helpers/MenuHelpers.cs`,
`coppercli/Helpers/DisplayHelpers.cs`, `coppercli/Helpers/InputHelpers.cs`,
`coppercli/Macro/MacroRunner.cs`, `coppercli/WebServer/CncWebServer.cs`,
`coppercli/WebServer/wwwroot/js/mill.js`, `coppercli/WebServer/wwwroot/js/jog.js`,
`coppercli.Tests/Fakes/DoorModel.cs`, `.architecture/rules/check-layering.sh`.
