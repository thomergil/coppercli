#nullable enable
using System;
using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;
using System.Text.RegularExpressions;
using coppercli;
using coppercli.Core.Controllers;
using coppercli.Helpers;
using coppercli.Core.GCode;
using coppercli.Core.Settings;
using coppercli.Core.Util;
using coppercli.Tests.Fakes;
using coppercli.WebServer;
using Xunit;

namespace coppercli.Tests
{
    // Drives CncWebServer end to end over HTTP: the probe, mill, door, settings and work-zero
    // endpoints, and the /api/status payload the browser renders from. The server, the fake
    // GRBL and AppState are shared across the collection, so a test that moves the machine
    // restores it in a finally block. Several tests read
    // coppercli/WebServer/wwwroot/js/constants.js directly, because it keeps its own copy of
    // the C# enum names and nothing else compares the two outside a running browser.
    [Collection(WebServerCollection.Name)]
    public class WebServerSequenceTests
    {
        private readonly WebServerFixture _web;

        public WebServerSequenceTests(WebServerFixture web)
        {
            _web = web;
            _web.RestoreFixtureState();
        }

        private HttpClient Client => _web.Client;

        private async Task<JsonElement> GetJson(string path)
        {
            var response = await Client.GetAsync(path);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        }

        private async Task<(HttpStatusCode Code, JsonElement Body)> Post(string path, object? body = null)
        {
            var response = body == null
                ? await Client.PostAsync(path, null)
                : await Client.PostAsJsonAsync(path, body);
            string text = await response.Content.ReadAsStringAsync();
            var json = string.IsNullOrWhiteSpace(text)
                ? default
                : JsonDocument.Parse(text).RootElement.Clone();
            return (response.StatusCode, json);
        }

        private static bool Flag(JsonElement json, string name) =>
            json.ValueKind == JsonValueKind.Object
            && json.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.True;

        /// <summary>
        /// Runs <paramref name="body"/> with a mill run holding at the enclosure prompt, then
        /// stops the run and releases the hold. The fixture's machine is shared, so the
        /// teardown runs in a finally block whether the body passed or not.
        /// </summary>
        private async Task WhileAMillRunHoldsAtTheDoor(Func<Task> body)
        {
            await GivenAGridIsReady();
            GivenACompleteAutosaveForThisJob();
            Assert.True(Flag((await Post(WebConstants.ApiProbeApply)).Body, "success"),
                "the map could not be applied");

            _web.Grbl.SimulateDoorClosedAndHolding();
            WebServerFixture.WaitUntil(
                () => MachineWait.GetDoorState(AppState.Machine) == DoorState.WaitingForResume,
                "the machine to report the door hold");

            try
            {
                Assert.True(Flag((await Post(WebConstants.ApiMillStart)).Body, "success"),
                    "the mill did not start");
                WebServerFixture.WaitUntil(
                    () => AppState.Milling.IsRunInProgress, "the run to take the machine");

                await body();
            }
            finally
            {
                await Post(WebConstants.ApiMillStop);
                WebServerFixture.WaitUntil(() => !AppState.Milling.IsRunInProgress, "the run to end");
                await MachineWait.ReleaseDoorHoldAsync(AppState.Machine, ControllerConstants.DoorResumeTimeoutMs);
                WebServerFixture.WaitUntil(() => !MachineWait.IsDoor(AppState.Machine), "the door hold to clear");
            }
        }

        /// <summary>
        /// Loads a board and sets up a grid, leaving the G-code file on disk. Discard and
        /// re-apply reload that file, so a test of either needs it to still exist.
        /// </summary>
        private async Task<string> GivenAGridIsReadyAndTheBoardStays()
        {
            string file = Path.Combine(Path.GetTempPath(), "coppercli-smoke-" + Guid.NewGuid().ToString("N") + ".ngc");
            File.WriteAllText(file,
                "G21\nG90\nG0 X0 Y0 Z1\nG1 Z-0.1 F100\nG1 X40 Y0\nG1 X40 Y40\n"
                + "G1 X0 Y40\nG1 X0 Y0\nG0 Z1\nM5\nM2\n");

            AppState.MarkWorkZeroSet();

            var (loadCode, loadBody) = await Post(WebConstants.ApiFileLoad, new { path = file });
            Assert.True(loadCode == HttpStatusCode.OK && Flag(loadBody, "success"),
                $"loading the board failed: {loadCode} {loadBody}");

            var (setupCode, setupBody) = await Post(WebConstants.ApiProbeSetup,
                new { margin = 1.0, gridSize = 20.0 });
            Assert.True(setupCode == HttpStatusCode.OK && Flag(setupBody, "success"),
                $"probe setup failed: {setupCode} {setupBody}");

            return file;
        }

        /// <summary>
        /// Loads a board and sets up a grid, then deletes the G-code file. A test that needs
        /// that file on disk calls GivenAGridIsReadyAndTheBoardStays instead.
        /// </summary>
        private async Task GivenAGridIsReady()
        {
            File.Delete(await GivenAGridIsReadyAndTheBoardStays());
        }

        private static ProbeGrid CompleteMapForThisJob()
        {
            var complete = new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20))
            {
                Context = new ProbeContext(AppState.Session.LastLoadedGCodeFile!, AppState.Machine.G54Offset)
            };

            for (int x = 0; x < complete.SizeX; x++)
            {
                for (int y = 0; y < complete.SizeY; y++)
                {
                    complete.RecordMeasurement(x, y, -0.1);
                }
            }

