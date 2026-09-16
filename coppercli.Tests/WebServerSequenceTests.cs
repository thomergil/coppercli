#nullable enable
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;
using coppercli;
using coppercli.Core.GCode;
using coppercli.Core.Util;
using coppercli.Tests.Fakes;
using coppercli.WebServer;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// The sequences an operator performs, driven through the real HTTP API.
    /// </summary>
    [Collection(WebServerCollection.Name)]
    public class WebServerSequenceTests
    {
        private readonly WebServerFixture _web;

        public WebServerSequenceTests(WebServerFixture web)
        {
            _web = web;
            _web.TakeBackAppState();
        }

        // =====================================================================
        // Helpers
        // =====================================================================

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

        /// <summary>Loads a board and sets up a grid, so a probe has something to measure.</summary>
        private async Task GivenAGridIsReady()
        {
            string file = Path.Combine(Path.GetTempPath(), "coppercli-smoke-" + Guid.NewGuid().ToString("N") + ".ngc");
            // Big enough for a grid with points to measure.
            File.WriteAllText(file,
                "G21\nG90\nG0 X0 Y0 Z1\nG1 Z-0.1 F100\nG1 X40 Y0\nG1 X40 Y40\n"
                + "G1 X0 Y40\nG1 X0 Y0\nG0 Z1\nM5\nM2\n");

            try
            {
                // The operator zeroes before probing; grid coordinates are work coordinates.
                AppState.WorkZeroWasSet();

                var (loadCode, loadBody) = await Post(WebConstants.ApiFileLoad, new { path = file });
                Assert.True(loadCode == HttpStatusCode.OK && Flag(loadBody, "success"),
                    $"loading the board failed: {loadCode} {loadBody}");

                var (setupCode, setupBody) = await Post(WebConstants.ApiProbeSetup,
                    new { margin = 1.0, gridSize = 20.0 });
                Assert.True(setupCode == HttpStatusCode.OK && Flag(setupBody, "success"),
                    $"probe setup failed: {setupCode} {setupBody}");
            }
            finally
            {
                File.Delete(file);
            }
        }

        /// <summary>Puts a finished map for this job in the autosave, and nothing in memory.</summary>
        private static void GivenACompleteAutosaveForThisJob()
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

            AppState.DiscardProbeData();
            complete.Save(Persistence.GetProbeAutoSavePath());
        }

        private async Task StopTheProbeAndWaitForIdle()
        {
            await Post(WebConstants.ApiProbeStop);
            WebServerFixture.WaitUntil(() => !AppState.Probe.IsRunInProgress,
                "the probe controller to stop claiming the machine");
        }

        // =====================================================================
        // The sequences
        // =====================================================================

        /// <summary>Stop then start is the operator's ordinary loop, so it has to work.</summary>
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
        /// The trace and the grid probe drive one controller, so a stopped trace has to leave
        /// it ready for the next run.
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
        /// A trace moves the tool but measures nothing, so it must not read as probing. Both
        /// endpoints have to answer that the same way.
        /// </summary>
        [Fact]
        public async Task WhileTracing_TheStatusSaysTracingAndNotProbing()
        {
            await GivenAGridIsReady();

            var (traceCode, traceBody) = await Post(WebConstants.ApiProbeTrace);
            Assert.True(Flag(traceBody, "success"), $"trace refused: {traceCode} {traceBody}");

            WebServerFixture.WaitUntil(() => AppState.IsTracingOutline, "the trace to start");

            var status = await GetJson(WebConstants.ApiStatus);
            Assert.True(status.GetProperty("tracingOutline").GetBoolean(), "status did not report the trace");
            Assert.False(status.GetProperty("probing").GetBoolean(), "a trace reported itself as probing");

            // The probe endpoint answers the same question the same way.
            var probeStatus = await GetJson(WebConstants.ApiProbeStatus);
            Assert.False(probeStatus.GetProperty("active").GetBoolean(),
                "the probe status called a trace an active probe");

            await StopTheProbeAndWaitForIdle();
        }

        /// <summary>
        /// A second start is refused while the first run owns the machine, and says so.
        /// </summary>
        [Fact]
        public async Task StartingTwice_IsRefusedWhileTheFirstRunOwnsTheMachine()
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
        /// Stopping has to reach the machine, not just the software, so this asserts at the
        /// wire. GRBL works through its planner buffer whether or not anyone is listening.
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

            // Ordered: the hold decelerates and the reset then ends the job. A reset first
            // would stop the machine where it stands.
            var sent = _web.Grbl.Received;
            int hold = sent.ToList().FindLastIndex(l => l == FakeGrbl.FeedHoldMark);
            int reset = sent.ToList().FindLastIndex(l => l == FakeGrbl.SoftResetMark);
            Assert.True(hold >= 0, "no feed hold reached the machine");
            Assert.True(reset > hold, "the soft reset did not follow the feed hold");
        }

        /// <summary>
        /// A map measured on another board is not the operator's data, so the status does not
        /// offer it. Announcing it puts the recovery modal up over a height map that would cut
        /// this board at the wrong depth.
        /// </summary>
        [Fact]
        public async Task AnAutosaveMeasuredForAnotherBoard_IsNotReportedAsProbeData()
        {
            await GivenAGridIsReady();

            // Clear what setup left in memory, so the status has only the autosave to go on.
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

            // The same response must not offer Recover for the map it just declined to name.
            Assert.False(status.GetProperty("hasUnsavedData").GetBoolean());
        }

        /// <summary>
        /// Probing from an origin nobody set drives the tool to arbitrary XY, so the server
        /// refuses it as the terminal does.
        /// </summary>
        [Fact]
        public async Task StartingAProbe_IsRefusedWhenNoWorkZeroIsSet()
        {
            await GivenAGridIsReady();
            AppState.ForgetWorkZero();

            var (code, body) = await Post(WebConstants.ApiProbeStart);

            Assert.Equal(HttpStatusCode.Conflict, code);
            Assert.False(Flag(body, "success"));
        }

        /// <summary>
        /// A complete map the status announces is one every other gate can act on. While it
        /// sits unapplied the mill must refuse, or the job runs with no height correction.
        /// </summary>
        [Fact]
        public async Task ACompleteMapTheStatusAnnounces_StopsTheMillUntilItIsApplied()
        {
            await GivenAGridIsReady();

            GivenACompleteAutosaveForThisJob();

            var status = await GetJson(WebConstants.ApiProbeStatus);
            Assert.Equal(WebConstants.ProbeStateComplete, status.GetProperty("state").GetString());

            var preflight = await GetJson(WebConstants.ApiMillPreflight);
            Assert.False(preflight.GetProperty("canStart").GetBoolean(),
                "the mill was cleared to run uncorrected while a complete map sat unapplied");
        }

        /// <summary>Reading the status adopts nothing - see rule no-side-effect-on-get.</summary>
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
        /// A map is told apart from one measured elsewhere by the setup stamped on it when it
        /// is created. Unstamped, every later check answers "cannot tell" and waves it through.
        /// </summary>
        [Fact]
        public async Task AGridTheSetupCreates_RecordsTheSetupItWasMeasuredIn()
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
        /// The mill refuses a complete map until it is applied, so applying has to work on the
        /// map the status announced - otherwise the refusal has no way out.
        /// </summary>
        [Fact]
        public async Task ACompleteMapTheStatusAnnounces_CanBeApplied()
        {
            await GivenAGridIsReady();
            GivenACompleteAutosaveForThisJob();

            var status = await GetJson(WebConstants.ApiProbeStatus);
            Assert.Equal(WebConstants.ProbeStateComplete, status.GetProperty("state").GetString());

            var (_, applied) = await Post(WebConstants.ApiProbeApply);
            Assert.True(Flag(applied, "success"),
                "the map the status announced could not be applied, so the mill stays blocked");

            var preflight = await GetJson(WebConstants.ApiMillPreflight);
            Assert.True(preflight.GetProperty("canStart").GetBoolean(),
                "the mill stayed blocked after the announced map was applied");
        }

        /// <summary>
        /// The heights in a map measured from a different origin land somewhere else, so it is
        /// not the operator's data either.
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

        /// <summary>A stop always leaves the probe startable, even with no run going.</summary>
        [Fact]
        public async Task StoppingWhenNothingIsRunning_IsAnsweredAndLeavesTheProbeStartable()
        {
            await GivenAGridIsReady();
            await StopTheProbeAndWaitForIdle();

            var (code, body) = await Post(WebConstants.ApiProbeStart);
            Assert.True(Flag(body, "success"), $"start after an idle stop refused: {code} {body}");

            await StopTheProbeAndWaitForIdle();
        }
    }
}
