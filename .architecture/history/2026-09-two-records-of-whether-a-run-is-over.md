# 2026-09 — Two records of whether a run is over

**Who:** Thomer, with Claude Opus 5, after a report: "When I stop probing and then start
probing, it says probe is already running. This feels like state is kept in more than one
place."

**Tried:** the web server held `_probeTask` and `_probeCts` for the run it started, and the
controller held the run's state. `ReleaseProbeRun` cleared the task unconditionally and
called `controller.Reset()` only `if (controller.HasFinished)`.

**Believed:** a run that ends is a run that reached Completed, Failed or Cancelled, so the
two records say the same thing.

**Realized:** nothing made that true. `ControllerBase.StartAsync` transitions to a terminal
state in its two catch blocks, so a `RunAsync` that *returns* rather than throws ends the
task in whatever state it was in - and `ProbeController.RunAsync` has an exit that returns
while still Initializing. When that happened the task was cleared and the controller was
not, so the server said no run was going while the controller said one was. Starting was
refused by `IsRunInProgress`, stopping took the `runTask == null` branch and never touched
the controller, and the operator had no way back except restarting coppercli.

The rule for clearing a controller had five copies, each with its own hedge, and three of
them leaked. Two start paths wrote `if (State != Idle) Reset()`; `MillMenu` wrote
`if (HasFinished) Reset()` twice, with a comment saying to skip it rather than let Reset
throw - which is exactly the case that leaves the controller stuck. `Reset` refuses
anything but a terminal state, so every caller had to know that, and each answered it
differently.

`ControllerBase.StartAsync` now guarantees a terminal state: a run that returns without
reaching one is finished here, and logged. `IController.ReleaseAsync` is the one way back
to Idle - it stops an unfinished run first, then resets - and the terminal UI, the web
server and both stop paths call it. `HandleProbeStop` calls it even with no run task, so
Stop is the operator's way out of a controller that is stuck.

**Lesson → new rule `one-way-back-to-idle`.** A task completing and a run being over are
one fact, and the controller owns it. Anything else holding a handle to a run holds it to
await, never to answer whether a run is going.

**Touches:** rule `one-way-back-to-idle`, interface `ui → controllers` v5,
`coppercli.Core/Controllers/ControllerBase.cs`, `coppercli.Core/Controllers/IController.cs`,
`coppercli/WebServer/CncWebServer.cs`, `coppercli/Menus/MillMenu.cs`,
`coppercli/Menus/ProbeMenu.cs`, `coppercli.Tests/ControllerBaseTests.cs`,
`coppercli.Tests/ProbeControllerTests.cs`.