            return complete;
        }

        private static void GivenACompleteAutosaveForThisJob()
        {
            var complete = CompleteMapForThisJob();
            AppState.DiscardProbeData();
            complete.Save(Persistence.GetProbeAutoSavePath());
        }

        private async Task StopTheProbeAndWaitForIdle()
        {
            await Post(WebConstants.ApiProbeStop);
            WebServerFixture.WaitUntil(() => !AppState.Probe.IsRunInProgress,
                "the probe controller to stop claiming the machine");
        }

        [Fact]
        public async Task StartingAProbe_StoppingIt_AndStartingAgain_IsAllowed()
        {
            await GivenAGridIsReady();

            var (firstCode, firstBody) = await Post(WebConstants.ApiProbeStart);
            Assert.True(Flag(firstBody, "success"), $"first start refused: {firstCode} {firstBody}");

            await StopTheProbeAndWaitForIdle();

            var (secondCode, secondBody) = await Post(WebConstants.ApiProbeStart);
            Assert.True(Flag(secondBody, "success"), $"second start refused: {secondCode} {secondBody}");

            await StopTheProbeAndWaitForIdle();
        }

        /// <summary>
        /// The trace and the grid probe run on the same controller, so a stopped trace has to
        /// leave it able to start the next run.
        /// </summary>
        [Fact]
        public async Task TracingTheOutline_StoppingIt_AndStartingAProbe_IsAllowed()
        {
            await GivenAGridIsReady();

            var (traceCode, traceBody) = await Post(WebConstants.ApiProbeTrace);
            Assert.True(Flag(traceBody, "success"), $"trace refused: {traceCode} {traceBody}");

            await StopTheProbeAndWaitForIdle();

            var (startCode, startBody) = await Post(WebConstants.ApiProbeStart);
            Assert.True(Flag(startBody, "success"), $"start after a stopped trace refused: {startCode} {startBody}");

            await StopTheProbeAndWaitForIdle();
        }

        /// <summary>
        /// A trace moves the tool but records no measurements, so neither /api/status nor
        /// /api/probe/status may report it as probing.
        /// </summary>
        [Fact]
        public async Task WhileTracing_TheStatusReportsTracingNotProbing()
        {
            await GivenAGridIsReady();

            var (traceCode, traceBody) = await Post(WebConstants.ApiProbeTrace);
            Assert.True(Flag(traceBody, "success"), $"trace refused: {traceCode} {traceBody}");

            WebServerFixture.WaitUntil(() => AppState.IsTracingOutline, "the trace to start");

            var status = await GetJson(WebConstants.ApiStatus);
            Assert.True(status.GetProperty("tracingOutline").GetBoolean(), "status did not report the trace");
            Assert.False(status.GetProperty("probing").GetBoolean(), "a trace reported itself as probing");

            var probeStatus = await GetJson(WebConstants.ApiProbeStatus);
            Assert.False(probeStatus.GetProperty("active").GetBoolean(),
                "the probe status called a trace an active probe");

            await StopTheProbeAndWaitForIdle();
        }

        [Fact]
        public async Task StartingTwice_IsRefusedWhileTheFirstRunIsActive()
        {
            await GivenAGridIsReady();

            var (_, firstBody) = await Post(WebConstants.ApiProbeStart);
            Assert.True(Flag(firstBody, "success"));

            WebServerFixture.WaitUntil(() => AppState.Probe.IsRunInProgress, "the first run to own the machine");

            var (secondCode, secondBody) = await Post(WebConstants.ApiProbeStart);
            Assert.Equal(HttpStatusCode.Conflict, secondCode);
            Assert.False(Flag(secondBody, "success"));

            await StopTheProbeAndWaitForIdle();
        }

        /// <summary>
        /// GRBL keeps executing the moves in its planner buffer after the host stops
        /// streaming, so a stop that only changes controller state leaves the tool cutting.
        /// </summary>
        [Fact]
        public async Task StoppingAProbe_SendsAFeedHoldAndASoftResetToTheMachine()
        {
            await GivenAGridIsReady();

            int resetsBefore = _web.Grbl.SoftResetCount;

            var (_, startBody) = await Post(WebConstants.ApiProbeStart);
            Assert.True(Flag(startBody, "success"));
            WebServerFixture.WaitUntil(() => AppState.Probe.IsRunInProgress, "the run to own the machine");

            await StopTheProbeAndWaitForIdle();

            WebServerFixture.WaitUntil(() => _web.Grbl.SoftResetCount > resetsBefore,
                "a soft reset to reach the machine");

            // Order matters: the feed hold decelerates the tool and the reset then ends the
            // job. A reset first would stop the machine where it stands.
            var sent = _web.Grbl.Received;
            int hold = sent.ToList().FindLastIndex(l => l == FakeGrbl.FeedHoldMark);
            int reset = sent.ToList().FindLastIndex(l => l == FakeGrbl.SoftResetMark);
            Assert.True(hold >= 0, "no feed hold reached the machine");
            Assert.True(reset > hold, "the soft reset did not follow the feed hold");
        }

        /// <summary>
        /// Reporting an autosave measured on another board would offer the operator recovery
        /// of a height map that cuts this board at the wrong depth.
        /// </summary>
        [Fact]
        public async Task AnAutosaveMeasuredForAnotherBoard_IsNotReportedAsProbeData()
        {
            await GivenAGridIsReady();

            // Clear what setup left in memory, so only the autosave is left to read.
            var (discardCode, _) = await Post(WebConstants.ApiProbeDiscard);
            Assert.Equal(HttpStatusCode.OK, discardCode);

            var strayGrid = new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20))
            {
                Context = new ProbeContext("/some/other/board.ngc", new Vector3(0, 0, 0))
            };
            strayGrid.RecordMeasurement(0, 0, -0.1);
            strayGrid.Save(Persistence.GetProbeAutoSavePath());

            var status = await GetJson(WebConstants.ApiProbeStatus);

            Assert.Equal(WebConstants.ProbeStateNone, status.GetProperty("state").GetString());

            // The same response must not offer Recover for the map it excluded.
            Assert.False(status.GetProperty("hasUnsavedData").GetBoolean());
        }

        /// <summary>
        /// With no work zero set, the grid's coordinates put the tool at arbitrary XY, so the
        /// start returns 409 as the terminal's own check does.
        /// </summary>
        [Fact]
        public async Task StartingAProbe_IsRefusedWhenNoWorkZeroIsSet()
        {
            await GivenAGridIsReady();
            AppState.ClearWorkZero();

            var (code, body) = await Post(WebConstants.ApiProbeStart);

            Assert.Equal(HttpStatusCode.Conflict, code);
            Assert.False(Flag(body, "success"));
        }

        /// <summary>
        /// A complete map that is not applied blocks the mill. Without that check the job
        /// runs with no height correction.
        /// </summary>
        [Fact]
        public async Task ACompleteMapTheStatusReports_StopsTheMillUntilApplied()
        {
            await GivenAGridIsReady();

            GivenACompleteAutosaveForThisJob();

            var status = await GetJson(WebConstants.ApiProbeStatus);
            Assert.Equal(WebConstants.ProbeStateComplete, status.GetProperty("state").GetString());

            var canStart = await GetJson(WebConstants.ApiMillCanStart);
            Assert.False(canStart.GetProperty("canStart").GetBoolean(),
                "the mill was cleared to run uncorrected while a complete map sat unapplied");
        }

        /// <summary>The GET reports the autosave without adopting it; see rule
        /// no-side-effect-on-get in ARCHITECTURE.md.</summary>
        [Fact]
        public async Task ReadingTheProbeStatus_DoesNotAdoptTheAutosave()
        {
            await GivenAGridIsReady();

            var usable = new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20))
            {
                Context = new ProbeContext(AppState.Session.LastLoadedGCodeFile!, new Vector3(0, 0, 0))
            };
            usable.RecordMeasurement(0, 0, -0.1);

            AppState.DiscardProbeData();
            usable.Save(Persistence.GetProbeAutoSavePath());

            var status = await GetJson(WebConstants.ApiProbeStatus);

            Assert.Equal(WebConstants.ProbeStatePartial, status.GetProperty("state").GetString());
            Assert.Null(AppState.ProbePoints);
        }

        /// <summary>
        /// A grid records its source file and G54 offset when it is created. Without that
        /// record every later applicability check returns Unknown and the map is accepted.
        /// </summary>
        [Fact]
        public async Task ANewGrid_RecordsTheSetupItWasMeasuredIn()
        {
            await GivenAGridIsReady();

            var grid = AppState.ProbePoints!;

            Assert.True(grid.Context.IsKnown, "the grid was created with no recorded setup");
            Assert.Equal(AppState.Session.LastLoadedGCodeFile, grid.Context.SourceFile);
            Assert.Equal(
                ProbeApplicability.Applicable,
                grid.GetApplicability(AppState.Session.LastLoadedGCodeFile!, AppState.Machine.G54Offset));
        }

        /// <summary>
        /// The mill is blocked while a complete map is unapplied, so /api/probe/apply has to
        /// apply the map /api/probe/status reported from the autosave. Otherwise the block
        /// has no way out.
        /// </summary>
        [Fact]
        public async Task ACompleteMapTheStatusReports_CanBeApplied()
        {
            await GivenAGridIsReady();
            GivenACompleteAutosaveForThisJob();

            var status = await GetJson(WebConstants.ApiProbeStatus);
            Assert.Equal(WebConstants.ProbeStateComplete, status.GetProperty("state").GetString());

            var (_, applied) = await Post(WebConstants.ApiProbeApply);
            Assert.True(Flag(applied, "success"),
                "the map the status announced could not be applied, so the mill stays blocked");

            var canStart = await GetJson(WebConstants.ApiMillCanStart);
            Assert.True(canStart.GetProperty("canStart").GetBoolean(),
                "the mill stayed blocked after the announced map was applied");
        }

        /// <summary>
        /// The status payload includes derived values so screens do not interpret the raw
        /// GRBL status. This test detects missing fields or older door booleans.
        /// </summary>
        [Fact]
        public async Task Status_IncludesDerivedMachineFields()
        {
            var status = await GetJson(WebConstants.ApiStatus);

            foreach (string answer in new[]
                     {
                         "machineActivity", "needsAttention", "canPause", "canResume",
                         "canReleaseDoor", "machineUnavailable", "doorMessage",
                         "connected", "buttons"
                     })
            {
                Assert.True(status.TryGetProperty(answer, out _),
                    $"the status no longer answers {answer}, so a screen has to work it out");
            }

            // machineActivity is a MachineActivity name, not a raw GRBL word, so the browser
            // looks its text up by name instead of comparing status strings.
            Assert.True(System.Enum.TryParse<MachineActivity>(
                    status.GetProperty("machineActivity").GetString(), out _),
                "machineActivity is not one of the names MachineActivity defines");

            // The browser reads machineActivity for the door, so these three stay out.
            foreach (string gone in new[] { "doorOpen", "doorWaitingForResume", "doorResuming" })
            {
                Assert.False(status.TryGetProperty(gone, out _),
                    $"{gone} is back; the browser will partition the door itself again");
            }
        }

        /// <summary>
        /// With no run in progress, /api/door/release is the operator's confirmation and
        /// sends the cycle start itself rather than answering a run's prompt.
        /// </summary>
        [Fact]
        public async Task AClosedDoorWithNoRun_IsReleasedByTheOverlay()
        {
            _web.Grbl.SimulateDoorClosedAndHolding();
            WebServerFixture.WaitUntil(
                () => MachineWait.GetDoorState(AppState.Machine) == DoorState.WaitingForResume,
                "the door hold");

            int cycleStarts = _web.Grbl.CycleStartCount;
            var (_, body) = await Post(WebConstants.ApiDoorRelease);

            Assert.True(Flag(body, "success"), "the release was refused with no run to own it");
            Assert.True(_web.Grbl.CycleStartCount > cycleStarts, "no cycle start was sent");
            WebServerFixture.WaitUntil(
                () => !MachineWait.IsDoor(AppState.Machine), "the door hold to clear");
        }

        /// <summary>
        /// An open door is rejected before ReleaseDoorHoldAsync, which would wait for the
        /// status reading to catch up. A switch that flipped closed inside that wait would
        /// take the cycle start the operator asked for while the door was open.
        /// </summary>
        [Fact]
        public async Task AnOpenDoor_IsNotReleasedByTheOverlay()
        {
            _web.Grbl.SimulateDoorOpen();
            WebServerFixture.WaitUntil(
                () => MachineWait.GetDoorState(AppState.Machine) == DoorState.Open, "the open door");

            try
            {
                int cycleStarts = _web.Grbl.CycleStartCount;
                var (_, body) = await Post(WebConstants.ApiDoorRelease);

                Assert.False(Flag(body, "success"), "an open door was accepted for release");
                Assert.Equal(
                    ControllerConstants.ErrorDoorBlocksResume, body.GetProperty("error").GetString());
                Assert.Equal(cycleStarts, _web.Grbl.CycleStartCount);
            }
            finally
            {
                _web.Grbl.SimulateDoorClosedAndHolding();
                await MachineWait.ReleaseDoorHoldAsync(AppState.Machine, ControllerConstants.DoorResumeTimeoutMs);
                WebServerFixture.WaitUntil(() => MachineWait.IsIdle(AppState.Machine), "the machine to settle");
            }
        }

        /// <summary>
        /// The text for a door state comes from ControllerConstants through the status
        /// payload. A second copy in the browser could show the wrong sentence for a state.
        /// </summary>
        [Fact]
        public async Task Status_DoorMessageMatchesControllerText()
        {
            _web.Grbl.SimulateDoorOpen();
            WebServerFixture.WaitUntil(
                () => MachineWait.GetDoorState(AppState.Machine) == DoorState.Open, "the open door");

            try
            {
                var status = await GetJson(WebConstants.ApiStatus);
                Assert.Equal(
                    ControllerConstants.DoorOpenPrompt,
                    status.GetProperty("doorMessage").GetString());
            }
            finally
            {
                _web.Grbl.SimulateDoorClosedAndHolding();
                await MachineWait.ReleaseDoorHoldAsync(AppState.Machine, ControllerConstants.DoorResumeTimeoutMs);
                WebServerFixture.WaitUntil(() => MachineWait.IsIdle(AppState.Machine), "the machine to settle");
            }
        }

        /// <summary>
        /// doorMessage is null away from the door, which is what keeps the browser's overlay
        /// hidden.
        /// </summary>
        [Fact]
        public async Task AMachineAwayFromTheDoor_SendsNoDoorMessage()
        {
            var status = await GetJson(WebConstants.ApiStatus);

            Assert.Equal(JsonValueKind.Null, status.GetProperty("doorMessage").ValueKind);
        }

        /// <summary>
        /// constants.js holds its own copy of the MachineActivity names the browser looks its
        /// text up by. validateConstants compares the two only in a running browser, so
        /// without this a rename in C# reaches the operator as GRBL's raw word.
        /// </summary>
        [Fact]
        public void EveryBrowserActivityName_MatchesTheEnum()
        {
            string constants = File.ReadAllText(Path.Combine(
                WebServerFixture.RepositoryRoot, "coppercli", "WebServer", "wwwroot", "js", "constants.js"));

            var held = Regex.Matches(constants, @"MACHINE_ACTIVITY_[A-Z_]+ = '([A-Za-z]+)'")
                .Select(m => m.Groups[1].Value)
                .ToList();

            Assert.NotEmpty(held);
            foreach (string name in held)
            {
                Assert.True(Enum.TryParse<MachineActivity>(name, out _),
                    $"constants.js names the activity {name}, which MachineActivity no longer defines");
            }
        }

        /// <summary>
        /// The warning before an X or Y zero is published through /api/constants so the
        /// terminal and the browser use one wording. Two copies drift, and the operator then
        /// reads a different sentence depending on which screen they are on.
        /// </summary>
        [Fact]
        public async Task TheZeroWarning_ReachesTheBrowserWordForWord()
        {
            var published = (await GetJson(WebConstants.ApiConstants)).GetProperty("zeroWarning");

            string constants = File.ReadAllText(Path.Combine(
                WebServerFixture.RepositoryRoot, "coppercli", "WebServer", "wwwroot", "js", "constants.js"));

            Assert.Equal(CliConstants.ZeroDiscardsMap, published.GetProperty("discardsMap").GetString());
            Assert.Equal(CliConstants.PartlyMeasuredMap, published.GetProperty("partlyMeasured").GetString());
            Assert.Equal(CliConstants.CompleteMap, published.GetProperty("complete").GetString());

            Assert.Contains(SingleQuoted(CliConstants.PartlyMeasuredMap), constants);
            Assert.Contains(SingleQuoted(CliConstants.CompleteMap), constants);
        }

        private static string SingleQuoted(string text) => $"'{text}'";

        /// <summary>
        /// An outcome added to WorkZeroOutcome but not to constants.js leaves the browser
        /// looking up text under a name it does not define, and the operator reads the bare
        /// axes line.
        /// </summary>
        [Fact]
        public void EveryHeightMapOutcome_HasABrowserName()
        {
            string constants = File.ReadAllText(Path.Combine(
                WebServerFixture.RepositoryRoot, "coppercli", "WebServer", "wwwroot", "js", "constants.js"));

            var named = Regex.Matches(constants, @"\bZEROED_[A-Z_]+ = '([A-Za-z]+)'")
                .Select(m => m.Groups[1].Value)
                .ToHashSet();

            foreach (var outcome in Enum.GetValues<WorkZeroOutcome>())
            {
                // NothingToDo needs no text: the map did not change.
                if (outcome == WorkZeroOutcome.NothingToDo)
                {
                    continue;
                }

                Assert.Contains(outcome.ToString(), named);
            }
        }

        /// <summary>
        /// The browser looks its text up under the ZEROED_ values in constants.js, so one
        /// that no longer matches a WorkZeroOutcome name leaves the operator reading
        /// "undefined" in place of what became of the map.
        /// </summary>
        [Fact]
        public void EveryBrowserHeightMapOutcomeName_MatchesTheEnum()
        {
            string constants = File.ReadAllText(Path.Combine(
                WebServerFixture.RepositoryRoot, "coppercli", "WebServer", "wwwroot", "js", "constants.js"));

            var held = Regex.Matches(constants, @"\bZEROED_[A-Z_]+ = '([A-Za-z]+)'")
                .Select(m => m.Groups[1].Value)
                .ToList();

            Assert.NotEmpty(held);
            foreach (string name in held)
            {
                Assert.True(Enum.TryParse<WorkZeroOutcome>(name, out _),
                    $"constants.js names the outcome {name}, which WorkZeroOutcome no longer defines");
            }
        }

        /// <summary>
        /// MachineWaitTests covers what MachineWait derives; this covers which status field
        /// each value is sent in. A value that parses but is wrong gives the browser a button
        /// that does the opposite of its label.
        /// </summary>
        [Fact]
        public async Task TheStatus_ReportsWhichControlsApply()
        {
            try
            {
            WebServerFixture.WaitUntil(() => MachineWait.IsIdle(AppState.Machine), "the machine to be idle");
            await AssertStatusReports(MachineActivity.Idle, needsAttention: false, canPause: false, canResume: false);

            await Post(WebConstants.ApiFeedhold);
            WebServerFixture.WaitUntil(() => MachineWait.IsHold(AppState.Machine), "the feed hold");
            await AssertStatusReports(MachineActivity.Hold, needsAttention: false, canPause: false, canResume: true);

            await Post(WebConstants.ApiResume);
            WebServerFixture.WaitUntil(() => MachineWait.IsIdle(AppState.Machine), "the hold to lift");

            _web.Grbl.SimulateDoorOpen();
            WebServerFixture.WaitUntil(
                () => MachineWait.GetDoorState(AppState.Machine) == DoorState.Open, "the open door");
            await AssertStatusReports(MachineActivity.DoorOpen, needsAttention: true, canPause: false, canResume: false);

            _web.Grbl.SimulateDoorClosedAndHolding();
            WebServerFixture.WaitUntil(
                () => MachineWait.GetDoorState(AppState.Machine) == DoorState.WaitingForResume, "the door hold");
            await AssertStatusReports(
                MachineActivity.DoorHolding, needsAttention: true, canPause: false, canResume: false);

            }
            finally
            {
                // The fixture's machine is shared, so restore it even when an assertion failed.
                await MachineWait.ReleaseDoorHoldAsync(AppState.Machine, ControllerConstants.DoorResumeTimeoutMs);
                WebServerFixture.WaitUntil(() => MachineWait.IsIdle(AppState.Machine), "the machine to settle");
            }
        }

        private async Task AssertStatusReports(
            MachineActivity expected, bool needsAttention, bool canPause, bool canResume)
        {
            var status = await GetJson(WebConstants.ApiStatus);

            Assert.Equal(expected.ToString(), status.GetProperty("machineActivity").GetString());
            Assert.Equal(needsAttention, status.GetProperty("needsAttention").GetBoolean());
            Assert.Equal(canPause, status.GetProperty("canPause").GetBoolean());
            Assert.Equal(canResume, status.GetProperty("canResume").GetBoolean());
            Assert.True(status.GetProperty("connected").GetBoolean(),
                "a machine that is answering reports as disconnected");
        }

        /// <summary>
        /// A prompt recovered from the status carries the run's own message text. Without it
        /// the overlay falls back to its tool-change heading, and the operator answers what
        /// reads as a prompt about the tool while the machine restarts the spindle.
        /// </summary>
        [Fact]
        public async Task AProbeRunsDoorPrompt_IsRecoverableFromTheStatus()
        {
            await GivenAGridIsReady();

            _web.Grbl.SimulateDoorClosedAndHolding();
            WebServerFixture.WaitUntil(
                () => MachineWait.GetDoorState(AppState.Machine) == DoorState.WaitingForResume,
                "the door hold");

            try
            {
                await Post(WebConstants.ApiProbeStart);
                // The run transitions to WaitingForUserInput before it publishes the prompt,
                // so a status read taken on the state alone can find no prompt yet.
                WebServerFixture.WaitUntil(
                    () => PendingPrompt.Current?.IsDoorPrompt == true,
                    "the probe run to ask about the enclosure");

                var prompt = (await GetJson(WebConstants.ApiStatus)).GetProperty("toolChange");

                Assert.True(prompt.ValueKind != JsonValueKind.Null,
                    "a browser that reloaded cannot learn the probe run is waiting on the door");
                Assert.Equal(ControllerConstants.DoorHoldingPrompt, prompt.GetProperty("message").GetString());
                Assert.True(prompt.GetProperty("isDoorPrompt").GetBoolean());
                Assert.NotNull(prompt.GetProperty("id").GetString());
            }
            finally
            {
                await Post(WebConstants.ApiProbeStop);
                WebServerFixture.WaitUntil(() => !AppState.Probe.IsRunInProgress, "the run to end");
                await MachineWait.ReleaseDoorHoldAsync(AppState.Machine, ControllerConstants.DoorResumeTimeoutMs);
                WebServerFixture.WaitUntil(() => !MachineWait.IsDoor(AppState.Machine), "the door hold to clear");
            }
        }

        /// <summary>
        /// While a run waits on its own enclosure prompt, /api/door/release must not send the
        /// cycle start. The machine would resume while the run still waits on an answer that
        /// can no longer arrive.
        /// </summary>
        [Fact]
        public async Task ARunWaitingOnTheDoor_RefusesTheOverlaysRelease()
        {
            await GivenAGridIsReady();

            _web.Grbl.SimulateDoorClosedAndHolding();
            WebServerFixture.WaitUntil(
                () => MachineWait.GetDoorState(AppState.Machine) == DoorState.WaitingForResume,
                "the door hold");

            try
            {
                await Post(WebConstants.ApiProbeStart);
                // The run transitions to WaitingForUserInput before it publishes the prompt,
                // so a status read taken on the state alone can find no prompt yet.
                WebServerFixture.WaitUntil(
                    () => PendingPrompt.Current?.IsDoorPrompt == true,
                    "the probe run to ask about the enclosure");

                int cycleStarts = _web.Grbl.CycleStartCount;
                var (_, body) = await Post(WebConstants.ApiDoorRelease);

                Assert.False(Flag(body, "success"), "the release went through behind the run");
                Assert.Equal(
                    ControllerConstants.ErrorDoorAnswerThePrompt, body.GetProperty("error").GetString());
                Assert.Equal(cycleStarts, _web.Grbl.CycleStartCount);
            }
            finally
            {
                await Post(WebConstants.ApiProbeStop);
                WebServerFixture.WaitUntil(() => !AppState.Probe.IsRunInProgress, "the run to end");
                await MachineWait.ReleaseDoorHoldAsync(AppState.Machine, ControllerConstants.DoorResumeTimeoutMs);
                WebServerFixture.WaitUntil(() => !MachineWait.IsDoor(AppState.Machine), "the door hold to clear");
            }
        }

        /// <summary>
        /// MenuEnableTests checks only the terminal's mapping, so without this a new
        /// MillBlocker reaches the browser as the fallback text.
        /// </summary>
        [Fact]
        public void EveryMillBlocker_HasBrowserText()
        {
            foreach (MillBlocker error in System.Enum.GetValues<MillBlocker>())
            {
                if (error == MillBlocker.None) { continue; }

                string named = CncWebServer.GetMillBlockerMessage(
                    new MillStartCheck(error, new System.Collections.Generic.List<MillWarning>(), "0/9"));

                Assert.False(string.IsNullOrWhiteSpace(named), $"{error} has no reason to show");
                Assert.NotEqual(WebConstants.MillBlockedUnknown, named);
            }
        }

        /// <summary>
        /// Resume releases a feed hold. Releasing a door hold restarts the spindle, so that
        /// path goes through the run's prompt instead.
        /// </summary>
        [Fact]
        public async Task ResumeDoesNotReleaseADoorHold()
        {
            _web.Grbl.SimulateDoorClosedAndHolding();
            try
            {
                WebServerFixture.WaitUntil(
                    () => MachineWait.GetDoorState(AppState.Machine) == DoorState.WaitingForResume,
                    "the machine to report the door hold");

                int before = _web.Grbl.CycleStartCount;
                long reportsBefore = AppState.Machine.StatusReportCount;

                var (code, body) = await Post(WebConstants.ApiResume);
                Assert.Equal(HttpStatusCode.Conflict, code);
                Assert.Equal(ControllerConstants.ErrorDoorBlocksResume,
                    body.GetProperty("error").GetString());

                // A cycle start already sent would have reached the fake by the next report.
                WebServerFixture.WaitUntil(
                    () => AppState.Machine.StatusReportCount > reportsBefore, "a status report");
                Assert.Equal(before, _web.Grbl.CycleStartCount);
                Assert.True(MachineWait.IsDoor(AppState.Machine));
            }
            finally
            {
                await MachineWait.ReleaseDoorHoldAsync(AppState.Machine, ControllerConstants.DoorResumeTimeoutMs);
                WebServerFixture.WaitUntil(() => !MachineWait.IsDoor(AppState.Machine), "the door hold to clear");
            }
        }

        /// <summary>
        /// Closing the enclosure leaves GRBL holding until a cycle start, so a check in front
        /// of the run would still block after the operator did what it asked. The run prompts
        /// about the enclosure and releases the hold instead.
        /// </summary>
        [Fact]
        public async Task ADoorHoldDoesNotBlockTheProbe_TheRunPromptsInstead()
        {
            await GivenAGridIsReady();

            _web.Grbl.SimulateDoorClosedAndHolding();
            WebServerFixture.WaitUntil(
                () => MachineWait.GetDoorState(AppState.Machine) == DoorState.WaitingForResume,
                "the machine to report the door hold");

            try
            {
                Assert.Null(MenuHelpers.GetProbeDisabledReason());

                var (code, started) = await Post(WebConstants.ApiProbeStart);
                Assert.Equal(HttpStatusCode.OK, code);
                Assert.True(Flag(started, "success"),
                    "the probe refused a door hold the run would have asked about: "
                    + (started.TryGetProperty("error", out var why) ? why.GetString() : "no reason given"));

                WebServerFixture.WaitUntil(
                    () => AppState.Probe.State == ControllerState.WaitingForUserInput,
                    "the run to ask about the enclosure");
            }
            finally
            {
                await Post(WebConstants.ApiProbeStop);
                WebServerFixture.WaitUntil(() => !AppState.Probe.IsRunInProgress, "the run to end");
                await MachineWait.ReleaseDoorHoldAsync(AppState.Machine, ControllerConstants.DoorResumeTimeoutMs);
                WebServerFixture.WaitUntil(() => !MachineWait.IsDoor(AppState.Machine), "the door hold to clear");
            }
        }

        /// <summary>
        /// Opening the enclosure and closing it again leaves GRBL in Door, waiting for a
        /// cycle start. Nothing the operator can do at the machine clears that state, so the
        /// start has to reach the milling controller, which prompts and sends the cycle start.
        /// </summary>
        [Fact]
        public async Task ADoorHoldDoesNotBlockTheMill_TheControllerPromptsInstead()
        {
            await GivenAGridIsReady();
            GivenACompleteAutosaveForThisJob();

            var (_, applied) = await Post(WebConstants.ApiProbeApply);
            Assert.True(Flag(applied, "success"), "the map could not be applied");

            _web.Grbl.SimulateDoorClosedAndHolding();
            WebServerFixture.WaitUntil(
                () => MachineWait.GetDoorState(AppState.Machine) == DoorState.WaitingForResume,
                "the machine to report the door hold");

            try
            {
                var (code, started) = await Post(WebConstants.ApiMillStart);
                Assert.Equal(HttpStatusCode.OK, code);
                Assert.True(Flag(started, "success"),
                    "the mill refused a door hold the controller would have asked about: "
                    + (started.TryGetProperty("error", out var why) ? why.GetString() : "no reason given"));

                WebServerFixture.WaitUntil(
                    () => AppState.Milling.State == ControllerState.WaitingForUserInput,
                    "the run to ask about the enclosure");
            }
            finally
            {
                // The fixture's machine is shared; left in Door it fails every test that runs
                // after this one.
                await Post(WebConstants.ApiMillStop);
                WebServerFixture.WaitUntil(() => !AppState.Milling.IsRunInProgress, "the run to end");
                await MachineWait.ReleaseDoorHoldAsync(AppState.Machine, ControllerConstants.DoorResumeTimeoutMs);
                WebServerFixture.WaitUntil(() => !MachineWait.IsDoor(AppState.Machine), "the door hold to clear");
            }
        }

        /// <summary>
        /// A UNC path names another host, and listing it sends the request thread to that
        /// host's file server.
        /// </summary>
        [Theory]
        [InlineData(@"\\attacker.example\share")]
        [InlineData("//attacker.example/share")]
        public void APathOnAnotherHost_IsRefused(string path)
        {
            Assert.False(CncWebServer.IsLocalPath(path),
                "the request would reach another host's file server");

            Assert.Null(CncWebServer.ResolveRequestPath(path, "/tmp", out string? refused));
            Assert.Equal(WebConstants.ErrorPathNotOnThisComputer, refused);
        }

        [Fact]
        public void ARelativePath_IsRootedAgainstTheBrowseDirectory()
        {
            string? path = CncWebServer.ResolveRequestPath("board.nc", "/tmp", out string? refused);

            Assert.Null(refused);
            Assert.Equal(Path.Combine("/tmp", "board.nc"), path);
        }

        [Theory]
        [InlineData("~")]
        [InlineData("~/")]
        public void AHomePath_ResolvesToTheHomeDirectory(string path)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            Assert.Equal(home, CncWebServer.ResolveRequestPath(path, null, out string? refused));
            Assert.Null(refused);
        }

        [Fact]
        public void TildePath_PreservesRelativeSuffix()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            Assert.Equal(
                Path.Combine(home, "pcb", "board.nc"),
                CncWebServer.ResolveRequestPath("~/pcb/board.nc", null, out _));
        }

        [Fact]
        public void ANamelessPath_IsRefused()
        {
            Assert.Null(CncWebServer.ResolveRequestPath("", "/tmp", out string? refused));
            Assert.Equal(WebConstants.ErrorNoPathSpecified, refused);
        }

        /// <summary>
        /// A probe stop with no probe run in progress sends a soft reset. During a mill run
        /// that aborts the cut, and the unlock after it clears the alarm before the milling
        /// monitor reads it.
        /// </summary>
        [Fact]
        public async Task ProbeStopDuringMill_DoesNotStopMill()
        {
            await WhileAMillRunHoldsAtTheDoor(async () =>
            {
                int resets = _web.Grbl.SoftResetCount;

                var (code, body) = await Post(WebConstants.ApiProbeStop);

                Assert.Equal(HttpStatusCode.Conflict, code);
                Assert.Equal(WebConstants.ErrorMachineBusy, body.GetProperty("error").GetString());
                Assert.Equal(resets, _web.Grbl.SoftResetCount);
                Assert.True(AppState.Milling.IsRunInProgress, "the mill run was ended by a probe stop");
            });
        }

        /// <summary>
        /// Each of these settings reaches the machine as a number in a G-code line or a move,
        /// so a zero feed or a negative height returns 400 before anything is stored.
        /// </summary>
        [Theory]
        [InlineData("probeFeed", 0.0, "Probe feed", "mm/min")]
        [InlineData("probeFeed", -20.0, "Probe feed", "mm/min")]
        [InlineData("probeMaxDepth", 0.0, "Probe max depth", "mm")]
        [InlineData("probeSafeHeight", 0.0, "Probe safe height", "mm")]
        [InlineData("probeMinimumHeight", -1.0, "Probe minimum height", "mm")]
        [InlineData("outlineTraceHeight", -1.0, "Outline trace height", "mm")]
        [InlineData("outlineTraceFeed", 0.0, "Outline trace feed", "mm/min")]
        public async Task ASettingTheMachineCannotWorkTo_IsRefused(
            string field, double value, string name, string unit)
        {
            var before = JsonSerializer.Serialize(AppState.Settings);

            var (code, body) = await Post(
                WebConstants.ApiSettings,
                JsonSerializer.Deserialize<object>($"{{\"{field}\":{value.ToString(CultureInfo.InvariantCulture)}}}"));

            Assert.Equal(HttpStatusCode.BadRequest, code);

            // The exact message, so a request that failed to parse - also a 400 - cannot
            // satisfy this.
            Assert.Equal(
                string.Format(SettingsText.MustBePositive, name, unit),
                body.GetProperty("error").GetString());
            Assert.Equal(before, JsonSerializer.Serialize(AppState.Settings));
        }

        /// <summary>
        /// The settings handler pairs request fields with properties by hand, so a crossed
        /// pair stores a value in another setting's place with nothing to show for it.
        /// </summary>
        [Fact]
        public async Task EachSettingInRequest_UpdatesMatchingProperty()
        {
            // Distinct values, so a crossed pair cannot read back as the right one.
            var sent = new
            {
                probeFeed = 21.0,
                probeMaxDepth = 22.0,
                probeSafeHeight = 23.0,
                probeMinimumHeight = 24.0,
                outlineTraceHeight = 25.0,
                outlineTraceFeed = 26.0,
                toolSetterX = 27.0,
                toolSetterY = 28.0
            };

            var (code, _) = await Post(WebConstants.ApiSettings, sent);
            Assert.Equal(HttpStatusCode.OK, code);

            var settings = AppState.Settings;
            Assert.Equal(sent.probeFeed, settings.ProbeFeed);
            Assert.Equal(sent.probeMaxDepth, settings.ProbeMaxDepth);
            Assert.Equal(sent.probeSafeHeight, settings.ProbeSafeHeight);
            Assert.Equal(sent.probeMinimumHeight, settings.ProbeMinimumHeight);
            Assert.Equal(sent.outlineTraceHeight, settings.OutlineTraceHeight);
            Assert.Equal(sent.outlineTraceFeed, settings.OutlineTraceFeed);
            Assert.Equal(sent.toolSetterX, settings.ToolSetterX);
            Assert.Equal(sent.toolSetterY, settings.ToolSetterY);
        }

        /// <summary>
        /// An unknown profile would turn the tool setter off, and the next tool change would
        /// stop using it with no message to the operator.
        /// </summary>
        [Fact]
        public async Task AProfileTheMachineDoesNotHave_IsRefused()
        {
            string before = AppState.Settings.MachineProfile;

            var (code, body) = await Post(
                WebConstants.ApiSettings, new { machineProfile = "not-a-machine" });

            Assert.Equal(HttpStatusCode.BadRequest, code);
            Assert.Equal(
                WebConstants.ErrorUnknownMachineProfile, body.GetProperty("error").GetString());
            Assert.Equal(before, AppState.Settings.MachineProfile);
        }

        /// <summary>
        /// The whole request is validated before any of it is stored. Applied one field at a
        /// time, the machine would work to a mix of old and new settings.
        /// </summary>
        [Fact]
        public async Task OneUnusableSettingInARequest_StoresNoneOfIt()
        {
            double feedBefore = AppState.Settings.ProbeFeed;

            var (code, _) = await Post(
                WebConstants.ApiSettings,
                new { probeFeed = feedBefore + 1, probeMaxDepth = 0.0 });

            Assert.Equal(HttpStatusCode.BadRequest, code);
            Assert.Equal(feedBefore, AppState.Settings.ProbeFeed);
        }

        [Fact]
        public async Task ZeroingWithNoMap_ReportsNothingToDo()
        {
            await GivenAGridIsReady();
            AppState.DiscardProbeDataAndAutosave();

            Assert.Equal(WorkZeroOutcome.NothingToDo, AppState.HandleWorkZeroChange("Z0"));
        }

        [Fact]
        public async Task ZeroingXYWithAMap_ReportsItDiscarded()
        {
            string board = await GivenAGridIsReadyAndTheBoardStays();
            try
            {
                GivenACompleteAutosaveForThisJob();
                Assert.True(Flag((await Post(WebConstants.ApiProbeApply)).Body, "success"),
                    "the map could not be applied");

                Assert.Equal(WorkZeroOutcome.MapDiscarded, AppState.HandleWorkZeroChange("X0 Y0"));
                Assert.Null(AppState.ProbePoints);
                Assert.False(AppState.AreProbePointsApplied);
                Assert.Null(AppState.ReadUsableAutosave());
            }
            finally
            {
                File.Delete(board);
            }
        }

        /// <summary>
        /// The browser draws what became of the height map from the heightMap field. Without
        /// it the operator is not told the corrections in the file are wrong.
        /// </summary>
        [Fact]
        public async Task ZeroResponse_IncludesHeightMapOutcome()
        {
            string board = await GivenAGridIsReadyAndTheBoardStays();
            try
            {
                GivenACompleteAutosaveForThisJob();
                Assert.True(Flag((await Post(WebConstants.ApiProbeApply)).Body, "success"),
                    "the map could not be applied");

                var (code, body) = await Post(WebConstants.ApiZero, new { axes = new[] { "X", "Y", "Z" } });

                Assert.Equal(HttpStatusCode.OK, code);
                Assert.Equal(
                    nameof(WorkZeroOutcome.MapDiscarded), body.GetProperty("heightMap").GetString());
                Assert.False(Flag(body, "reloadTheFile"),
                    "the map came out of the G-code, so there is nothing to reload");
            }
            finally
            {
                File.Delete(board);
            }
        }

        /// <summary>
        /// Zeroing Y alone moves the datum as zeroing X does. The map's points are measured
        /// from the origin, so it no longer describes the board.
        /// </summary>
        [Fact]
        public async Task ZeroingOnlyY_DiscardsTheMap()
        {
            await GivenAGridIsReady();
            GivenACompleteAutosaveForThisJob();

            Assert.Equal(WorkZeroOutcome.MapDiscarded, AppState.HandleWorkZeroChange("Y0"));
            Assert.Null(AppState.ReadUsableAutosave());
        }

        /// <summary>
        /// Refuse Y zero during a run because the streamed file already contains
        /// corrections from the current height map.
        /// </summary>
        [Fact]
        public async Task ZeroingOnlyYDuringARun_IsRefused()
        {
            await WhileAMillRunHoldsAtTheDoor(() =>
            {
                Assert.Equal(
                    CliConstants.ErrorZeroXYDuringRun,
                    MachineCommands.SetWorkZeroAndWait(AppState.Machine, "Y0").Refused);
                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// A Z-only zero leaves X and Y wherever they were, so only a full X, Y and Z origin
        /// is stored for the next session.
        /// </summary>
        [Theory]
        [InlineData("X0 Y0 Z0", true)]
        [InlineData("Z0", false)]
        public async Task OnlyAFullOrigin_IsRememberedForTheNextSession(string axes, bool remembered)
        {
            await GivenAGridIsReady();
            AppState.Session.HasStoredWorkZero = false;

            // A refused zero also leaves HasStoredWorkZero false, so the Z0 row would pass
            // for the wrong reason.
            Assert.Null(MachineCommands.SetWorkZeroAndWait(AppState.Machine, axes).Refused);

            Assert.Equal(remembered, AppState.Session.HasStoredWorkZero);
        }

        /// <summary>
        /// Swapping the map during a run leaves AppState recording no applied map while the
        /// machine cuts one, and the next apply doubles the corrections.
        /// </summary>
        [Fact]
        public async Task EveryWayToChangeTheMapDuringARun_IsRefused()
        {
            string gridFile = Path.Combine(
                Path.GetTempPath(), "coppercli-grid-" + Guid.NewGuid().ToString("N") + ".hmap");

            await WhileAMillRunHoldsAtTheDoor(() =>
            {
                AppState.ProbePoints!.Save(gridFile);

                var map = AppState.ProbePoints;
                var loaded = AppState.Machine.File;
                AppState.Machine.FileGoto(1);
                string why = CliConstants.ErrorFileChangeDuringRun;

                Assert.Equal(why, AppState.LoadProbeGridFromFile(gridFile).Refused);
                Assert.Equal(why, AppState.ForceLoadProbeFromAutosave().Refused);
                Assert.Equal(why, AppState.DiscardProbeData());
                Assert.Equal(why, AppState.AdoptProbeGrid(null));
                Assert.Equal(why, AppState.SetupProbeGrid(
                    new Vector2(0, 0), new Vector2(10, 10), margin: 1.0, gridSize: 5.0).Refused);

                // With a map already loaded there is nothing to adopt, so this one does
                // nothing instead of returning the refusal.
                Assert.Null(AppState.EnsureProbeDataLoaded());

                Assert.Same(map, AppState.ProbePoints);
                Assert.True(AppState.AreProbePointsApplied,
                    "the run is cutting the map and AppState stopped saying so");
                Assert.Same(loaded, AppState.Machine.File);
                Assert.Equal(1, AppState.Machine.FilePosition);
                return Task.CompletedTask;
            });

            File.Delete(gridFile);
        }

        /// <summary>
        /// The probe panel and the Mill button in one status payload are built from a single
        /// read of the map. Two reads could report a complete map in one field and none in
        /// the other, and would parse the autosave twice per broadcast.
        /// </summary>
        [Fact]
        public async Task Status_UsesOneProbeMapSnapshot()
        {
            await GivenAGridIsReady();
            GivenACompleteAutosaveForThisJob();
            AppState.DiscardProbeData();

            var status = await GetJson(WebConstants.ApiStatus);

            Assert.Equal(
                WebConstants.ProbeStateComplete,
                status.GetProperty("probe").GetProperty("state").GetString());

            // A complete map that is not applied is what blocks the mill, so the button field
            // has to come from the same read as the panel field.
            Assert.False(
                status.GetProperty("buttons").GetProperty("mill").GetProperty("enabled").GetBoolean(),
                "the Mill button was cleared while the probe panel reported an unapplied map");
        }

        /// <summary>
        /// Saving the height map to a file has to leave the autosave in place. The mill is
        /// blocked by a complete map that is not applied, and a save that consumed the map
        /// would clear that block.
        /// </summary>
        [Fact]
        public async Task SavingAutosavedMap_RetainsMapForJob()
        {
            await GivenAGridIsReady();
            GivenACompleteAutosaveForThisJob();

            Assert.Null(AppState.ProbePoints);
            Assert.Equal(MillBlocker.ProbeNotApplied, MenuHelpers.CheckMillCanStart().Error);

            string saved = Path.Combine(
                Path.GetTempPath(), "coppercli-saved-" + Guid.NewGuid().ToString("N") + ".pgrid");

            try
            {
                Assert.True(Persistence.SaveProbeToFile(saved));

                Assert.Equal(
                    MillBlocker.ProbeNotApplied,
                    MenuHelpers.CheckMillCanStart().Error);
            }
            finally
            {
                File.Delete(saved);
            }
        }

        /// <summary>
        /// Loading a board drops a map measured for another one, and the droppedMap field
        /// carries the reason. Without it the browser has no reason to show.
        /// </summary>
        [Fact]
        public async Task ALoadThatDropsTheMap_IsReportedInTheResponse()
        {
            string first = await GivenAGridIsReadyAndTheBoardStays();
            string second = Path.Combine(
                Path.GetTempPath(), "coppercli-other-" + Guid.NewGuid().ToString("N") + ".ngc");

            try
            {
                File.WriteAllText(second, "G21\nG90\nG0 X0 Y0 Z1\nG1 Z-0.1 F100\nG1 X5 Y5\nM2\n");
                Assert.Null(AppState.AdoptProbeGrid(CompleteMapForThisJob()));

                var (code, body) = await Post(WebConstants.ApiFileLoad, new { path = second });

                Assert.Equal(HttpStatusCode.OK, code);
                Assert.Equal(
                    AppState.GetInapplicableReason(ProbeApplicability.DifferentFile, first),
                    body.GetProperty("droppedMap").GetString());
            }
            finally
            {
                File.Delete(first);
                File.Delete(second);
            }
        }

        /// <summary>
        /// CheckMillCanStart judges the map passed to it. A second read of the autosave
        /// inside the check lets the status describe one map in the probe panel and another
        /// in the Mill button.
        /// </summary>
        [Fact]
        public async Task CheckMillCanStart_UsesProvidedMap()
        {
            await GivenAGridIsReady();
            AppState.DiscardProbeData();
            Persistence.ClearProbeAutoSave();

            // With nothing in memory and nothing on disk, a check that reads for itself finds
            // no map and clears the mill to start.
            Assert.Null(AppState.CurrentProbeGrid);
            Assert.Equal(MillBlocker.None, MenuHelpers.CheckMillCanStart().Error);

            Assert.Equal(
                MillBlocker.ProbeNotApplied,
                MenuHelpers.CheckMillCanStart(CompleteMapForThisJob()).Error);
        }

        /// <summary>
        /// A single Z probe runs with no controller behind it, so IsRunInProgress stays
        /// false. OperatingMode.Probe on the machine is the record that the tool is
        /// descending, and a command of the operator's own returns 409 while it is set.
        /// </summary>
        [Fact]
        public async Task ZeroCommandDuringProbeCycle_IsRefused()
        {
            await GivenAGridIsReady();
            Assert.False(MachineWait.IsProbeCycleOpen(AppState.Machine));

            AppState.Machine.ProbeStart();
            try
            {
                Assert.True(MachineWait.IsProbeCycleOpen(AppState.Machine),
                    "the machine does not record that a probe cycle is open");

                var (code, body) = await Post(
                    WebConstants.ApiZero, new { axes = new[] { "Z" } });

                Assert.Equal(HttpStatusCode.Conflict, code);
                Assert.Equal(
                    WebConstants.ErrorMachineBusy, body.GetProperty("error").GetString());
            }
            finally
            {
                AppState.Machine.ProbeStop();
            }
        }

        /// <summary>
        /// Every endpoint that changes the map returns 409 with ErrorFileChangeDuringRun, so
        /// the operator reads the same reason whichever button they pressed.
        /// </summary>
        [Fact]
        public async Task EveryEndpointThatChangesTheMapDuringARun_RefusesInTheSameWords()
        {
            string gridFile = Path.Combine(
                Path.GetTempPath(), "coppercli-grid-" + Guid.NewGuid().ToString("N") + ".hmap");

            await WhileAMillRunHoldsAtTheDoor(async () =>
            {
                AppState.ProbePoints!.Save(gridFile);

                var posts = new (string Path, object? Body)[]
                {
                    (WebConstants.ApiProbeSetup, new { margin = 1.0, gridSize = 20.0 }),
                    (WebConstants.ApiProbeLoad, new { path = gridFile }),
                    (WebConstants.ApiProbeApply, null),
                    (WebConstants.ApiProbeDiscard, null),
                    (WebConstants.ApiProbeRecoverAutosave, null)
                };

                foreach (var (path, body) in posts)
                {
                    var (code, response) = body == null ? await Post(path) : await Post(path, body);

                    Assert.Equal(HttpStatusCode.Conflict, code);
                    Assert.Equal(
                        CliConstants.ErrorFileChangeDuringRun,
                        response.GetProperty("error").GetString());
                }
            });

            File.Delete(gridFile);
        }

        /// <summary>
        /// The probe screen loads, applies and discards the map, so it is disabled for the
        /// whole of a run rather than refusing each choice one at a time.
        /// </summary>
        [Fact]
        public async Task TheProbeScreen_IsClosedWhileARunIsStreamingTheFile()
        {
            await WhileAMillRunHoldsAtTheDoor(() =>
            {
                Assert.Equal(CliConstants.DisabledRunInProgress, MenuHelpers.GetProbeDisabledReason());
                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// The browser turns its confirmation into a warning from the reloadTheFile field.
        /// Without it the operator reads an ordinary confirmation and cuts with the old
        /// origin's corrections.
        /// </summary>
        [Fact]
        public async Task TheZeroResponse_RequiresAReloadWhenTheGCodeIsWrong()
        {
            await GivenAGridIsReady();
            GivenACompleteAutosaveForThisJob();
            Assert.True(Flag((await Post(WebConstants.ApiProbeApply)).Body, "success"),
                "the map could not be applied");

            // GivenAGridIsReady deletes its board, so the corrections cannot be taken out.
            var (code, body) = await Post(WebConstants.ApiZero, new { axes = new[] { "X", "Y", "Z" } });

            Assert.Equal(HttpStatusCode.OK, code);
            Assert.Equal(
                nameof(WorkZeroOutcome.MapNotDiscarded), body.GetProperty("heightMap").GetString());
            Assert.True(Flag(body, "reloadTheFile"),
                "the G-code still holds the old origin's corrections and nothing says so");
        }

        /// <summary>
        /// An outcome added to WorkZeroOutcome but missing from /api/constants leaves the
        /// browser with no text for what became of the map.
        /// </summary>
        [Fact]
        public async Task EveryHeightMapOutcome_IsPublished()
        {
            var published = (await GetJson(WebConstants.ApiConstants))
                .GetProperty("heightMapOutcomes")
                .EnumerateObject()
                .Select(field => field.Value.GetString())
                .ToHashSet();

            foreach (var outcome in Enum.GetValues<WorkZeroOutcome>())
            {
                // NothingToDo needs no text: the map did not change.
                if (outcome == WorkZeroOutcome.NothingToDo)
                {
                    continue;
                }

                Assert.Contains(outcome.ToString(), published);
            }
        }

        [Fact]
        public async Task ZeroingXYWithNoMap_ReportsNothingToDo()
        {
            await GivenAGridIsReady();
            AppState.DiscardProbeDataAndAutosave();

            Assert.Equal(WorkZeroOutcome.NothingToDo, AppState.HandleWorkZeroChange("X0 Y0"));
        }

        /// <summary>
        /// A finished map in the autosave counts with nothing in memory. The browser warns
        /// the operator the map will be invalidated, so the zero has to delete the autosave.
        /// </summary>
        [Fact]
        public async Task ZeroingXYWithOnlyAnAutosavedMap_DiscardsIt()
        {
            await GivenAGridIsReady();
            GivenACompleteAutosaveForThisJob();

            Assert.NotNull(AppState.ReadUsableAutosave());
            Assert.Null(AppState.ProbePoints);

            Assert.Equal(WorkZeroOutcome.MapDiscarded, AppState.HandleWorkZeroChange("X0 Y0"));
            Assert.Null(AppState.ReadUsableAutosave());
        }

        /// <summary>
        /// With the map applied and the source file gone, the corrections cannot be taken
        /// back out of the loaded G-code. Reporting MapDiscarded would leave the operator
        /// cutting with them.
        /// </summary>
        [Fact]
        public async Task ZeroingXYWhenTheMapCannotBeRemoved_IsReported()
        {
            await GivenAGridIsReady();
            GivenACompleteAutosaveForThisJob();
            Assert.True(Flag((await Post(WebConstants.ApiProbeApply)).Body, "success"),
                "the map could not be applied");

            // GivenAGridIsReady deletes its board, so the original is already gone.
            Assert.False(string.IsNullOrEmpty(AppState.Session.LastLoadedGCodeFile));
            Assert.False(File.Exists(AppState.Session.LastLoadedGCodeFile));

            Assert.Equal(WorkZeroOutcome.MapNotDiscarded, AppState.HandleWorkZeroChange("X0 Y0"));
            Assert.True(AppState.AreProbePointsApplied,
                "the corrections are still in the G-code, so AppState must still say so");
        }

        /// <summary>
        /// A Z zero reloads the G-code from the source file to strip the old corrections.
        /// With that file gone the map cannot be re-applied and the loaded G-code still holds
        /// them, so the outcome is MapNotReapplied.
        /// </summary>
        [Fact]
        public async Task ZeroingZWhenTheSourceFileIsGone_ReportsTheMapWasNotReapplied()
        {
            await GivenAGridIsReady();
            GivenACompleteAutosaveForThisJob();
            Assert.True(Flag((await Post(WebConstants.ApiProbeApply)).Body, "success"),
                "the map could not be applied");

            // GivenAGridIsReady deletes its board, so the source is already missing.
            Assert.False(string.IsNullOrEmpty(AppState.Session.LastLoadedGCodeFile));
            Assert.False(File.Exists(AppState.Session.LastLoadedGCodeFile));

            Assert.Equal(WorkZeroOutcome.MapNotReapplied, AppState.HandleWorkZeroChange("Z0"));
            Assert.True(AppState.AreProbePointsApplied,
                "the map is still in the G-code, so AppState must still say so");
        }

        /// <summary>
        /// Applying a map rewrites the loaded file and resets its line count, so it is
        /// refused during a run for the same reason a load is.
        /// </summary>
        [Fact]
        public async Task ApplyingAMapDuringARun_IsRefused()
        {
            await WhileAMillRunHoldsAtTheDoor(() =>
            {
                var loaded = AppState.Machine.File;
                AppState.Machine.FileGoto(1);

                // The map is still applied here, so the refusal has to come before
                // ApplyProbeData's already-applied early return.
                Assert.Equal(
                    CliConstants.ErrorFileChangeDuringRun, AppState.ApplyProbeData());

                Assert.Same(loaded, AppState.Machine.File);
                Assert.Equal(1, AppState.Machine.FilePosition);
                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// Discarding deletes the autosave and then reloads the G-code without the map. The
        /// refusal during a run comes before the delete, so both copies stay.
        /// </summary>
        [Fact]
        public async Task DiscardDuringRun_PreservesAutosave()
        {
            await WhileAMillRunHoldsAtTheDoor(() =>
            {
                var map = AppState.ProbePoints;

                Assert.Equal(
                    CliConstants.ErrorFileChangeDuringRun,
                    AppState.DiscardProbeDataAndAutosave());

                Assert.Same(map, AppState.ProbePoints);
                Assert.True(AppState.AreProbePointsApplied,
                    "the map was removed while a run was cutting with it");
                Assert.NotNull(AppState.ReadUsableAutosave());
                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// A run tracks its place in the loaded file by line number. Replacing the file
        /// resets that number and would restart the job.
        /// </summary>
        [Fact]
        public async Task MillRun_RejectsLoadedFileReplacement()
        {
            await WhileAMillRunHoldsAtTheDoor(() =>
            {
                var loaded = AppState.Machine.File;
                AppState.Machine.FileGoto(1);

                // Every loader calls LoadGCodeIntoMachine. Even the current file is
                // refused because reloading resets the line count.
                Assert.Equal(
                    CliConstants.ErrorFileChangeDuringRun,
                    AppState.LoadGCodeIntoMachine(AppState.CurrentFile!).Refused);

                Assert.Same(loaded, AppState.Machine.File);
                Assert.Equal(1, AppState.Machine.FilePosition);
                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// A probe run blocks the same changes a mill run does. Without this the probe term
        /// of IsRunInProgress could be dropped with nothing failing.
        /// </summary>
        [Fact]
        public async Task ProbeRun_RejectsFileAndOriginChanges()
        {
            await GivenAGridIsReady();

            _web.Grbl.SimulateDoorClosedAndHolding();
            WebServerFixture.WaitUntil(
                () => MachineWait.GetDoorState(AppState.Machine) == DoorState.WaitingForResume,
                "the door hold");

            try
            {
                await Post(WebConstants.ApiProbeStart);
                WebServerFixture.WaitUntil(
                    () => PendingPrompt.Current?.IsDoorPrompt == true,
                    "the probe run to ask about the enclosure");

                Assert.Equal(
                    CliConstants.ErrorFileChangeDuringRun,
                    AppState.LoadGCodeIntoMachine(AppState.CurrentFile!).Refused);

                Assert.Equal(
                    CliConstants.ErrorZeroXYDuringRun,
                    MachineCommands.SetWorkZeroAndWait(AppState.Machine, "X0 Y0").Refused);
            }
            finally
            {
                await Post(WebConstants.ApiProbeStop);
                WebServerFixture.WaitUntil(() => !AppState.Probe.IsRunInProgress, "the run to end");
                await MachineWait.ReleaseDoorHoldAsync(AppState.Machine, ControllerConstants.DoorResumeTimeoutMs);
                WebServerFixture.WaitUntil(() => !MachineWait.IsDoor(AppState.Machine), "the door hold to clear");
            }
        }

        /// <summary>
        /// Zeroing changes the height map's work origin. During a run, keep the map and
        /// streamed file unchanged because that file already contains its corrections.
        /// </summary>
        [Fact]
        public async Task ZeroDuringRun_PreservesAppliedMapAndLoadedFile()
        {
            await WhileAMillRunHoldsAtTheDoor(() =>
            {
                var loaded = AppState.Machine.File;
                var map = AppState.ProbePoints;
                AppState.Machine.FileGoto(1);

                // X or Y moves the datum, so it is refused before the offset is written.
                Assert.Equal(
                    CliConstants.ErrorZeroXYDuringRun,
                    MachineCommands.SetWorkZeroAndWait(AppState.Machine, "X0 Y0 Z0").Refused);

                Assert.Same(map, AppState.ProbePoints);
                Assert.True(AppState.AreProbePointsApplied,
                    "the map was removed while a run was cutting with it");

                // A tool change needs a Z zero, so Z alone goes through and the file stays.
                var zeroed = MachineCommands.SetWorkZeroAndWait(AppState.Machine, "Z0");
                Assert.Null(zeroed.Refused);
                Assert.Equal(WorkZeroOutcome.FileLeftAlone, zeroed.Outcome);

                Assert.Same(loaded, AppState.Machine.File);
                Assert.Equal(1, AppState.Machine.FilePosition);
                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// The points in a map measured from a different G54 offset land elsewhere on the
        /// board, so the status does not report it.
        /// </summary>
        [Fact]
        public async Task AnAutosaveMeasuredBeforeTheOriginMoved_IsNotReportedAsProbeData()
        {
            await GivenAGridIsReady();

            var moved = new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20))
            {
                Context = new ProbeContext(AppState.Session.LastLoadedGCodeFile!, new Vector3(10, 0, 0))
            };
            moved.RecordMeasurement(0, 0, -0.1);

            AppState.DiscardProbeData();
            moved.Save(Persistence.GetProbeAutoSavePath());

            var status = await GetJson(WebConstants.ApiProbeStatus);

            Assert.Equal(WebConstants.ProbeStateNone, status.GetProperty("state").GetString());
            Assert.False(status.GetProperty("hasUnsavedData").GetBoolean());
        }

        [Fact]
        public async Task StoppingWhenNothingIsRunning_LeavesTheProbeStartable()
        {
            await GivenAGridIsReady();
            await StopTheProbeAndWaitForIdle();

            var (code, body) = await Post(WebConstants.ApiProbeStart);
            Assert.True(Flag(body, "success"), $"start after an idle stop refused: {code} {body}");

            await StopTheProbeAndWaitForIdle();
        }
    }
}
