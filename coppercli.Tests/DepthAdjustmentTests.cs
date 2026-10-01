#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using coppercli;
using coppercli.Core.Controllers;
using coppercli.Core.GCode;
using coppercli.Core.GCode.GCodeCommands;
using coppercli.Core.Util;
using coppercli.Tests.Fakes;
using coppercli.WebServer;
using Xunit;

namespace coppercli.Tests
{
    // The depth adjustment rebuilds the G-code the machine streams from the loaded file, so
    // every cut goes deeper and nothing else changes. These tests hold the loaded file, the
    // adjusted copy and the machine's lines to that, through AppState, the web API and a run.
    [Collection(WebServerCollection.Name)]
    public class DepthAdjustmentTests : JobFixtureTests
    {
        private const double Tolerance = 1e-9;
        private const double TestDepth = -0.10;
        private const double OtherDepth = -0.26;
        private const double WayPastTheLimit = 50;
        private const int RunWaitTimeoutMs = 30_000;
        private const string ActionUnknown = "sideways";
        private const string JsonFieldDepth = "depth";
        private const int ConcurrencyRounds = 150;
        private const int ConcurrentWorkers = 8;
        private const int OperationsPerWorker = 3;
        private const int OperationKinds = 5;
        private const int BigBoardMoves = 1500;
        private const int RandomSeedBase = 7000;

        private static readonly string[] Board =
        {
            "G21", "G90",
            "G0 X0 Y0 Z1",
            "G1 Z-0.1 F100",
            "G1 X10 Y0",
            "G1 X10 Y10",
            "G0 Z1",
            "G0 X20 Y20",
            "G1 Z-1.7",
            "G1 Z1",
            "G0 X0 Y0",
            "M5"
        };

        private static readonly string[] BoardWithAnXZArc =
        {
            "G21", "G90",
            "G0 X0 Y0 Z1",
            "G18",
            "G2 X5 Y0 Z-1 I2.5 K0 F100"
        };

        private static string[] BigBoard() =>
            new[] { "G21", "G90", "G0 X0 Y0 Z1", "G1 Z-0.1 F100" }
                .Concat(Enumerable.Range(0, BigBoardMoves)
                    .Select(i => FormattableString.Invariant($"G1 X{i % 20} Y{i % 13} Z-0.{1 + i % 5}")))
                .Append("G0 Z1")
                .ToArray();

        private static readonly string[] ShortCut =
        {
            "G21", "G90",
            "G0 X0 Y0 Z1",
            "G1 Z-0.1 F100",
            "G1 X1 Y1",
            "G0 Z1"
        };

        public DepthAdjustmentTests(WebServerFixture web) : base(web)
        {
        }

        private HttpClient Client => _web.Client;

        private async Task<JsonElement> GetJson(string path) => (await _web.GetJsonAsync(path)).Body;

        private Task<(HttpStatusCode Code, JsonElement Body)> Post(string path, object? body = null) =>
            _web.PostJsonAsync(path, body);

        /// <summary>A depth change as the pre-mill window sends it, confirming the version the machine holds now.</summary>
        private Task<(HttpStatusCode Code, JsonElement Body)> PostDepth(string action) =>
            Post(WebConstants.ApiMillDepth, new { action, version = AppState.MachineFileVersion });

        private GCodeFile GivenTheBoardIsLoaded(string[]? lines = null)
        {
            var loaded = _boards.Load(lines ?? Board);
            Assert.Equal(0, AppState.DepthAdjustment);
            return loaded;
        }


        private static string[] GCodeOf(GCodeFile file) => file.GetGCode().ToArray();

        private static string[] RapidLines(IEnumerable<string> lines) =>
            lines.Where(l => l.StartsWith("G0 ", StringComparison.Ordinal)).ToArray();

