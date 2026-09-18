using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using coppercli.Core.Communication;
using coppercli.Core.Controllers;
using coppercli.Core.Util;
using coppercli.Tests.Fakes;
using Xunit;

namespace coppercli.Tests
{
    // Each test here covers one guard on machine movement: the
    // confirmed retract, the stop before a disconnect, homing, readiness, the work-offset
    // query and the depth adjustment. Remove a guard and the test covering it fails.
    public class SafetyCheckTests
    {
        /// <summary>Long enough that reaching it means the run never started at all.</summary>
        private const int HangDetectMs = 10000;

        /// <summary>
        /// If the failed retract is returned rather than thrown, the run unwinds as a normal
        /// finish and reports Completed.
        /// </summary>
        [Fact]
        public async Task AMillWhoseFinalRetractIsNotConfirmed_NeverReportsItFinished()
        {
            using var machine = new FakeMachine();
            machine.LoadFile("G21", "G90", "G1 X1 Y1 F100");

            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { RequireHoming = false }
            };

            // The job's own moves all land. At Completing the tool is put back at Z0 and
            // every further move alarms, so only the final lift is left unconfirmed.
            controller.StateChanged += state =>
            {
                if (state == ControllerState.Completing)
                {
                    machine.SetMachinePosition(0, 0, 0);
                    machine.AlarmOnMove = true;
                }
            };

            var errors = new List<ControllerError>();
            controller.ErrorOccurred += errors.Add;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await controller.StartAsync(cts.Token);

            Assert.NotEqual(ControllerState.Completed, controller.State);
            Assert.Contains(
                errors, e => e.Message == ControllerConstants.ErrorSafetyRetractFailed);
        }

        /// <summary>
        /// A stop at the door never confirms the lift: the soft reset clears the hold, so the
        /// tool position is unknown and no move can be queued to check it. Milling reports it
        /// through LiftAfterStopAsync, the same path probing takes.
        /// </summary>
        [Fact]
        public async Task AMillStoppedAtTheDoor_ReportsAnUnconfirmedLift()
        {
            using var machine = new FakeMachine();
            machine.LoadFile("G21", "G90", "G1 X1 Y1 F100");
            machine.SimulateDoorOpen();

            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { RequireHoming = false }
            };

            var errors = new List<ControllerError>();
            controller.ErrorOccurred += errors.Add;

            using var cts = new CancellationTokenSource();
            var run = controller.StartAsync(cts.Token);

            long deadline = Environment.TickCount64 + HangDetectMs;
            while (!controller.IsRunInProgress && Environment.TickCount64 < deadline)
            {
                await Task.Delay(Constants.StatusPollIntervalMs);
            }

            cts.Cancel();
            try { await run; } catch (OperationCanceledException) { }

