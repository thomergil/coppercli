# The ETA spread the job's time over its lines

**Date:** 2026-10-01 · **Found by:** Thomer (owner), at the machine: "The ETA is completely
bogus as it seems to not take into account the length of the lines it's milling."
Supersedes `eta-that-could-only-count-down`.

**Cause:** `EtaEstimator` gave every line the same share of the job's time, then blended in
the pace measured in lines per second. Lines differ by a hundredfold: an isolation segment
takes 0.05 s, an outline segment a second or more, a drill several short moves. It also
counted lines sent, which run about 17 lines ahead of the tool in GRBL's buffers, and left
out rapids and dwells.

**Decided:** `JobTimeline` times each line of the machine's G-code: a feed move or an arc
its length at its feed, a rapid as long as the axis that takes longest at its own top speed
($110 to $112, listed at each run's start), a dwell its time. The run finds the tool on the
path among the lines sent, searching forward only. The time left is the time of the moves
after the tool's place, at GRBL's feed and rapid overrides. Nothing is rescaled by the pace
so far.

**Evidence:** the debug log records every line sent and a status report every 100 ms, so two
runs from 2026-10-01 on a Nomad 3 were replayed through each candidate: isolation (549 s,
9,252 lines) and drilling with an outline (581 s, 1,021 lines). Error of the time left, old
against new: isolation, mean 63 s against 4 s and worst 325 s against 12 s; drilling, mean
116 s against 3 s and worst 534 s against 5 s. The model's total came within 2.4% of one run
and to the second on the other; after the first minute the machine never ran more than 8%
behind it.

**Tried and dropped:**
- Rescaling the model by the measured pace, over the whole run or the last 60 or 120 s. It
  made both runs worse (mean 13 to 24 s): drilling runs a little slower than its model, and
  that pace was carried onto the outline, which matches its model.
- A planner model with acceleration and corner speeds. The drilling run ran 46 s behind the
  model, but the prototype of the plain model had timed the milled holes' helical arcs as
  straight plunges; timed by arc length, the plain model matched the run to a tenth of a
  second. Fitting accelerations to the two runs gave values a factor of sixteen apart.
- Counting progress by the lines sent: mean errors 6 and 8 s, against 4 and 3 s from where
  the tool is.

**Limits:** the estimate assumes the machine holds the feeds the file gives, as this Nomad
does. A machine whose accelerations keep it below its feeds on short moves finishes later
than shown, and the estimate does not learn that.

**Lesson:** Score an ETA against real runs before trusting the formula; the debug log holds
what a replay needs. The earlier rule, that a mid-job slowdown must raise the estimate, now
reads: a lower feed override raises it at once, and a stall leaves it where it was rather
than counting it down.

**Rule:** controllers → machine v10 (`FeedOverride`, `RapidOverride`, `TopSpeeds`,
`RefreshSettingsAsync`);
machine → GRBL v6 (`$$`); ui → controllers v8 (`MillingController.Estimate`); web → browser
v16 (`file.progress`, `file.timeLeft`).

Touches: `JobTimeline`, `JobPosition`, `JobEstimate`, `Motion.Nearest`, `Line.Nearest`,
`Arc.Nearest`, `Vector3.Dot`, `MillingController`, `IMillingController.Estimate`,
`IMachine.TopSpeeds`, `IMachine.RefreshSettingsAsync`, `GrblProtocol.CmdViewSettings`,
`Constants.GrblLinesHeldMax`, `DisplayHelpers.FormatTimeSpan`, `AppState.MillEstimate`,
`MillMenu.DrawMillProgress`, `CncWebServer` `GetFileStatus`, `screens.js`, `EtaEstimator`,
`eta-that-could-only-count-down`, `controllers → machine`, `machine → GRBL`,
`ui → controllers`, `web → browser`, `coppercli.Tests/JobTimelineTests.cs`,
`coppercli.Tests/MillEstimateEndpointTests.cs`, `coppercli.Tests/MillingControllerTests.cs`.