        private static void AssertEveryCutBelowZeroIsDeeperBy(GCodeFile original, GCodeFile adjusted, double depth)
        {
            Assert.Equal(original.Toolpath.Count, adjusted.Toolpath.Count);
            int cuts = 0;
            for (int i = 0; i < original.Toolpath.Count; i++)
            {
                if (original.Toolpath[i] is not Line before || before.Rapid)
                {
                    continue;
                }

                var after = Assert.IsType<Line>(adjusted.Toolpath[i]);
                double expected = before.End.Z < 0 ? before.End.Z + depth : before.End.Z;
                Assert.Equal(expected, after.End.Z, Tolerance);
                if (before.End.Z < 0)
                {
                    cuts++;
                }
            }

            Assert.True(cuts >= 2, "the board has a cut and a drill below zero");
        }

        /// <summary>
        /// The depth must reach the G-code the machine streams, not just a number on screen:
        /// every cut below zero exactly that much deeper than the loaded file, every rapid as
        /// written. The loaded file itself stays as it was, since it is what every later
        /// change is built from, and the machine's lines are the ones that file produces.
        /// </summary>
        [Fact]
        public void SettingTheDepth_RebuildsTheMachinesGCode_FromTheLoadedFile()
        {
            var loaded = GivenTheBoardIsLoaded();
            string[] original = GCodeOf(loaded);

            Assert.Null(AppState.SetDepthAdjustment(TestDepth));

            Assert.Equal(TestDepth, AppState.DepthAdjustment, Tolerance);
            Assert.Same(loaded, AppState.CurrentFile);
            Assert.Equal(original, GCodeOf(AppState.CurrentFile!));
            AssertEveryCutBelowZeroIsDeeperBy(loaded, AppState.MachineFile!, TestDepth);
            Assert.Equal(GCodeOf(AppState.MachineFile!), OnTheMachine());
            Assert.Equal(RapidLines(original), RapidLines(OnTheMachine()));
            Assert.NotEqual(original, OnTheMachine());
        }

        /// <summary>
        /// The one-step "deeper" is the same operation with a step size, so it must also go
        /// through the rebuild: a step that changed the number and not the lines would cut at
        /// the old depth.
        /// </summary>
        [Fact]
        public void SteppingDeeper_MovesTheCutsByOneIncrement()
        {
            var loaded = GivenTheBoardIsLoaded();

            Assert.Null(AppState.AdjustDepthDeeper());

            Assert.Equal(-CliConstants.DepthAdjustmentIncrement, AppState.DepthAdjustment, Tolerance);
            AssertEveryCutBelowZeroIsDeeperBy(loaded, AppState.MachineFile!, -CliConstants.DepthAdjustmentIncrement);
            Assert.Equal(GCodeOf(AppState.MachineFile!), OnTheMachine());
        }

        /// <summary>
        /// Each change is an offset from the loaded file, not from the last adjusted one: two
        /// changes to the same depth cut that deep once, and going deeper then shallower
        /// returns the original G-code. Stacking would cut a board through on the second press.
        /// </summary>
        [Fact]
        public void RepeatedChanges_DoNotStack()
        {
            var loaded = GivenTheBoardIsLoaded();
            string[] original = GCodeOf(loaded);

            Assert.Null(AppState.SetDepthAdjustment(TestDepth));
            Assert.Null(AppState.SetDepthAdjustment(TestDepth));

            AssertEveryCutBelowZeroIsDeeperBy(loaded, AppState.MachineFile!, TestDepth);
            Assert.Equal(GCodeOf(AppState.MachineFile!), OnTheMachine());

            Assert.Null(AppState.AdjustDepthDeeper());
            Assert.Null(AppState.AdjustDepthShallower());
            AssertEveryCutBelowZeroIsDeeperBy(loaded, AppState.MachineFile!, TestDepth);

            Assert.Null(AppState.SetDepthAdjustment(OtherDepth));
            Assert.Null(AppState.SetDepthAdjustment(0));
            Assert.Equal(original, OnTheMachine());
        }

