#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using coppercli.Core.Communication;
using coppercli.Core.Controllers;
using coppercli.Core.GCode;
using coppercli.Core.Util;
using coppercli.Helpers;
using coppercli.Tests.Fakes;
using coppercli.WebServer;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// A stop while cutting on a homed machine is remembered, so the next job offers to home
    /// first. These tests check the rules for StoppedWhileCutting on Machine, FakeMachine and
    /// MockMachine, and when a run homes.
    /// </summary>
    public class HomeFirstMachineTests
    {
        private const int HomingWaitMs = 5000;
        private const int WaitMs = 15_000;
        private const double FastMoveSpeedMmPerSec = 10000.0;
        private const int FastHomingDurationMs = 50;
        private static readonly string[] Job = { "G21", "G90", "G1 X100 Y100 F60" };
        private static readonly string[] JobWithAToolChange = { "G21", "G90", "M6 T2", "G1 X100 Y100 F60" };
        private static readonly string[] AShortJob = { "G21", "G90", "G1 X1 Y1 F100" };
        private static readonly string[] JobWithAProgramPause = { "G21", "G90", "G1 X1 Y1 F100", "M0", "G1 X2 Y2 F100" };

        public static IEnumerable<object[]> Kinds() => new[]
        {
            new object[] { MachineKind.Real },
            new object[] { MachineKind.Fake },
            new object[] { MachineKind.Mock }
        };

        public enum MachineKind { Real, Fake, Mock }

        private sealed class Rig : IDisposable
        {
            private readonly FakeGrbl? _grbl;
            public IMachine Machine { get; }

            public Rig(MachineKind kind)
            {
                switch (kind)
                {
                    case MachineKind.Real:
                        _grbl = new FakeGrbl();
                        Machine = _grbl.ConnectedMachine();
                        break;
                    case MachineKind.Fake:
                        Machine = new FakeMachine { HomingDurationMs = FastHomingDurationMs };
                        break;
                    default:
                        Machine = new MockMachine();
                        break;
                }
            }

            public void Dispose()
            {
                if (Machine is Machine real)
                {
                    real.Disconnect();
                }

                (Machine as IDisposable)?.Dispose();
                _grbl?.Dispose();
            }
        }

        /// <summary>
        /// A machine that is not homed has nothing to re-home, so noting a stop records
        /// nothing; a homed one records it.
        /// </summary>
        [Theory]
        [MemberData(nameof(Kinds))]
        public void NotingAStop_IsRecordedOnlyOnAHomedMachine(MachineKind kind)
        {
            using var rig = new Rig(kind);
            rig.Machine.IsHomed = false;

            rig.Machine.NoteStoppedWhileCutting();
            Assert.False(rig.Machine.StoppedWhileCutting, "a stop was recorded on a machine that is not homed");

            rig.Machine.IsHomed = true;
            rig.Machine.NoteStoppedWhileCutting();
            Assert.True(rig.Machine.StoppedWhileCutting);
        }

        /// <summary>
        /// Any assignment to IsHomed, true or false, clears StoppedWhileCutting: homing
        /// re-established the position or the machine lost it, and either way the next job
        /// does not need to ask.
        /// </summary>
        [Theory]
        [MemberData(nameof(Kinds))]
        public void AnyAssignmentToIsHomed_ClearsTheStop(MachineKind kind)
        {
            using var rig = new Rig(kind);
            foreach (bool assigned in new[] { true, false })
            {
                rig.Machine.IsHomed = true;
                rig.Machine.NoteStoppedWhileCutting();
                Assert.True(rig.Machine.StoppedWhileCutting);

                rig.Machine.IsHomed = assigned;

                Assert.False(rig.Machine.StoppedWhileCutting, $"assigning IsHomed = {assigned} kept the stop");
            }
        }

        /// <summary>
        /// A successful home clears StoppedWhileCutting through the IsHomed it sets.
        /// </summary>
        [Theory]
        [MemberData(nameof(Kinds))]
        public async Task AHomingThatSucceeds_ClearsTheStop(MachineKind kind)
        {
            using var rig = new Rig(kind);
            rig.Machine.IsHomed = true;
            rig.Machine.NoteStoppedWhileCutting();
            Assert.True(rig.Machine.StoppedWhileCutting);

            var outcome = await MachineWait.HomeAsync(rig.Machine, HomingWaitMs);

            Assert.True(outcome.Success, outcome.Reason);
            Assert.True(rig.Machine.IsHomed);
            Assert.False(rig.Machine.StoppedWhileCutting);
        }

        private static FakeMachine JobMachine()
        {
            var machine = new FakeMachine
            {
                RapidSpeed = FastMoveSpeedMmPerSec,
                HomingDurationMs = FastHomingDurationMs
            };
            machine.LoadFile(Job);
            return machine;
        }

        private static async Task AwaitOutcomeAsync(Task run)
        {
            try
            {
                await run;
            }
            catch (InvalidOperationException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>A run the test drives through its phases.</summary>
        private static (MillingController Controller, Task Run) StartRun(
            FakeMachine machine, bool homed = true, bool homeFirst = false)
        {
            machine.IsHomed = homed;
            var controller = new MillingController(machine)
            {
                Options = MillingOptions.Create(filePath: null, homeFirst: homeFirst, enclosureConfirmed: true)
            };
            return (controller, controller.StartAsync());
        }

        private static async Task WaitForPhaseAsync(MillingController controller, MillingPhase phase) =>
            await AsyncWait.WaitUntilAsync(() => controller.Phase == phase, $"the {phase} phase", WaitMs);

        private static bool Homed(FakeMachine machine) => machine.SentCommands.Contains(GrblProtocol.CmdHome);

        /// <summary>
        /// A stop while the job is streaming may follow a stall or a crash, so the machine
        /// remembers it and the next job offers to home first.
        /// </summary>
        [Fact]
        public async Task AStopWhileMilling_IsRemembered()
        {
            using var machine = JobMachine();
            var (controller, run) = StartRun(machine);

            await WaitForPhaseAsync(controller, MillingPhase.Milling);
            await controller.StopAsync();
            await AwaitOutcomeAsync(run);

            Assert.True(machine.StoppedWhileCutting, "a stop in the middle of cutting was not remembered");
        }

        /// <summary>
        /// Pausing is the likeliest first answer to a stall, and a pause keeps the Milling
        /// phase, so a stop after it is a stop while cutting.
        /// </summary>
        [Fact]
        public async Task AStopWhilePausedMidCut_IsRemembered()
        {
            using var machine = JobMachine();
            var (controller, run) = StartRun(machine);

            await AsyncWait.WaitUntilAsync(
                () => controller.State == ControllerState.Running && controller.Phase == MillingPhase.Milling,
                "cutting", WaitMs);
            controller.Pause();
            await AsyncWait.WaitUntilAsync(() => controller.IsPaused, "the pause", WaitMs);
            await controller.StopAsync();
            await AwaitOutcomeAsync(run);

            Assert.True(machine.StoppedWhileCutting, "a stop after pausing mid-cut was not remembered");
        }

        /// <summary>
        /// A stop before any cutting has skipped no steps, so it must not make the next job
        /// offer to home: the settle has moved nothing.
        /// </summary>
        [Fact]
        public async Task AStopDuringTheSettle_IsNotRemembered()
        {
            using var machine = JobMachine();
            var (controller, run) = StartRun(machine);

            await WaitForPhaseAsync(controller, MillingPhase.Settling);
            await controller.StopAsync();
            await AwaitOutcomeAsync(run);

            Assert.NotEqual(MillingPhase.Milling, controller.Phase);
            Assert.False(machine.StoppedWhileCutting, "a stop before cutting began was remembered as a stop while cutting");
        }

        /// <summary>
        /// A tool change waits with the spindle stopped, so a stop there follows no crash.
        /// </summary>
        [Fact]
        public async Task AStopDuringAToolChange_IsNotRemembered()
        {
            using var machine = JobMachine();
            machine.LoadFile(JobWithAToolChange);
            var (controller, run) = StartRun(machine);

            await WaitForPhaseAsync(controller, MillingPhase.ToolChange);
            await controller.StopAsync();
            await AwaitOutcomeAsync(run);

            Assert.False(machine.StoppedWhileCutting, "a stop at a tool change was remembered as a stop while cutting");
        }

        /// <summary>
        /// At a program pause (M0) the job waits for the operator with nothing cutting, so a
        /// stop there follows no crash.
        /// </summary>
        [Fact]
        public async Task AStopAtAProgramPause_IsNotRemembered()
        {
            using var machine = JobMachine();
            machine.FeedSpeed = FastMoveSpeedMmPerSec;
            machine.LoadFile(JobWithAProgramPause);
            machine.IsHomed = true;
            var controller = new MillingController(machine)
            {
                Options = MillingOptions.Create(filePath: null, homeFirst: false, enclosureConfirmed: true)
            };
            controller.UserInputRequired += _ => { };

            var run = controller.StartAsync();
            await AsyncWait.WaitUntilAsync(
                () => controller.State == ControllerState.WaitingForUserInput, "the program pause", WaitMs);
            await controller.StopAsync();
            await AwaitOutcomeAsync(run);

            Assert.False(machine.StoppedWhileCutting, "a stop at a program pause was remembered as a stop while cutting");
        }

        /// <summary>
        /// Completing keeps the Milling phase, but every line has been cut by then, so a stop
        /// during the final retract and home is not a stop while cutting.
        /// </summary>
        [Fact]
        public async Task AStopWhileCompleting_IsNotRemembered()
        {
            using var machine = JobMachine();
            machine.FeedSpeed = FastMoveSpeedMmPerSec;
            machine.LoadFile(AShortJob);
            machine.IsHomed = true;
            var controller = new MillingController(machine)
            {
                Options = MillingOptions.Create(filePath: null, homeFirst: false, enclosureConfirmed: true)
            };

            // Started from the transition itself, so the stop lands inside Completing; not
            // awaited there, because the run is what raises the event.
            Task? stop = null;
            controller.StateChanged += state =>
            {
                if (state == ControllerState.Completing)
                {
                    stop ??= controller.StopAsync();
                }
            };

            await AwaitOutcomeAsync(controller.StartAsync());

            Assert.NotNull(stop);
            await stop!;
            Assert.Equal(ControllerState.Cancelled, controller.State);
            Assert.False(machine.StoppedWhileCutting, "a stop after the last line was cut was remembered as a stop while cutting");
        }

        /// <summary>
        /// An alarm while cutting is worse than a stop: it clears IsHomed, so the next job
        /// homes without asking instead of offering to.
        /// </summary>
        [Fact]
        public async Task AnAlarmWhileCutting_LeavesTheMachineToHome_NotToAsk()
        {
            using var machine = JobMachine();
            var (controller, run) = StartRun(machine);

            await WaitForPhaseAsync(controller, MillingPhase.Milling);
            machine.SimulateAlarm();
            await AwaitOutcomeAsync(run);

            Assert.False(machine.IsHomed);
            Assert.False(machine.StoppedWhileCutting);
        }

        /// <summary>
        /// A homed machine homes only when the operator asks to home first; an unhomed one always
        /// homes.
        /// </summary>
        [Theory]
        [InlineData(true, false, false)]
        [InlineData(true, true, true)]
        [InlineData(false, false, true)]
        [InlineData(false, true, true)]
        public async Task ARun_Homes_WhenNotHomedOrAskedToHomeFirst(bool homed, bool homeFirst, bool expected)
        {
            using var machine = JobMachine();
            var (controller, run) = StartRun(machine, homed, homeFirst);

            await WaitForPhaseAsync(controller, MillingPhase.Milling);
            await controller.StopAsync();
            await AwaitOutcomeAsync(run);

            Assert.Equal(expected, Homed(machine));
        }

        /// <summary>
        /// The run decides whether to skip homing after the settle, so a restart during the settle
        /// that clears IsHomed makes it home rather than cut from a lost position.
        /// </summary>
        [Fact]
        public async Task AMachineThatLosesItsPositionDuringTheSettle_Homes()
        {
            using var machine = JobMachine();
            var (controller, run) = StartRun(machine);

            await WaitForPhaseAsync(controller, MillingPhase.Settling);
            machine.IsHomed = false;
            await WaitForPhaseAsync(controller, MillingPhase.Milling);
            await controller.StopAsync();
            await AwaitOutcomeAsync(run);

            Assert.True(Homed(machine), "the run cut without homing after the machine lost its position");
        }
    }

    /// <summary>
    /// The mill start check and the web endpoints that offer and take the home-first answer.
    /// </summary>
    [Collection(WebServerCollection.Name)]
    public class HomeFirstWebTests : IDisposable
    {
        private static readonly string[] Board =
        {
            "G21", "G90", "G0 X0 Y0 Z1", "G1 Z-0.1 F100", "G1 X10 Y0", "G0 Z1"
        };

        private const string JsonFieldOffer = "offerHomeFirst";
        private const int UnknownWarning = 999;

        private readonly WebServerFixture _web;
        private readonly List<string> _files = new();

        public HomeFirstWebTests(WebServerFixture web)
        {
            _web = web;
            _web.RestoreFixtureState();
        }

        public void Dispose()
        {
            AppState.Machine.IsHomed = false;
            AppState.DiscardProbeData();
            Persistence.ClearProbeAutoSave();
            foreach (string file in _files)
            {
                File.Delete(file);
            }
        }

        private HttpClient Client => _web.Client;

        private void GivenTheBoardIsLoaded()
        {
            string path = Path.Combine(Path.GetTempPath(), "coppercli-homefirst-" + Guid.NewGuid().ToString("N") + ".ngc");
            File.WriteAllLines(path, Board);
            _files.Add(path);
            AppState.MarkWorkZeroSet();
            Assert.Null(AppState.LoadGCodeIntoMachine(GCodeFile.Load(path)).Refused);
        }

        private static void GivenAStopWhileCutting()
        {
            AppState.Machine.IsHomed = true;
            AppState.Machine.NoteStoppedWhileCutting();
            Assert.True(AppState.Machine.StoppedWhileCutting);
        }

        private async Task<JsonElement> CanStart()
        {
            var response = await Client.GetAsync(WebConstants.ApiMillCanStart);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        }

        /// <summary>
        /// The check for a job adds the home-first question exactly when the machine is homed
        /// and was stopped while cutting; an unhomed machine gets the homing warning instead.
        /// </summary>
        [Fact]
        public void CheckMillCanStart_AddsTheStopWarning_OnlyWhenHomedAndStoppedWhileCutting()
        {
            GivenTheBoardIsLoaded();

            AppState.Machine.IsHomed = true;
            Assert.DoesNotContain(MillWarning.StoppedWhileCutting, MenuHelpers.CheckMillCanStart().Warnings);

            GivenAStopWhileCutting();
            var stopped = MenuHelpers.CheckMillCanStart().Warnings;
            Assert.Contains(MillWarning.StoppedWhileCutting, stopped);
            Assert.DoesNotContain(MillWarning.NotHomed, stopped);

            AppState.Machine.IsHomed = false;
            var notHomed = MenuHelpers.CheckMillCanStart().Warnings;
            Assert.DoesNotContain(MillWarning.StoppedWhileCutting, notHomed);
            Assert.Contains(MillWarning.NotHomed, notHomed);
        }

        /// <summary>
        /// Every warning has its own text, so a new warning cannot show the operator a blank; a
        /// value the table does not know throws.
        /// </summary>
        [Fact]
        public void GetMillWarningText_MapsEveryWarning_AndThrowsOnAnUnknownOne()
        {
            var texts = Enum.GetValues<MillWarning>().Select(MenuHelpers.GetMillWarningText).ToArray();

            Assert.All(texts, text => Assert.False(string.IsNullOrWhiteSpace(text)));
            Assert.Equal(texts.Length, texts.Distinct().Count());
            Assert.Equal(CliConstants.StoppedWhileCuttingWarning,
                MenuHelpers.GetMillWarningText(MillWarning.StoppedWhileCutting));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => MenuHelpers.GetMillWarningText((MillWarning)UnknownWarning));
        }

        /// <summary>
        /// The browser is offered the home-first question after a stop while cutting and at no
        /// other time, and homing again ends the offer.
        /// </summary>
        [Fact]
        public async Task CanStart_OffersHomeFirst_OnlyAfterAStopWhileCutting()
        {
            GivenTheBoardIsLoaded();
            AppState.Machine.IsHomed = true;
            Assert.False((await CanStart()).GetProperty(JsonFieldOffer).GetBoolean());

            GivenAStopWhileCutting();
            var offered = await CanStart();
            Assert.True(offered.GetProperty(JsonFieldOffer).GetBoolean());
            Assert.Contains(offered.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()),
                w => w == CliConstants.StoppedWhileCuttingWarning);

            AppState.Machine.IsHomed = true;
            Assert.False((await CanStart()).GetProperty(JsonFieldOffer).GetBoolean());
        }

        /// <summary>
        /// Starts a run held at the closed-but-holding door with <paramref name="homeFirst"/>,
        /// and reads whether it was told to home first.
        /// </summary>
        private async Task<bool> RunHomesFirst(bool stoppedWhileCutting, bool? homeFirst)
        {
            GivenTheBoardIsLoaded();
            if (stoppedWhileCutting)
            {
                GivenAStopWhileCutting();
            }
            else
            {
                AppState.Machine.IsHomed = true;
            }

            bool told = false;
            await _web.WhileAMillRunHoldsAtTheDoorAsync(() =>
            {
                told = AppState.Milling.Options.HomeFirst;
                return Task.CompletedTask;
            }, homeFirst);
            return told;
        }

        /// <summary>
        /// Declining to home first after a stop while cutting starts a run that does not home.
        /// </summary>
        [Fact]
        public async Task Start_WithHomeFirstFalse_DoesNotHome()
        {
            Assert.False(await RunHomesFirst(stoppedWhileCutting: true, homeFirst: false));
        }

        /// <summary>
        /// After a stop while cutting, a start with no answer takes HomeFirstByDefault, the
        /// answer the terminal's question starts at, and a start with homeFirst true homes.
        /// </summary>
        [Fact]
        public async Task Start_WithNoAnswer_TakesTheDefault_AndHomeFirstTrueHomes_AfterAStopWhileCutting()
        {
            Assert.Equal(CliConstants.HomeFirstByDefault, await RunHomesFirst(stoppedWhileCutting: true, homeFirst: null));
            Assert.True(await RunHomesFirst(stoppedWhileCutting: true, homeFirst: true));
        }

        /// <summary>
        /// With no stop to ask about, a homed machine runs without homing.
        /// </summary>
        [Fact]
        public async Task Start_OnAHomedMachineNotStoppedWhileCutting_DoesNotHome()
        {
            Assert.False(await RunHomesFirst(stoppedWhileCutting: false, homeFirst: null));
        }
    }
}
