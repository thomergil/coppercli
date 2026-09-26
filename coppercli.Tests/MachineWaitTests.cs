using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using coppercli.Core.Controllers;
using coppercli.Core.Util;
using coppercli.Tests.Fakes;
using static coppercli.Core.Util.Constants;
using Xunit;

namespace coppercli.Tests
{
    // Covers MachineWait: the status predicates, the polling waits, the door hold release,
    // and the derived activity and control values every screen reads. MockMachine is the
    // only machine these run against, so GRBL behavior it does not simulate is untested.
    [Collection(TimingSensitiveCollection.Name)]
    public class MachineWaitTests
    {
        /// <summary>Long enough that a wait running to its timeout fails the test.</summary>
        private const int HangDetectTimeoutMs = 4000;
        private const double RetractStartZ = -10.0;
        private const double RetractTargetZ = -1.0;

        [Fact]
        public void IsIdle_WhenIdle_ReturnsTrue()
        {
            var machine = new MockMachine { Status = "Idle" };
            Assert.True(MachineWait.IsIdle(machine));
        }

        [Fact]
        public void IsIdle_WhenRun_ReturnsFalse()
        {
            var machine = new MockMachine { Status = "Run" };
            Assert.False(MachineWait.IsIdle(machine));
        }

        [Fact]
        public void IsAlarm_WhenAlarm_ReturnsTrue()
        {
            var machine = new MockMachine { Status = GrblProtocol.StatusAlarm, StatusSubState = "1" };
            Assert.True(MachineWait.IsAlarm(machine));
        }

        [Fact]
        public void IsAlarm_WhenIdle_ReturnsFalse()
        {
            var machine = new MockMachine { Status = "Idle" };
            Assert.False(MachineWait.IsAlarm(machine));
        }

        [Fact]
        public void IsHold_WhenHold_ReturnsTrue()
        {
            var machine = new MockMachine { Status = GrblProtocol.StatusHold };
            Assert.True(MachineWait.IsHold(machine));
        }

        [Fact]
        public void IsDoor_WhenDoor_ReturnsTrue()
        {
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateClosed);
            Assert.True(MachineWait.IsDoor(machine));
        }

        [Fact]
        public void IsUnavailable_WhenAlarm_ReturnsTrue()
        {
            var machine = new MockMachine { Status = GrblProtocol.StatusAlarm, StatusSubState = "2" };
            Assert.True(MachineWait.IsUnavailable(machine));
        }