        /// <summary>
        /// With a map applied, a cut's height is what the file programmed plus the map's local
        /// height plus the depth: the plunge at the first node, where the map reads 0.05, goes
        /// from -0.1 to -0.05 and then to -0.15 with a 0.1 depth. Changing the depth must not
        /// drop the map, and dropping the depth must leave the map's fit alone.
        /// </summary>
        [Fact]
        public void DepthAndMap_Combine_AndChangingDepthKeepsTheMapApplied()
        {
            var loaded = GivenTheBoardIsLoaded();
            Assert.Null(AppState.AdoptProbeGrid(WebServerFixture.CompleteMapForThisJob(0.05, 0.1)));
            Assert.Null(AppState.ApplyProbeData());
            string[] mapOnly = OnTheMachine();
            Assert.Contains("G1 Z-0.05", mapOnly);

            Assert.Null(AppState.SetDepthAdjustment(TestDepth));

            Assert.True(AppState.AreProbePointsApplied, "changing the depth dropped the map");
            Assert.Contains("G1 Z-0.15", OnTheMachine());
            Assert.DoesNotContain("G1 Z-0.05", OnTheMachine());

            // Against the map alone: every feed move is deeper by the depth except the
            // drill's retract above zero, which stays where the map put it.
            var reference = loaded.ApplyProbeGrid(AppState.ProbePoints!);
            var deltas = reference.Toolpath.OfType<Line>().Zip(AppState.MachineFile!.Toolpath.OfType<Line>())
                .Where(pair => !pair.First.Rapid)
                .Select(pair => Math.Round(pair.Second.End.Z - pair.First.End.Z, 6))
                .ToArray();
            Assert.Equal(1, deltas.Count(d => d == 0));
            Assert.All(deltas.Where(d => d != 0), d => Assert.Equal(TestDepth, d, Tolerance));
            Assert.Equal(reference.Toolpath.Count, AppState.MachineFile!.Toolpath.Count);
            Assert.Equal(RapidLines(mapOnly), RapidLines(OnTheMachine()));

            Assert.Null(AppState.SetDepthAdjustment(0));
            Assert.Equal(mapOnly, OnTheMachine());
            Assert.True(AppState.AreProbePointsApplied);
        }

        /// <summary>
        /// A depth picked for one job must not carry to another: loading a file starts again
        /// from zero, with the new file's own G-code on the machine.
        /// </summary>
        [Fact]
        public void LoadingAFile_ResetsTheDepth_AndTheMachinesGCode()
        {
            GivenTheBoardIsLoaded();
            Assert.Null(AppState.SetDepthAdjustment(TestDepth));

            var next = GCodeFile.Load(_boards.Write(ShortCut));
            Assert.Null(AppState.LoadGCodeIntoMachine(next).Refused);

            Assert.Equal(0, AppState.DepthAdjustment);
            Assert.Equal(GCodeOf(next), OnTheMachine());
        }

        /// <summary>
        /// A new map, or none, describes a new surface, so the depth chosen against the old
        /// one is dropped and the machine's lines go back to the file as loaded.
        /// </summary>
        [Fact]
        public void AdoptingOrDiscardingAMap_ResetsTheDepth_AndTheMachinesGCode()
        {
            var loaded = GivenTheBoardIsLoaded();
            string[] original = GCodeOf(loaded);

            Assert.Null(AppState.SetDepthAdjustment(TestDepth));
            Assert.Null(AppState.AdoptProbeGrid(WebServerFixture.CompleteMapForThisJob(0.05, 0.1)));
            Assert.Equal(0, AppState.DepthAdjustment);
            Assert.Equal(original, OnTheMachine());

            Assert.Null(AppState.SetDepthAdjustment(TestDepth));
            Assert.Null(AppState.DiscardProbeData());
            Assert.Equal(0, AppState.DepthAdjustment);
            Assert.Equal(original, OnTheMachine());
        }

        /// <summary>
        /// The depth is limited to the configured maximum either way, so a mistyped value or a
        /// held key cannot cut through the board. The machine's lines follow the clamped value.
        /// </summary>
        [Fact]
        public void TheDepth_IsClampedToTheLimit_BothWays()
        {
            var loaded = GivenTheBoardIsLoaded();

            Assert.Null(AppState.SetDepthAdjustment(-WayPastTheLimit));
            Assert.Equal(-CliConstants.DepthAdjustmentMax, AppState.DepthAdjustment, Tolerance);
            AssertEveryCutBelowZeroIsDeeperBy(loaded, AppState.MachineFile!, -CliConstants.DepthAdjustmentMax);

            Assert.Null(AppState.AdjustDepthDeeper());
            Assert.Equal(-CliConstants.DepthAdjustmentMax, AppState.DepthAdjustment, Tolerance);

            Assert.Null(AppState.SetDepthAdjustment(WayPastTheLimit));
            Assert.Equal(CliConstants.DepthAdjustmentMax, AppState.DepthAdjustment, Tolerance);
            AssertEveryCutBelowZeroIsDeeperBy(loaded, AppState.MachineFile!, CliConstants.DepthAdjustmentMax);
            Assert.Equal(GCodeOf(AppState.MachineFile!), OnTheMachine());
        }