            Assert.Contains(
                errors, e => e.Message == ControllerConstants.ErrorStopRetractFailed);
        }

        /// <summary>
        /// Closing the port does not stop GRBL, so a machine reporting anything but Idle is
        /// stopped first. Machine.Status holds the bare state word with any substate kept
        /// separately; "Jog" and "Home" are written out because the codebase has no
        /// constants for them.
        /// </summary>
        [Theory]
        [InlineData(GrblProtocol.StatusRun)]
        [InlineData(GrblProtocol.StatusHold)]
        [InlineData(GrblProtocol.StatusDoor)]
        [InlineData(GrblProtocol.StatusAlarm)]
        [InlineData("Jog")]
        [InlineData("Home")]
        public void NeedsStopBeforeDisconnect_IsTrueWhenTheMachineIsNotIdle(string status)
        {
            Assert.True(Machine.NeedsStopBeforeDisconnect(connected: true, status, bytesSent: 0));
        }

        [Fact]
        public void NeedsStopBeforeDisconnect_IsFalseWhenIdleAndNothingOutstanding()
        {
            Assert.False(
                Machine.NeedsStopBeforeDisconnect(connected: true, GrblProtocol.StatusIdle, bytesSent: 0));
        }

        /// <summary>
        /// A stop waits out the whole teardown - feed hold, reset, unlock, idle, then the
        /// retract - so its timeout has to cover the sum. A shorter timeout reports a stop
        /// that worked as one that may have left the machine moving.
        /// </summary>
        [Fact]
        public void ControllerCancelTimeout_CoversStopSequenceAndRetract()
        {
            int teardown = (Constants.CommandDelayMs * 2)
                + Constants.ResetWaitMs
                + Constants.IdleWaitTimeoutMs
                + Constants.CancelRetractTimeoutMs;

            Assert.True(Constants.ControllerCancelTimeoutMs >= teardown);
        }

        /// <summary>
        /// A line sent a moment earlier sits unparsed in GRBL's receive buffer while the
        /// status still reads Idle. That happens when a jog and a disconnect both land
        /// between two status polls.
        /// </summary>
        [Fact]
        public void NeedsStopBeforeDisconnect_IsTrueWhenIdleWithALineOutstanding()
        {
            Assert.True(
                Machine.NeedsStopBeforeDisconnect(connected: true, GrblProtocol.StatusIdle, bytesSent: 12));
        }

        /// <summary>
        /// Auto-detect opens every serial port in turn and disconnects the ones that do not
        /// respond. A port that never answered as GRBL must not be sent a reset.
        /// </summary>
        [Fact]
        public void NeedsStopBeforeDisconnect_IsFalseForAPortThatNeverAnsweredGrbl()
        {
            Assert.False(Machine.NeedsStopBeforeDisconnect(
                connected: true, GrblProtocol.StatusDisconnected, bytesSent: 0));
        }

        [Fact]
        public void NeedsStopBeforeDisconnect_IsFalseWhenNotConnected()
        {
            Assert.False(
                Machine.NeedsStopBeforeDisconnect(connected: false, GrblProtocol.StatusRun, bytesSent: 0));
        }

        /// <summary>
        /// The bytes that stop a GRBL machine, in order, with time between them for GRBL to
        /// act on each.
        /// </summary>
        [Fact]
        public void WriteStopSequence_SendsAFeedHoldThenASoftReset()
        {
            var wire = new MemoryStream();
            var elapsed = Stopwatch.StartNew();

            Machine.WriteStopSequence(wire);

            Assert.Equal(
                new[] { (byte)GrblProtocol.FeedHold, (byte)GrblProtocol.SoftReset },
                wire.ToArray());
            Assert.True(elapsed.ElapsedMilliseconds >= Constants.CommandDelayMs + Constants.ResetWaitMs);
        }

        [Fact]
        public async Task SafetyRetract_ReportsFailure_WhenZNeverArrives()
        {
            var machine = new MockMachine
            {
                Status = "Run",                                   // never settles
                MachinePosition = new Vector3(0, 0, 0),
                IgnoreMoves = true                                // and never gets there
            };

            bool retracted = await MachineWait.SafetyRetractZAsync(machine, -40.0, 300);

            Assert.False(retracted);
        }

        [Fact]
        public async Task SafetyRetract_ReportsSuccess_WhenZArrives()
        {
            var machine = new MockMachine
            {
                Status = "Idle",
                MachinePosition = new Vector3(0, 0, -40.0)        // already at target
            };

            bool retracted = await MachineWait.SafetyRetractZAsync(machine, -40.0, 300);

            Assert.True(retracted);
        }

        /// <summary>
        /// GRBL keeps answering and keeps reporting Idle: the $H was rejected (homing
        /// disabled, or an error reply). Accepting that as homed would leave every later
        /// G53 safety move referenced to an origin that was never established.
        /// </summary>
        [Fact]
        public async Task Home_IsRefused_WhenGrblKeepsAnsweringAndStaysIdle()
        {
            var machine = new MockMachine { Status = "Idle", StatusReportCount = 1 };

            // HomeAsync separates a refused $H from a cycle under way by whether
            // StatusReportCount keeps advancing; this pump keeps it advancing.
            using var reporting = new CancellationTokenSource();
            var pump = Task.Run(async () =>
            {
                while (!reporting.IsCancellationRequested)
                {
                    machine.StatusReportCount++;
                    await Task.Delay(20);
                }
            });

            var outcome = await MachineWait.HomeAsync(machine, 500);
            reporting.Cancel();
            await pump;

            Assert.False(outcome.Success);
            Assert.False(machine.IsHomed);
        }

        [Fact]
        public async Task Home_IsRefused_WhenTheCycleNeverCompletes()
        {
            // No pump here, so StatusReportCount never advances: GRBL went quiet mid-cycle
            // and never came back.
            var machine = new MockMachine { Status = "Idle", StatusReportCount = 7 };

            var outcome = await MachineWait.HomeAsync(machine, 400);

            Assert.False(outcome.Success);
            Assert.False(machine.IsHomed);
        }

        [Fact]
        public async Task Ready_IsRefused_WhileTheMachineIsStillMoving()
        {
            var machine = new MockMachine { Status = "Run" };

            Assert.False(await MachineWait.EnsureMachineReadyAsync(machine, 300));
        }

        [Fact]
        public async Task EnsureMachineReady_AtADoorHold_IsRefusedAndDoesNotResume()
        {
            var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateClosed);

            Assert.False(await MachineWait.EnsureMachineReadyAsync(machine, 300));
            Assert.Equal(0, machine.CycleStartCount);
        }

        /// <summary>
        /// Without a current G54 the controller would be shifting an origin it cannot
        /// read, so the run fails instead of guessing how deep to cut.
        /// </summary>
        [Fact]
        public async Task Milling_Refuses_WhenTheMachineWillNotReportItsWorkOffsets()
        {
            var machine = new MockMachine
            {
                Status = "Idle",
                // Already at the safety height, so the retract confirms immediately and
                // the run reaches the work-offset query this test is about.
                MachinePosition = new Vector3(0, 0, Constants.SafeClearanceZ),
                WorkOffsetQuerySucceeds = false
            };
            machine.LoadFile("G21", "G90", "G1 X1 Y1 F100");

            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { DepthAdjustment = -0.05f, RequireHoming = false }
            };

            ControllerError? error = null;
            controller.ErrorOccurred += e => error = e;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await controller.StartAsync(cts.Token);

            Assert.Equal(ControllerState.Failed, controller.State);
            Assert.NotNull(error);
            Assert.Contains("work offsets", error!.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(machine.WorkOffsetQueryCount > 0);
        }

        /// <summary>
        /// With a tool-length offset live, the combined WCO and G54 differ. The depth
        /// adjustment is written with G10 L2 P1, which sets G54 alone, so it has to be
        /// computed from G54 - starting from the combined figure would re-datum Z.
        /// </summary>
        [Fact]
        public async Task DepthAdjustment_IsComputedFromG54_NotTheCombinedWorkOffset()
        {
            using var machine = new FakeMachine
            {
                ExtraOffset = new Vector3(0, 0, 3.0)   // a live tool-length offset
            };
            machine.LoadFile("G21", "G90", "G1 X1 Y1 F100");

            const double adjustment = -0.05;
            double g54Before = machine.G54Offset.Z;

            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { DepthAdjustment = (float)adjustment, RequireHoming = false }
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try
            {
                await controller.StartAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
            }

            // Assert on the offset the run wrote, not the end state - the run restores it
            // on the way out, which is a different guarantee.
            string prefix = GrblProtocol.CmdSetWorkOffset + " Z";
            string? applied = machine.SentCommands.FirstOrDefault(c => c.StartsWith(prefix));

            Assert.NotNull(applied);

            double written = double.Parse(applied!.Substring(prefix.Length),
                System.Globalization.CultureInfo.InvariantCulture);

            // Computed from the combined work offset the written value would be 2.95,
            // carrying the 3mm tool-length offset into the G54 slot and re-datuming Z.
            Assert.Equal(g54Before + adjustment, written, precision: 3);
        }
    
        /// <summary>
        /// A machine with homing switched off answers $H with error:5. Reporting only
        /// "homing failed" sends the operator hunting for a fault that is not there, when
        /// GRBL already reported the cause.
        /// </summary>
        [Fact]
        public async Task Home_ExplainsThatTheMachineHasHomingDisabled()
        {
            var machine = new MockMachine { Status = "Idle", StatusReportCount = 1 };

            using var reporting = new CancellationTokenSource();
            var pump = Task.Run(async () =>
            {
                machine.SimulateRejection(GrblRejection.HomingNotEnabled, "$H");

                while (!reporting.IsCancellationRequested)
                {
                    machine.StatusReportCount++;
                    await Task.Delay(20);
                }
            });

            var outcome = await MachineWait.HomeAsync(machine, 500);
            reporting.Cancel();
            await pump;

            Assert.False(outcome.Success);
            Assert.NotNull(outcome.Reason);
            Assert.Contains("$22", outcome.Reason!);
        }

        [Fact]
        public void Home_ReturnsNoReason_WhenTheMachineReportedNone()
        {
            var outcome = HomingOutcome.Refused(null);

            Assert.False(outcome.Success);
            Assert.Null(outcome.Reason);
        }
    
        /// <summary>
        /// The completion check cannot separate "never started" from "finished", so an
        /// unstarted stream would otherwise sit at Idle indefinitely with nothing reported.
        /// </summary>
        [Fact]
        public async Task Milling_FailsLoudly_WhenTheFileCannotStartStreaming()
        {
            var machine = new MockMachine
            {
                Status = "Idle",
                RefuseFileStart = true,
                MachinePosition = new Vector3(0, 0, Constants.SafeClearanceZ)
            };
            machine.LoadFile("G21", "G90", "G1 X1 Y1 F100");

            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { RequireHoming = false }
            };

            ControllerError? error = null;
            controller.ErrorOccurred += e => error = e;

            // Well above the controller's own timeouts, so a pass means the run ended by itself.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await controller.StartAsync(cts.Token);

            Assert.Equal(ControllerState.Failed, controller.State);
            Assert.NotNull(error);
            Assert.Contains("did not start", error!.Message, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A probe run that ends right before milling can leave the machine in Probe mode.
        /// Milling returns it to Manual up front, so the job streams instead of stalling at
        /// Idle.
        /// </summary>
        [Fact]
        public async Task Milling_RecoversFromLeftoverProbeMode()
        {
            using var machine = new FakeMachine();
            machine.SimulateModeChange(coppercli.Core.Communication.Machine.OperatingMode.Probe);
            machine.LoadFile("G21", "G90", "G1 X1 Y1 F100");

            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { RequireHoming = false }
            };

            ControllerError? error = null;
            controller.ErrorOccurred += e => error = e;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await controller.StartAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
            }

            Assert.True(error == null || !error.Message.Contains("did not start"),
                "Milling should recover a leftover probe mode, not fail to start.");
        }

        [Fact]
        public async Task Milling_Completes_WhenTheFileStreamsNormally()
        {
            using var machine = new FakeMachine();
            machine.LoadFile("G21", "G90", "G1 X1 Y1 F100");

            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { RequireHoming = false }
            };

            // Well above the controller's own timeouts, so reaching it means the run never
            // ended by itself. Nothing catches the cancellation here, so a stalled run fails
            // the test.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await controller.StartAsync(cts.Token);

            Assert.Equal(ControllerState.Completed, controller.State);
        }
    }
}