        [Fact]
        public void IsUnavailable_WhenDoor_ReturnsTrue()
        {
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateAjar);
            Assert.True(MachineWait.IsUnavailable(machine));
        }

        [Fact]
        public void IsUnavailable_WhenIdle_ReturnsFalse()
        {
            var machine = new MockMachine { Status = "Idle" };
            Assert.False(MachineWait.IsUnavailable(machine));
        }

        [Fact]
        public async Task WaitForIdleAsync_WhenAlreadyIdle_ReturnsImmediately()
        {
            var machine = new MockMachine { Status = "Idle" };

            var result = await MachineWait.WaitForIdleAsync(machine, 1000);

            Assert.True(result);
        }

        [Fact]
        public async Task WaitForIdleAsync_WhenBecomesIdle_ReturnsTrue()
        {
            var machine = new MockMachine { Status = "Run" };

            var waitTask = MachineWait.WaitForIdleAsync(machine, 5000);

            await Task.Delay(100);
            machine.SimulateStatusChange("Idle");

            var result = await waitTask;
            Assert.True(result);
        }

        [Fact]
        public async Task WaitForIdleAsync_OnTimeout_ReturnsFalse()
        {
            var machine = new MockMachine { Status = "Run" };

            var result = await MachineWait.WaitForIdleAsync(machine, 200);

            Assert.False(result);
        }

        [Fact]
        public async Task WaitForIdleAsync_WithCancellation_ThrowsOperationCanceled()
        {
            var machine = new MockMachine { Status = "Run" };
            var cts = new CancellationTokenSource();

            var waitTask = MachineWait.WaitForIdleAsync(machine, 10000, cts.Token);

            await Task.Delay(50);
            cts.Cancel();

            await Assert.ThrowsAsync<TaskCanceledException>(() => waitTask);
        }

        [Fact]
        public async Task WaitForZHeightAsync_WhenAtTarget_ReturnsTrue()
        {
            var machine = new MockMachine
            {
                WorkPosition = new Vector3(0, 0, 5.0)
            };

            var result = await MachineWait.WaitForZHeightAsync(machine, 5.0, 1000);

            Assert.True(result);
        }

        [Fact]
        public async Task WaitForZHeightAsync_WhenWithinTolerance_ReturnsTrue()
        {
            var machine = new MockMachine
            {
                WorkPosition = new Vector3(0, 0, 5.05) // inside PositionToleranceMm
            };

            var result = await MachineWait.WaitForZHeightAsync(machine, 5.0, 1000);

            Assert.True(result);
        }

        [Fact]
        public async Task WaitForZHeightAsync_WhenNeverReachesTarget_ReturnsFalse()
        {
            var machine = new MockMachine
            {
                WorkPosition = new Vector3(0, 0, 10.0)
            };

            var result = await MachineWait.WaitForZHeightAsync(machine, 5.0, 200);

            Assert.False(result);
        }

        [Fact]
        public async Task ASafetyRetract_IsCheckedInMachineCoordinates()
        {
            // G53 moves are in machine coordinates. Starting at the target would take the
            // already-there shortcut and skip the wait under test, so the machine starts
            // 50mm away and arrives mid-wait while work Z stays far from the target.
            var machine = new MockMachine
            {
                Status = GrblProtocol.StatusIdle,
                MachinePosition = new Vector3(0, 0, -50.0),
                WorkPosition = new Vector3(0, 0, 10.0)
            };

            _ = Task.Run(async () =>
            {
                await Task.Delay(StatusPollIntervalMs * 2);
                machine.MachinePosition = new Vector3(0, 0, -1.0);
            });

            Assert.True(await MachineWait.SafetyRetractZAsync(machine, -1.0, HangDetectTimeoutMs),
                "the retract was judged on work Z, which never reaches the G53 target");
        }

        // Closing the door does not end the hold: GRBL stays in Door until it receives a
        // cycle start. Everything the caller does after a release is machine motion, so the
        // return value has to mean the hold lifted.
        [Fact]
        public async Task Release_SendsOneCycleStartAndConfirmsTheHoldLifted()
        {
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateClosed);

            var left = await MachineWait.ReleaseDoorHoldAsync(machine, HangDetectTimeoutMs);

            Assert.Equal(DoorState.None, left);
            Assert.Equal(1, machine.CycleStartCount);
            Assert.False(MachineWait.IsDoor(machine));
        }

        [Fact]
        public async Task Release_SendsNoCycleStartWhileTheDoorReadsAjar()
        {
            // The operator's answer says they are clear of the machine, not that the door is
            // shut. A cycle start sent now stays pending in GRBL and starts the machine
            // whenever the door is closed later, unattended.
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateAjar);

            var elapsed = Stopwatch.StartNew();
            var left = await MachineWait.ReleaseDoorHoldAsync(machine, HangDetectTimeoutMs);
            elapsed.Stop();

            Assert.NotEqual(DoorState.None, left);
            Assert.Equal(0, machine.CycleStartCount);
            Assert.True(MachineWait.IsDoor(machine));
            Assert.True(elapsed.ElapsedMilliseconds < HangDetectTimeoutMs / 2,
                $"waited {elapsed.ElapsedMilliseconds}ms on a door that stayed open");
        }

        [Fact]
        public async Task DoorClosedOnALaterPoll_IsStillReleased()
        {
            // The substate arrives on the status poll, so the report received when the
            // operator answers is one poll old. Without the catch-up allowance they are
            // prompted again immediately after closing the door.
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateAjar);

            _ = Task.Run(async () =>
            {
                await Task.Delay(StatusPollIntervalMs);
                machine.SimulateDoorClosedAndHolding();
            });

            var left = await MachineWait.ReleaseDoorHoldAsync(machine, HangDetectTimeoutMs);

            Assert.Equal(DoorState.None, left);
            Assert.Equal(1, machine.CycleStartCount);
        }

        [Fact]
        public async Task TheCatchUpAndTheRelease_ShareOneTimeout()
        {
            // The restore outlasts the timeout. Elapsed time shows whether the switch
            // reading wait and release wait share that timeout.
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateAjar);
            machine.DoorRestoreMs = StatusPollIntervalMs * 8;

            _ = Task.Run(async () =>
            {
                await Task.Delay(StatusPollIntervalMs * 3);
                machine.SimulateDoorClosedAndHolding();
            });

            var elapsed = Stopwatch.StartNew();
            var left = await MachineWait.ReleaseDoorHoldAsync(machine, StatusPollIntervalMs * 6);
            elapsed.Stop();

            Assert.NotEqual(DoorState.None, left);
            Assert.True(elapsed.ElapsedMilliseconds < StatusPollIntervalMs * 10,
                $"spent {elapsed.ElapsedMilliseconds}ms with a timeout of {StatusPollIntervalMs * 6}ms");
        }

        [Fact]
        public async Task Release_WaitsOutTheParkRestoreBeforeReporting()
        {
            // GRBL reports Door:3 while it restores from the park, which is a real move.
            // Reporting released before it finishes would let the caller send the next move
            // into it.
            var machine = new MockMachine
            {
                Status = GrblProtocol.StatusDoor,
                StatusSubState = GrblProtocol.DoorSubStateClosed,
                DoorRestoreMs = StatusPollIntervalMs * 6
            };

            var left = await MachineWait.ReleaseDoorHoldAsync(machine, HangDetectTimeoutMs);

            Assert.Equal(DoorState.None, left);
            Assert.Equal(1, machine.CycleStartCount);
            Assert.False(MachineWait.IsDoor(machine));
        }

        [Fact]
        public async Task ARestoreThatOutlastsTheTimeout_IsReportedNotAssumedDone()
        {
            var machine = new MockMachine
            {
                Status = GrblProtocol.StatusDoor,
                StatusSubState = GrblProtocol.DoorSubStateClosed,
                DoorRestoreMs = HangDetectTimeoutMs * 4
            };

            var left = await MachineWait.ReleaseDoorHoldAsync(machine, StatusPollIntervalMs * 4);

            Assert.NotEqual(DoorState.None, left);
            Assert.True(MachineWait.IsDoor(machine));
        }

        [Fact]
        public async Task Release_WhileRestoring_SendsNoSecondCycleStart()
        {
            // A cycle start sent during a restore would arrive in the middle of a move.
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateResuming);

            _ = Task.Run(async () =>
            {
                await Task.Delay(StatusPollIntervalMs * 2);
                machine.SimulateStatusChange(GrblProtocol.StatusIdle);
            });

            var left = await MachineWait.ReleaseDoorHoldAsync(machine, HangDetectTimeoutMs);

            Assert.Equal(DoorState.None, left);
            Assert.Equal(0, machine.CycleStartCount);
        }

        /// <summary>
        /// GRBL takes no cycle start until the park move ends. The release waits only for a
        /// fresh reading, not for the retract to end.
        /// </summary>
        [Fact]
        public async Task Release_WhileRetracting_SendsNothingAndDoesNotWaitOutTheRetract()
        {
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateRetracting);
            var elapsed = Stopwatch.StartNew();

            var left = await MachineWait.ReleaseDoorHoldAsync(machine, HangDetectTimeoutMs);

            Assert.Equal(DoorState.Retracting, left);
            Assert.Equal(0, machine.CycleStartCount);
            Assert.True(elapsed.ElapsedMilliseconds < HangDetectTimeoutMs / 2, "the release waited out the retract");
        }

        /// <summary>
        /// The retract has just ended but the last report still reads it. The release waits for
        /// a fresh reading instead of refusing on a stale one.
        /// </summary>
        [Fact]
        public async Task Release_OnAStaleRetractReading_WaitsForTheClosedDoorAndReleases()
        {
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateRetracting);
            // Flipped on the next report rather than after a delay, so a slow thread pool cannot
            // push the change past the catch-up window.
            long start = machine.StatusReportCount;
            _ = Task.Run(async () =>
            {
                while (machine.StatusReportCount == start)
                {
                    await Task.Delay(1);
                }
                machine.StatusSubState = GrblProtocol.DoorSubStateClosed;
            });

            await MachineWait.ReleaseDoorHoldAsync(machine, HangDetectTimeoutMs);

            Assert.Equal(1, machine.CycleStartCount);
        }

        /// <summary>
        /// Resume and release are refused at the door, and only an open door is told to close:
        /// the others may already be closed.
        /// </summary>
        [Theory]
        [InlineData(DoorState.None, null)]
        [InlineData(DoorState.Open, ControllerConstants.ErrorDoorBlocksResume)]
        [InlineData(DoorState.WaitingForResume, ControllerConstants.ErrorDoorClosedStillHolding)]
        [InlineData(DoorState.Retracting, ControllerConstants.DoorRetractingMessage)]
        [InlineData(DoorState.Resuming, ControllerConstants.DoorResumingMessage)]
        public void GetDoorRefusal_NamesAMoveStillRunningRatherThanTheDoor(DoorState state, string? expected)
        {
            Assert.Equal(expected, MachineWait.GetDoorRefusal(state));
        }

        [Fact]
        public void DoorRefusal_WhileRetracting_DoesNotSayCloseTheDoor()
        {
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateRetracting);

            Assert.Equal(ControllerConstants.DoorRetractingMessage, MachineWait.GetDoorRefusal(machine));
        }

        [Fact]
        public async Task ReleaseCancelledBeforeTheCycleStart_SendsNothing()
        {
            // Stop pressed between the operator's answer and the resume. The cycle start
            // restarts the spindle, so a cancelled run must not send one.
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateClosed);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => MachineWait.ReleaseDoorHoldAsync(machine, HangDetectTimeoutMs, cts.Token));
            Assert.Equal(0, machine.CycleStartCount);
        }

        [Fact]
        public async Task Release_WhenNotHolding_SendsNothing()
        {
            var machine = new MockMachine { Status = GrblProtocol.StatusIdle };

            var left = await MachineWait.ReleaseDoorHoldAsync(machine, HangDetectTimeoutMs);

            Assert.Equal(DoorState.None, left);
            Assert.Equal(0, machine.CycleStartCount);
        }

        /// <summary>
        /// Releasing a door hold waits for the machine to leave Door. WaitForIdleAsync cannot
        /// do that: it treats a door as a reason to stop waiting and gives up on its first
        /// poll, so a release built on it would return before the hold lifted.
        /// </summary>
        [Fact]
        public async Task ADoorHold_IsWaitedOutRatherThanGivenUpOn()
        {
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateClosed);
            machine.DoorRestoreMs = Constants.StatusPollIntervalMs * 3;

            var left = await MachineWait.ReleaseDoorHoldAsync(machine, HangDetectTimeoutMs);

            Assert.Equal(DoorState.None, left);

            using var stillHolding = MockMachine.AtADoor(GrblProtocol.DoorSubStateClosed);

            var refused = Stopwatch.StartNew();
            bool wentIdle = await MachineWait.WaitForIdleAsync(stillHolding, HangDetectTimeoutMs);
            refused.Stop();

            Assert.False(wentIdle);
            Assert.True(refused.ElapsedMilliseconds < Constants.StatusPollIntervalMs * 2);
        }

        /// <summary>
        /// IgnoreCycleStart models a door switch that reads closed but never lets GRBL
        /// resume, so the release returns the state the machine is still in.
        /// </summary>
        [Fact]
        public async Task AHoldThatNeverLifts_IsReportedAsStillHolding()
        {
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateClosed);
            machine.IgnoreCycleStart = true;

            var left = await MachineWait.ReleaseDoorHoldAsync(
                machine, Constants.StatusPollIntervalMs * 2);

            Assert.Equal(DoorState.WaitingForResume, left);
        }

        [Fact]
        public async Task EnsureMachineReadyAsync_WhenIdle_ReturnsTrue()
        {
            var machine = new MockMachine { Status = "Idle" };

            var result = await MachineWait.EnsureMachineReadyAsync(machine, 1000);

            Assert.True(result);
        }

        [Fact]
        public async Task EnsureMachineReadyAsync_WhenAlarm_ReturnsFalse()
        {
            var machine = new MockMachine { Status = GrblProtocol.StatusAlarm, StatusSubState = "1" };

            var result = await MachineWait.EnsureMachineReadyAsync(machine, 200);

            Assert.False(result);
        }

        /// <summary>
        /// CycleStart restarts the spindle and resumes motion while the operator may be
        /// reaching in, so only the operator asks for it, through MillingController's prompt.
        /// </summary>
        [Fact]
        public async Task EnsureMachineReadyAsync_DoesNotResumeADoorHold()
        {
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateClosed);

            var result = await MachineWait.EnsureMachineReadyAsync(machine, 500);

            Assert.Equal(0, machine.CycleStartCount);
            Assert.False(result);
        }

        [Fact]
        public async Task EnsureMachineReadyAsync_IsNotReadyWhileStillMoving()
        {
            var machine = new MockMachine { Status = "Run" };

            var result = await MachineWait.EnsureMachineReadyAsync(machine, 500);

            Assert.False(result);
        }

        [Fact]
        public async Task EnsureMachineReadyAsync_IsReadyWhenIdle()
        {
            var machine = new MockMachine { Status = "Idle" };

            var result = await MachineWait.EnsureMachineReadyAsync(machine, 500);

            Assert.True(result);
        }

        [Fact]
        public async Task SafetyRetractZAsync_SendsCorrectCommands()
        {
            var machine = new MockMachine
            {
                Status = "Idle",
                MachinePosition = new Vector3(0, 0, -50)
            };

            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                machine.MachinePosition = new Vector3(0, 0, -1.0);
                machine.SimulateStatusChange("Run");
                await Task.Delay(50);
                machine.SimulateStatusChange("Idle");
            });

            await MachineWait.SafetyRetractZAsync(machine, -1.0, 2000);

            Assert.True(machine.WasCommandSent("G90"));
            Assert.True(machine.WasCommandSentMatching(@"G53.*G0.*Z-1"));
        }

        [Fact]
        public async Task SafetyRetractZAsync_WhenAlreadyAtTarget_ReturnsQuickly()
        {
            var machine = new MockMachine
            {
                Status = "Idle",
                MachinePosition = new Vector3(0, 0, -1.0)
            };

            // Stopwatch rather than wall-clock time: a clock adjustment mid-test would
            // change the elapsed figure this asserts on.
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            await MachineWait.SafetyRetractZAsync(machine, -1.0, 5000);

            Assert.True(elapsed.ElapsedMilliseconds < 1000,
                $"already at the target height and it took {elapsed.ElapsedMilliseconds}ms");
        }
        /// <summary>
        /// The numbers GRBL writes, from its report.c: 0 closed and ready to resume, 1 ajar,
        /// 2 parking retract running, 3 restoring from the park. Every door test feeds these
        /// constants through a double that reads the same constants, so without this test a
        /// mistyped digit would not show up.
        /// </summary>
        [Fact]
        public void TheDoorSubstates_MatchTheNumbersGrblSends()
        {
            Assert.Equal("0", GrblProtocol.DoorSubStateClosed);
            Assert.Equal("1", GrblProtocol.DoorSubStateAjar);
            Assert.Equal("2", GrblProtocol.DoorSubStateRetracting);
            Assert.Equal("3", GrblProtocol.DoorSubStateResuming);
        }

        /// <summary>
        /// Each GRBL door substate maps to one door state; an unrecognized or empty substate
        /// maps to Open.
        /// </summary>
        [Theory]
        [InlineData(GrblProtocol.DoorSubStateClosed, DoorState.WaitingForResume)]
        [InlineData(GrblProtocol.DoorSubStateAjar, DoorState.Open)]
        [InlineData(GrblProtocol.DoorSubStateRetracting, DoorState.Retracting)]
        [InlineData(GrblProtocol.DoorSubStateResuming, DoorState.Resuming)]
        [InlineData("9", DoorState.Open)]
        [InlineData("", DoorState.Open)]
        public void GetDoorState_NamesEachDoorState(string subState, DoorState expected)
        {
            Assert.Equal(expected, MachineWait.GetDoorState(MockMachine.AtADoor(subState)));
        }

        /// <summary>
        /// One mapping for the whole enum. Without a case here, a screen has to branch on
        /// GRBL's status word itself.
        /// </summary>
        [Theory]
        [InlineData(GrblProtocol.StatusIdle, "", MachineActivity.Idle)]
        [InlineData(GrblProtocol.StatusRun, "", MachineActivity.Running)]
        [InlineData(GrblProtocol.StatusHold, GrblProtocol.HoldSubStateComplete, MachineActivity.Hold)]
        [InlineData(GrblProtocol.StatusAlarm, GrblProtocol.AlarmSubStateHardLimit, MachineActivity.Alarm)]
        [InlineData(GrblProtocol.StatusDoor, GrblProtocol.DoorSubStateAjar, MachineActivity.DoorOpen)]
        [InlineData(GrblProtocol.StatusDoor, GrblProtocol.DoorSubStateRetracting, MachineActivity.DoorRetracting)]
        [InlineData(GrblProtocol.StatusDoor, GrblProtocol.DoorSubStateClosed, MachineActivity.DoorHolding)]
        [InlineData(GrblProtocol.StatusDoor, GrblProtocol.DoorSubStateResuming, MachineActivity.DoorResuming)]
        [InlineData(GrblProtocol.StatusDisconnected, "", MachineActivity.Disconnected)]
        [InlineData(GrblProtocol.StatusHome, "", MachineActivity.Other)]
        [InlineData(GrblProtocol.StatusJog, "", MachineActivity.Other)]
        [InlineData(GrblProtocol.StatusCheck, "", MachineActivity.Other)]
        [InlineData(GrblProtocol.StatusSleep, "", MachineActivity.Sleep)]
        public void GetActivity_MapsGrblStatesToActivities(
            string status, string subState, MachineActivity expected)
        {
            var machine = new MockMachine { Status = status, StatusSubState = subState };

            Assert.Equal(expected, MachineWait.GetActivity(machine));
        }

        /// <summary>
        /// The link is checked before the status word. Disconnect clears Connected before it
        /// writes the status word, so during that window the machine reports no link and
        /// GRBL's last status word at the same time.
        /// </summary>
        [Theory]
        [InlineData(GrblProtocol.StatusIdle)]
        [InlineData(GrblProtocol.StatusRun)]
        [InlineData(GrblProtocol.StatusHold)]
        [InlineData(GrblProtocol.StatusAlarm)]
        [InlineData(GrblProtocol.StatusDoor)]
        public void NoConnection_ReadsAsDisconnected(string lastReported)
        {
            var machine = new MockMachine { Status = lastReported, Connected = false };

            Assert.Equal(MachineActivity.Disconnected, MachineWait.GetActivity(machine));
        }

        /// <summary>
        /// The one input on which IsUnavailable and NeedsAttention differ. Without this test
        /// they could be merged into one predicate and nothing would fail.
        /// </summary>
        [Theory]
        [InlineData(GrblProtocol.StatusAlarm)]
        [InlineData(GrblProtocol.StatusDoor)]
        public void DisconnectedMachine_IsUnavailableButNeedsNoAttention(string lastReported)
        {
            var machine = new MockMachine { Status = lastReported, Connected = false };

            Assert.True(MachineWait.IsUnavailable(machine));
            Assert.False(MachineWait.NeedsAttention(machine));
        }

        /// <summary>
        /// Screens disable command controls on this, so a state added to the enum without an
        /// entry here would leave those controls enabled.
        /// </summary>
        [Theory]
        [InlineData(GrblProtocol.StatusAlarm, GrblProtocol.AlarmSubStateHardLimit, true)]
        [InlineData(GrblProtocol.StatusDoor, GrblProtocol.DoorSubStateAjar, true)]
        [InlineData(GrblProtocol.StatusDoor, GrblProtocol.DoorSubStateRetracting, true)]
        [InlineData(GrblProtocol.StatusDoor, GrblProtocol.DoorSubStateClosed, true)]
        [InlineData(GrblProtocol.StatusDoor, GrblProtocol.DoorSubStateResuming, true)]
        [InlineData(GrblProtocol.StatusHold, GrblProtocol.HoldSubStateComplete, false)]
        [InlineData(GrblProtocol.StatusRun, "", false)]
        [InlineData(GrblProtocol.StatusIdle, "", false)]
        [InlineData(GrblProtocol.StatusDisconnected, "", false)]
        [InlineData(GrblProtocol.StatusJog, "", false)]
        [InlineData(GrblProtocol.StatusSleep, "", true)]
        public void NeedsAttention_ListsTheStatesThatNeedTheOperator(
            string status, string subState, bool expected)
        {
            var machine = new MockMachine { Status = status, StatusSubState = subState };

            Assert.Equal(expected, MachineWait.NeedsAttention(machine));
        }

        /// <summary>
        /// One row per activity. EveryActivity_HasARowInTheControlPredicateTable reads these rows
        /// back, so an activity added to the enum without a row here fails that test.
        /// </summary>
        [Theory]
        [InlineData(MachineActivity.Disconnected, false, false, false, false, true, false)]
        [InlineData(MachineActivity.Alarm, true, true, false, false, true, true)]
        [InlineData(MachineActivity.DoorOpen, true, true, false, false, true, false)]
        [InlineData(MachineActivity.DoorRetracting, true, true, false, false, true, false)]
        [InlineData(MachineActivity.DoorHolding, true, true, false, false, true, false)]
        [InlineData(MachineActivity.DoorResuming, true, true, false, false, true, false)]
        [InlineData(MachineActivity.Hold, true, false, false, true, false, false)]
        [InlineData(MachineActivity.Running, true, false, true, false, false, false)]
        [InlineData(MachineActivity.Idle, true, false, false, false, false, false)]
        [InlineData(MachineActivity.Sleep, true, true, false, false, true, true)]
        [InlineData(MachineActivity.Other, true, false, false, false, false, false)]
        public void EveryActivity_HasTheExpectedControlPredicates(
            MachineActivity activity, bool isAnswering, bool needsAttention,
            bool canPause, bool canResume, bool isUnavailable, bool blocksJobStart)
        {
            Assert.Equal(isAnswering, MachineWait.IsResponding(activity));
            Assert.Equal(needsAttention, MachineWait.NeedsAttention(activity));
            Assert.Equal(canPause, MachineWait.CanPause(activity));
            Assert.Equal(canResume, MachineWait.CanResume(activity));
            Assert.Equal(isUnavailable, MachineWait.IsUnavailable(activity));

            // A door must not block a start: the run prompts about it. Folding this back
            // into NeedsAttention grays out Mill and Probe at an open enclosure.
            Assert.Equal(blocksJobStart, MachineWait.BlocksJobStart(activity));
        }

        [Fact]
        public void EveryActivity_HasARowInTheControlPredicateTable()
        {
            var decided = typeof(MachineWaitTests)
                .GetMethod(nameof(EveryActivity_HasTheExpectedControlPredicates))!
                .GetCustomAttributes(typeof(InlineDataAttribute), false)
                .Cast<InlineDataAttribute>()
                .Select(row => (MachineActivity)row.GetData(null!).Single()[0]!)
                .ToHashSet();

            Assert.Equal(Enum.GetValues<MachineActivity>().ToHashSet(), decided);
        }

        [Fact]
        public void MachineNotAtDoor_HasNoDoorState()
        {
            Assert.Equal(DoorState.None,
                MachineWait.GetDoorState(new MockMachine { Status = GrblProtocol.StatusIdle }));
        }

        // A door opened mid-cycle leaves GRBL holding: it will not reach Idle and will not
        // move until the operator acts. A wait that checks only the deadline runs the full
        // timeout and then reports an error that does not mention the door.
        [Fact]
        public async Task WaitingForIdle_StopsAsSoonAsTheDoorHolds()
        {
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateClosed);

            var elapsed = Stopwatch.StartNew();
            bool idle = await MachineWait.WaitForIdleAsync(machine, HangDetectTimeoutMs);
            elapsed.Stop();

            Assert.False(idle);
            Assert.True(elapsed.ElapsedMilliseconds < HangDetectTimeoutMs / 2,
                $"waited {elapsed.ElapsedMilliseconds}ms on a door hold that can never clear itself");
        }

        [Fact]
        public async Task WaitForIdle_ReturnsFalseImmediatelyOnAlarm()
        {
            var machine = new MockMachine { Status = GrblProtocol.StatusAlarm };

            var elapsed = Stopwatch.StartNew();
            bool idle = await MachineWait.WaitForIdleAsync(machine, HangDetectTimeoutMs);
            elapsed.Stop();

            Assert.False(idle);
            Assert.True(elapsed.ElapsedMilliseconds < HangDetectTimeoutMs / 2,
                $"waited {elapsed.ElapsedMilliseconds}ms on an alarm that can never clear itself");
        }

        [Fact]
        public async Task WaitingForAZHeight_StopsAsSoonAsTheDoorHolds()
        {
            var machine = new MockMachine
            {
                Status = GrblProtocol.StatusDoor,
                StatusSubState = GrblProtocol.DoorSubStateAjar,
                MachinePosition = new Vector3(0, 0, RetractStartZ)
            };

            var elapsed = Stopwatch.StartNew();
            bool reached = await MachineWait.SafetyRetractZAsync(machine, RetractTargetZ, HangDetectTimeoutMs);
            elapsed.Stop();

            // A retract that cannot be confirmed must fail, and fail promptly: everything
            // after it is XY motion that would drag the tool across the work.
            Assert.False(reached);
            Assert.True(elapsed.ElapsedMilliseconds < HangDetectTimeoutMs / 2,
                $"waited {elapsed.ElapsedMilliseconds}ms for a Z move a held machine will never make");
        }

        /// <summary>
        /// A probe reply cannot arrive from a machine parked at the door, so the wait gives
        /// up as soon as it parks. Without that check the run waits out the full
        /// ProbeReplyTimeoutMs: three minutes with the tool down and nothing on screen.
        /// </summary>
        [Fact]
        public async Task ReplyWait_ThrowsWhenDoorOpens()
        {
            var machine = new MockMachine { Status = GrblProtocol.StatusRun };
            var neverAnswers = new TaskCompletionSource<bool>();

            _ = Task.Run(async () =>
            {
                await Task.Delay(StatusPollIntervalMs * 2);
                machine.SimulateDoorOpen();
            });

            var elapsed = Stopwatch.StartNew();
            var failure = await Assert.ThrowsAsync<TimeoutException>(() =>
                MachineWait.AwaitReplyOrTimeoutAsync(
                    neverAnswers.Task, HangDetectTimeoutMs, "reply timed out",
                    CancellationToken.None, machine));
            elapsed.Stop();

            Assert.Equal(ControllerConstants.ErrorMachineNotResponding, failure.Message);
            Assert.True(elapsed.ElapsedMilliseconds < HangDetectTimeoutMs / 2,
                $"waited {elapsed.ElapsedMilliseconds}ms for a reply the parked machine cannot send");
        }

        /// <summary>
        /// A condition can become true as WaitUntilAsync reaches its timeout. Its final check
        /// must report success in that case, even when a zero timeout skips the polling loop.
        /// </summary>
        [Fact]
        public async Task AlreadyTrueAtZeroTimeout_Succeeds()
        {
            using var machine = new MockMachine { Status = GrblProtocol.StatusIdle, Connected = true };

            Assert.True(
                await MachineWait.WaitForIdleAsync(machine, 0),
                "a machine already Idle at the timeout was reported as timed out");
        }

        [Fact]
        public async Task StillFalseAtZeroTimeout_Fails()
        {
            using var machine = new MockMachine { Status = GrblProtocol.StatusRun, Connected = true };

            Assert.False(await MachineWait.WaitForIdleAsync(machine, 0));
        }
    }
}