        /// <summary>
        /// A file with an XZ or YZ arc cannot take a depth, because the arc's center carries Z.
        /// The refusal names that, and neither the depth nor the machine's lines move.
        /// </summary>
        [Fact]
        public void AFileWithArcsOutsideTheXYPlane_RefusesTheDepth_AndChangesNothing()
        {
            GivenTheBoardIsLoaded(BoardWithAnXZArc);
            string[] before = OnTheMachine();

            Assert.Equal(Constants.ErrorArcsOutsideXYPlane, AppState.SetDepthAdjustment(TestDepth));
            Assert.Equal(Constants.ErrorArcsOutsideXYPlane, AppState.AdjustDepthDeeper());

            Assert.Equal(0, AppState.DepthAdjustment);
            Assert.Equal(before, OnTheMachine());
        }

        /// <summary>
        /// Runs <paramref name="body"/> with the board loaded, a map applied, the depth set and
        /// a mill run holding at the enclosure prompt.
        /// </summary>
        private async Task WhileAMillRunHoldsAtTheDoor(double depth, Func<Task> body)
        {
            await _web.LoadWithAMapAppliedAsync(_boards.Write(Board));
            Assert.Null(AppState.SetDepthAdjustment(depth));

            await _web.WhileAMillRunHoldsAtTheDoorAsync(body);
        }

