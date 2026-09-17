# Two Resume windows for one door

**Problem:** A probe run on the machine showed two windows asking the same question about the
enclosure: a yellow box with nothing to press, and a blue box with a y/n.

**Cause:** The question had one owner and more than one channel carrying it to the screen.
The run raised the prompt and also published it as a progress message, and the screens polled
the door themselves and drew a second window from what they found.
`ProbeMenu.OnProgressChanged` was an empty handler, so the run's message went nowhere and the
screen drew its own window instead. It follows
`door-state-checked-in-ten-places.md`, which named the three door states, and
`browser-status-handler-threw-on-every-message.md`, which shipped the answers as values.

**Fix:** A door state reaches the operator on exactly one channel, decided by whether there
is anything to answer: `WaitingForResume` goes out as a prompt, `Open` and `Resuming` as a
progress message, never both. A screen with a run behind it draws what the run published and
never reads the door. `ProbeMenu` and both probe start paths subscribe `UserInputRequired`,
so the probe run can call `EnsureDoorClosedAsync`.
Four more places where the channel had been missed:

- `MenuHelpers.WaitForDoorClear` released the hold from the jog screen's draw loop with no
  operator action, a regression against `never-auto-clear-a-safety-gate`; no test covered it
  because the screen and the release lived in the same function.
- `/api/status` recovered a pending prompt from the milling controller only, so a probe run
  waiting on the enclosure prompt was invisible to a browser that had reloaded, and the
  browser fell through to its no-run door overlay, whose Continue sends a raw cycle start to
  the machine without telling the run. The status now reads `PendingPrompt`, the slot all
  three runs publish into, and `/api/door/release` refuses while any run is in progress.
- In the terminal, the thread that takes the answer also resumes the run, so the run can
  raise its next prompt before the answer returns, and a second keypress answered a question
  the operator had not read. Every prompt drawn in place of another drains the keyboard first.
- The browser's door overlay covers the whole page and carried only Continue, so an operator
  who wanted to abandon the job could not reach Stop. It carries the run's Abort now, and
  with nothing to answer it draws as a banner rather than a layer.

The three test doubles each had their own copy of GRBL's door behavior and had drifted:
`FakeGrbl` executed moves while holding at the door and never alarmed on a soft reset.
`FakeGrbl` is what the real `Machine` and the whole web suite run against, so no door test
could fail on this: a retract wrongly queued at the door would have moved the fake and
passed. `Fakes/DoorModel.cs` owns those rules now and the three doubles call it. `MockMachine`
now follows a `G53 G0 Z`, which also exposed that the tool-change tests waited out a
65-second timeout on a retract that never confirmed; the suite went from four minutes to two.
`RaiseZToClearanceAsync` returns whether the tool reached the height, and
`ToolChangeController` discarded it at all three call sites, each followed by an XY rapid,
while the probe and milling controllers both check it. Every caller has to check a value that
says "the tool may still be down"; where a caller discards it, the value only reaches the log.
`AppState.LoadGCodeIntoMachine` is the one way G-code reaches the machine and it had no
guard: `Machine.SetFile` refused only while `Mode` is `SendFile`, which a job paused at a
tool change is not. Setting Z0 there — the step the tool change exists to ask for —
re-applied the height map, which reloads the G-code, which resets `FilePosition` to 0, so
continuing re-cut the whole board with the new tool. Uploading a file, loading a height map
and restoring a session reach the same place, so one guard there closes all of them, and
zeroing X or Y is refused outright while a run is in progress. `Machine` cannot see a
controller, so its `Mode` check was the closest it could express to "a run owns this file",
and it was too narrow.
`coppercli.Core/Controllers/ControllerBase.cs`, `coppercli.Core/Controllers/MachineWait.cs`,
`coppercli.Core/Controllers/ControllerConstants.cs`,
`coppercli.Core/Controllers/ProbeController.cs`,
`coppercli.Core/Controllers/ToolChangeController.cs`,
`coppercli.Core/Controllers/MillingController.cs`, `coppercli.Core/Communication/Machine.cs`,
`coppercli/Menus/ProbeMenu.cs`, `coppercli/Menus/MillMenu.cs`,
`coppercli/Menus/ConnectionMenu.cs`, `coppercli/Helpers/MenuHelpers.cs`,
`coppercli/Helpers/DisplayHelpers.cs`, `coppercli/Helpers/InputHelpers.cs`,
`coppercli/Macro/MacroRunner.cs`, `coppercli/WebServer/CncWebServer.cs`,
`coppercli/WebServer/wwwroot/js/mill.js`, `coppercli/WebServer/wwwroot/js/jog.js`,
`coppercli.Tests/Fakes/DoorModel.cs`, `.architecture/rules/check-layering.sh`; rules
`never-auto-clear-a-safety-gate`, `one-field-per-fact`,
`a-new-distinction-lands-with-its-callers`, `resume-is-not-approval`,
`the-browser-draws-what-it-was-handed`, `a-test-must-be-able-to-fail`,
`fake-answers-like-the-machine`, `a-redrawn-control-settles-before-it-answers`; interface
`web → browser` v7 (`canReleaseDoor`, `doorMessage`, `machineUnavailable`;
`DetectPendingPrompt` replaces `DetectOperatorPause`).

**Rule:** When a fact has one owner but several ways to reach a screen, the screens will
disagree exactly as if the fact had several owners. Count the channels as well as the owners.
A rule enforced at a lower layer is only as wide as what that layer can see, so the guard
belongs where the runs are visible.
