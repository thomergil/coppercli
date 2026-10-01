#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using coppercli.Core.Controllers;
using coppercli.Core.GCode;
using coppercli.WebServer;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// A browser start confirms the version of the job the operator checked. The start is
    /// refused when the file, the applied map or the depth changed since, and two starts at
    /// once start one run.
    /// </summary>
    [Collection(WebServerCollection.Name)]
    public class MillStartTests : IDisposable
    {
        private static readonly string[] Board =
        {
            "G21", "G90", "G0 X0 Y0 Z1", "G1 Z-0.1 F100", "G1 X10 Y0", "G0 Z1"
        };

        private const double TestDepth = -0.10;
        private const double Tolerance = 1e-9;
        private const string ChangeDepth = "depth";
        private const string ChangeFile = "file";
        private const string ChangeMap = "map";

        private readonly WebServerFixture _web;
        private readonly List<string> _files = new();

        public MillStartTests(WebServerFixture web)
        {
            _web = web;
            _web.RestoreFixtureState();
        }

        public void Dispose()
        {
            AppState.DiscardProbeData();
            Persistence.ClearProbeAutoSave();
            foreach (string file in _files)
            {
                File.Delete(file);
            }
        }

        private HttpClient Client => _web.Client;

        private GCodeFile GivenTheBoardIsLoaded()
        {
            string path = Path.Combine(Path.GetTempPath(), "coppercli-start-" + Guid.NewGuid().ToString("N") + ".ngc");
            File.WriteAllLines(path, Board);
            _files.Add(path);
            AppState.MarkWorkZeroSet();
            var file = GCodeFile.Load(path);
            Assert.Null(AppState.LoadGCodeIntoMachine(file).Refused);
            return file;
        }

        private async Task<(HttpStatusCode Code, string? Error)> Start(object body)
        {
            var response = await Client.PostAsJsonAsync(WebConstants.ApiMillStart, body);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return (response.StatusCode,
                json.RootElement.TryGetProperty(WebConstants.JsonFieldError, out var error) ? error.GetString() : null);
        }

        /// <summary>
        /// A start that does not name the version the operator checked could run a job they
        /// never saw, so it is a bad request and no run starts.
        /// </summary>
        [Fact]
        public async Task AStartThatNamesNoVersion_IsRefused_AndNoRunStarts()
        {
            GivenTheBoardIsLoaded();

            var noBody = await Client.PostAsync(WebConstants.ApiMillStart, null);
            var (code, _) = await Start(new Dictionary<string, object>());

            Assert.Equal(HttpStatusCode.BadRequest, noBody.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, code);
            Assert.False(AppState.Milling.IsRunInProgress);
        }

        /// <summary>
        /// Another client changed the job between the operator's check and their Start: the
        /// start is refused with 409 and the shared text, and no run starts on a job the
        /// operator did not see.
        /// </summary>
        [Theory]
        [InlineData(ChangeDepth)]
        [InlineData(ChangeFile)]
        [InlineData(ChangeMap)]
        public async Task AStartAfterTheJobChanged_IsRefused_AndNoRunStarts(string change)
        {
            var file = GivenTheBoardIsLoaded();
            if (change == ChangeMap)
            {
                Assert.Null(AppState.AdoptProbeGrid(WebServerFixture.CompleteMapForThisJob()));
                Assert.Null(AppState.ApplyProbeData());
            }

            var checkedBody = await _web.MillStartBodyAsync();

            switch (change)
            {
                case ChangeDepth:
                    Assert.Null(AppState.SetDepthAdjustment(TestDepth));
                    break;
                case ChangeFile:
                    Assert.Null(AppState.LoadGCodeIntoMachine(file).Refused);
                    break;
                default:
                    Assert.Null(AppState.DiscardProbeData());
                    break;
            }

            var (code, error) = await Start(checkedBody);

            Assert.Equal(HttpStatusCode.Conflict, code);
            Assert.Equal(WebConstants.ErrorJobChangedSinceChecked, error);
            Assert.False(AppState.Milling.IsRunInProgress);
        }

        /// <summary>
        /// The version checked after a depth change starts the run at that depth.
        /// </summary>
        [Fact]
        public async Task AStartWithTheCheckedVersion_StartsTheRun_AtThatDepth()
        {
            GivenTheBoardIsLoaded();
            Assert.Null(AppState.SetDepthAdjustment(TestDepth));

            await _web.WhileAMillRunHoldsAtTheDoorAsync(() =>
            {
                Assert.True(AppState.Milling.IsRunInProgress);
                Assert.Equal(TestDepth, AppState.DepthAdjustment, Tolerance);
                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// Two starts at once start one run: the other is refused as a job already running,
        /// and does not cancel or reset the run that started.
        /// </summary>
        [Fact]
        public async Task TwoStartsAtOnce_StartOneRun_AndLeaveItRunning()
        {
            GivenTheBoardIsLoaded();

            await _web.AtAClosedDoorAsync(async () =>
            {
                var body = await _web.MillStartBodyAsync();

                var results = await Task.WhenAll(Start(body), Start(body));

                Assert.Single(results, r => r.Code == HttpStatusCode.OK);
                Assert.Single(results, r => r.Code == HttpStatusCode.Conflict
                    && r.Error == WebConstants.ErrorMillingAlreadyRunning);
                WebServerFixture.WaitUntil(() => AppState.Milling.IsRunInProgress, "the run to take the machine");
                Assert.NotEqual(ControllerState.Cancelled, AppState.Milling.State);
            });
        }
    }
}