        /// <summary>
        /// A run streams from the machine's lines by line number, so a depth change during one
        /// would swap the file under it. The change is refused with the shared message and the
        /// depth and the lines stay as the run has them.
        /// </summary>
        [Fact]
        public async Task DuringARun_TheDepthCannotChange()
        {
            await WhileAMillRunHoldsAtTheDoor(TestDepth, () =>
            {
                string[] during = OnTheMachine();

                Assert.Equal(CliConstants.ErrorFileChangeDuringRun, AppState.SetDepthAdjustment(OtherDepth));
                Assert.Equal(CliConstants.ErrorFileChangeDuringRun, AppState.AdjustDepthDeeper());
                Assert.Equal(CliConstants.ErrorFileChangeDuringRun, AppState.AdjustDepthShallower());

                Assert.Equal(TestDepth, AppState.DepthAdjustment, Tolerance);
                Assert.Equal(during, OnTheMachine());
                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// The web endpoint answers a depth change with the new depth, reads it back on GET,
        /// refuses a request it cannot read, or one that names no version, with 400 without
        /// changing anything, and refuses a
        /// change during a run with 409 and the same text the terminal shows.
        /// </summary>
        [Fact]
        public async Task TheDepthEndpoint_AnswersChanges_AndRefusesBadRequests()
        {
            GivenTheBoardIsLoaded();

            var (code, body) = await PostDepth(WebConstants.DepthActionDecrease);

            Assert.Equal(HttpStatusCode.OK, code);
            Assert.True(WebServerFixture.Flag(body, "success"));
            Assert.Equal(-CliConstants.DepthAdjustmentIncrement, body.GetProperty(JsonFieldDepth).GetDouble(), Tolerance);
            Assert.Equal(-CliConstants.DepthAdjustmentIncrement, AppState.DepthAdjustment, Tolerance);
            Assert.Equal(GCodeOf(AppState.MachineFile!), OnTheMachine());
            Assert.Equal(-CliConstants.DepthAdjustmentIncrement,
                (await GetJson(WebConstants.ApiMillDepth)).GetProperty(JsonFieldDepth).GetDouble(), Tolerance);

            var (badCode, badBody) = await Post(WebConstants.ApiMillDepth, new { action = ActionUnknown });
            var (noVersionCode, _) = await Post(WebConstants.ApiMillDepth, new { action = WebConstants.DepthActionDecrease });

            Assert.Equal(HttpStatusCode.BadRequest, badCode);
            Assert.Equal(HttpStatusCode.BadRequest, noVersionCode);
            Assert.False(WebServerFixture.Flag(badBody, "success"));
            Assert.Equal(-CliConstants.DepthAdjustmentIncrement, AppState.DepthAdjustment, Tolerance);
        }

        /// <inheritdoc cref="TheDepthEndpoint_AnswersChanges_AndRefusesBadRequests"/>
        [Fact]
        public async Task TheDepthEndpoint_RefusesAChangeDuringARun_With409()
        {
            await WhileAMillRunHoldsAtTheDoor(TestDepth, async () =>
            {
                var (code, body) = await PostDepth(WebConstants.DepthActionDecrease);

                Assert.Equal(HttpStatusCode.Conflict, code);
                Assert.Equal(CliConstants.ErrorFileChangeDuringRun,
                    body.GetProperty(WebConstants.JsonFieldError).GetString());
                Assert.Equal(TestDepth, AppState.DepthAdjustment, Tolerance);
                Assert.Equal(TestDepth,
                    (await GetJson(WebConstants.ApiMillDepth)).GetProperty(JsonFieldDepth).GetDouble(), Tolerance);
            });
        }

        /// <summary>
        /// The depth lives in the G-code, so a run with one set must not also move work zero:
        /// no G10 L2 is sent and G54 is where it was. A second correction through the work
        /// offset would cut twice as deep, and would stay in GRBL's G54 after the run.
        /// </summary>
        [Fact]
        public async Task AMillRunWithADepthSet_WritesNoWorkOffset()
        {
            GivenTheBoardIsLoaded(ShortCut);
            Assert.Null(AppState.SetDepthAdjustment(TestDepth));
            var cutLine = AppState.MachineFile!.GetGCode().Single(l => l.StartsWith("G1 Z", StringComparison.Ordinal));
            Assert.Equal("G1 Z-0.2", cutLine);

            using var machine = new FakeMachine
            {
                RapidSpeed = 10000.0,
                FeedSpeed = 10000.0,
                HomingDurationMs = 50
            };
            machine.LoadFile(AppState.MachineFile!.GetGCode().ToArray());
            var offsetBefore = machine.G54Offset;
            machine.IsHomed = true;
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions ()
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(RunWaitTimeoutMs));
            await controller.StartAsync(cts.Token);

            Assert.Equal(ControllerState.Completed, controller.State);
            Assert.Contains(cutLine, machine.File);
            Assert.DoesNotContain(machine.File,
                l => l.StartsWith(GrblProtocol.CmdSetWorkOffset, StringComparison.Ordinal));
            Assert.DoesNotContain(machine.SentCommands,
                c => c.StartsWith(GrblProtocol.CmdSetWorkOffset, StringComparison.Ordinal));
            Assert.Equal(offsetBefore, machine.G54Offset);
        }

        /// <summary>
        /// Applying map A, adopting map B and applying B must leave exactly what map B alone
        /// makes of the loaded file. A build that started from the machine's lines instead of
        /// the loaded file would carry A's corrections into B's.
        /// </summary>
        [Fact]
        public void ApplyingAdoptedMapB_AfterMapA_DoesNotStackTheCorrections()
        {
            var loaded = GivenTheBoardIsLoaded();
            var mapB = WebServerFixture.CompleteMapForThisJob(0.02, 0.3);

            Assert.Null(AppState.AdoptProbeGrid(WebServerFixture.CompleteMapForThisJob(0.05, 0.1)));
            Assert.Null(AppState.ApplyProbeData());
            Assert.Null(AppState.AdoptProbeGrid(mapB));
            Assert.False(AppState.AreProbePointsApplied);
            Assert.Null(AppState.ApplyProbeData());

            Assert.True(AppState.AreProbePointsApplied);
            Assert.Equal(GCodeOf(loaded.ApplyProbeGrid(mapB)), OnTheMachine());
            Assert.Equal(GCodeOf(AppState.MachineFile!), OnTheMachine());
        }

        /// <summary>
        /// A file with an XZ or YZ arc cannot take a height map: applying one is refused with
        /// the arc message, the map stays unapplied and the machine's lines are not touched.
        /// </summary>
        [Fact]
        public void ApplyingAMap_ToAFileWithArcsOutsideTheXYPlane_IsRefused_AndChangesNothing()
        {
            GivenTheBoardIsLoaded(BoardWithAnXZArc);
            Assert.Null(AppState.AdoptProbeGrid(WebServerFixture.CompleteMapForThisJob(0.05, 0.1)));
            var before = AppState.MachineFile;
            string[] linesBefore = OnTheMachine();

            Assert.Equal(Constants.ErrorArcsOutsideXYPlane, AppState.ApplyProbeData());

            Assert.False(AppState.AreProbePointsApplied);
            Assert.Same(before, AppState.MachineFile);
            Assert.Equal(linesBefore, OnTheMachine());
        }

        /// <summary>
        /// Depth changes, map applications and map adoptions arrive on several threads, and
        /// the machine's G-code is built from the file, the map and the depth together. After
        /// each round of them racing, the machine must hold exactly what the depth and the
        /// applied state now say; without FileLock one change overwrites another's inputs
        /// and the lines describe a depth or a map that is no longer current.
        /// </summary>
        [Fact]
        public void ConcurrentDepthMapAndAdoptChanges_LeaveTheMachineConsistent()
        {
            // Long enough that building and loading it takes real time, so a change that
            // interleaves with another has a window to show in.
            GivenTheBoardIsLoaded(BigBoard());
            var map = WebServerFixture.CompleteMapForThisJob(0.05, 0.1);

            for (int round = 0; round < ConcurrencyRounds; round++)
            {
                Assert.Null(AppState.AdoptProbeGrid(map));
                using var barrier = new Barrier(ConcurrentWorkers);
                var workers = Enumerable.Range(0, ConcurrentWorkers).Select(worker => Task.Factory.StartNew(() =>
                {
                    var random = new Random(RandomSeedBase + round * ConcurrentWorkers + worker);
                    barrier.SignalAndWait();
                    for (int i = 0; i < OperationsPerWorker; i++)
                    {
                        switch (random.Next(OperationKinds))
                        {
                            case 0: AppState.AdjustDepthDeeper(); break;
                            case 1: AppState.AdjustDepthShallower(); break;
                            case 2: AppState.SetDepthAdjustment(0); break;
                            case 3: AppState.ApplyProbeData(); break;
                            default: AppState.AdoptProbeGrid(map); break;
                        }
                    }
                }, TaskCreationOptions.LongRunning)).ToArray();
                Task.WaitAll(workers);

                var expected = AppState.CurrentFile!.OffsetCutDepth(AppState.DepthAdjustment);
                if (AppState.AreProbePointsApplied)
                {
                    expected = expected.ApplyProbeGrid(AppState.ProbePoints!);
                }

                Assert.Equal(GCodeOf(expected), OnTheMachine());
                Assert.Equal(GCodeOf(AppState.MachineFile!), OnTheMachine());
            }
        }

        /// <summary>
        /// A depth that is not a number is refused with the invalid-depth text, and with no
        /// file loaded the refusal says so; neither changes the depth or the machine's lines.
        /// </summary>
        [Fact]
        public void ADepthThatIsNotANumber_OrNoFile_IsRefused_AndChangesNothing()
        {
            GivenTheBoardIsLoaded();
            Assert.Null(AppState.SetDepthAdjustment(TestDepth));
            string[] before = OnTheMachine();

            Assert.Equal(CliConstants.ErrorInvalidDepth, AppState.SetDepthAdjustment(double.NaN));

            Assert.Equal(TestDepth, AppState.DepthAdjustment, Tolerance);
            Assert.Equal(before, OnTheMachine());

            AppState.UnloadFileForTest();

            Assert.Equal(CliConstants.ErrorNoFileLoaded, AppState.SetDepthAdjustment(TestDepth));
            Assert.Equal(CliConstants.ErrorInvalidDepth, AppState.SetDepthAdjustment(double.NaN));
            Assert.Null(AppState.MachineFile);
            Assert.Equal(0, AppState.DepthAdjustment);
        }

        /// <summary>
        /// Two presses at once must each move the depth by one step. If each press read the
        /// depth outside the lock that changes it, both would read the same depth and one step
        /// would be lost.
        /// </summary>
        [Fact]
        public void ConcurrentDepthSteps_EachMoveTheDepthByOneIncrement()
        {
            GivenTheBoardIsLoaded(BigBoard());
            int steps = Math.Min(ConcurrentWorkers,
                (int)Math.Floor(CliConstants.DepthAdjustmentMax / CliConstants.DepthAdjustmentIncrement));
            using var barrier = new Barrier(steps);
            var workers = Enumerable.Range(0, steps).Select(_ => Task.Factory.StartNew(() =>
            {
                barrier.SignalAndWait();
                Assert.Null(AppState.AdjustDepthDeeper());
            }, TaskCreationOptions.LongRunning)).ToArray();
            Task.WaitAll(workers);

            Assert.Equal(-steps * CliConstants.DepthAdjustmentIncrement, AppState.DepthAdjustment, Tolerance);
            Assert.Equal(GCodeOf(AppState.CurrentFile!.OffsetCutDepth(AppState.DepthAdjustment)), OnTheMachine());
        }

        /// <summary>
        /// The reply to a depth change names the version that change made, so the pre-mill
        /// window confirms the job with its new depth rather than the one it opened on.
        /// </summary>
        [Fact]
        public async Task TheDepthReply_NamesTheVersionTheChangeMade()
        {
            GivenTheBoardIsLoaded();
            long before = AppState.MachineFileVersion;

            var (code, body) = await PostDepth(WebConstants.DepthActionDecrease);

            Assert.Equal(HttpStatusCode.OK, code);
            long named = body.GetProperty(WebServerFixture.JsonFieldVersion).GetInt64();
            Assert.NotEqual(before, named);
            Assert.Equal(AppState.MachineFileVersion, named);
        }

        /// <summary>
        /// A depth change names the version the window checked. If another client changed the
        /// job since, the change is refused with the shared text and the depth stays, so the
        /// window cannot fold that change into a version it then confirms.
        /// </summary>
        [Fact]
        public async Task ADepthChangeToAJobThatChangedSinceTheCheck_IsRefused()
        {
            var loaded = GivenTheBoardIsLoaded();
            long checkedVersion = AppState.MachineFileVersion;
            Assert.Null(AppState.LoadGCodeIntoMachine(loaded).Refused);

            var (code, body) = await Post(WebConstants.ApiMillDepth,
                new { action = WebConstants.DepthActionDecrease, version = checkedVersion });

            Assert.Equal(HttpStatusCode.Conflict, code);
            Assert.Equal(WebConstants.ErrorJobChangedSinceChecked,
                body.GetProperty(WebConstants.JsonFieldError).GetString());
            Assert.Equal(0, AppState.DepthAdjustment);
        }

        /// <summary>
        /// The depth endpoint takes only an action. A depth value in the body is not an action,
        /// so it is a 400 and changes nothing; "reset" returns the original lines.
        /// </summary>
        [Fact]
        public async Task TheDepthEndpoint_TakesOnlyAnAction()
        {
            var loaded = GivenTheBoardIsLoaded();

            var (badCode, _) = await Post(WebConstants.ApiMillDepth, new Dictionary<string, object> { [JsonFieldDepth] = TestDepth });
            Assert.Equal(HttpStatusCode.BadRequest, badCode);
            Assert.Equal(0, AppState.DepthAdjustment);

            var (codeIncrease, _) = await PostDepth(WebConstants.DepthActionIncrease);
            Assert.Equal(HttpStatusCode.OK, codeIncrease);
            Assert.Equal(CliConstants.DepthAdjustmentIncrement, AppState.DepthAdjustment, Tolerance);

            var (codeReset, _) = await PostDepth(WebConstants.DepthActionReset);
            Assert.Equal(HttpStatusCode.OK, codeReset);
            Assert.Equal(0, AppState.DepthAdjustment);
            Assert.Equal(GCodeOf(loaded), OnTheMachine());
        }
    }
}
