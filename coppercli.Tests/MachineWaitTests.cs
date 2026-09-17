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
    /// <summary>
    /// Tests for MachineWait utility methods.
    /// </summary>
    public class MachineWaitTests
    {
        /// <summary>Long enough that sitting it out would be an obvious failure.</summary>
        private const int HangDetectTimeoutMs = 4000;
        private const double RetractStartZ = -10.0;
        private const double RetractTargetZ = -1.0;

        // =========================================================================
        // Status check tests
        // =========================================================================

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

        // =========================================================================
        // WaitForIdleAsync tests
        // =========================================================================

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

            // Simulate status change after short delay
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

            // Standard .NET pattern: cancellation throws TaskCanceledException
            await Assert.ThrowsAsync<TaskCanceledException>(() => waitTask);
        }

        // =========================================================================
        // WaitForZHeightAsync tests
        // =========================================================================

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
                WorkPosition = new Vector3(0, 0, 5.05) // Within 0.1mm tolerance
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
            // already-there shortcut and skip the wait under test, so it starts 50mm away
            // and arrives mid-wait, with work Z nowhere near the target.
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

        // =========================================================================
        // ReleaseDoorHoldAsync tests
        //
        // Closing the door does not end the hold; GRBL waits for a cycle start (see
        // MachineWait.GetDoorState). Two things must hold: the cycle start goes only on
        // GRBL's own reading of the switch, and the return value means the hold actually
        // lifted, because everything the caller does next is machine motion.
        // =========================================================================

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
            // The cycle start goes only when the operator has answered and GRBL reports the
            // door closed. Their answer says they are clear of the machine, not where the
            // door is. Refusing here also stops the answer sitting pending and starting the
            // machine when the door is closed later, unattended.
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateAjar);

            using var reporting = PumpStatusReports(machine);

            var elapsed = Stopwatch.StartNew();
            var left = await MachineWait.ReleaseDoorHoldAsync(machine, HangDetectTimeoutMs);
            elapsed.Stop();

            Assert.NotEqual(DoorState.None, left);
            Assert.Equal(0, machine.CycleStartCount);
            Assert.True(MachineWait.IsDoor(machine));
            Assert.True(elapsed.ElapsedMilliseconds < HangDetectTimeoutMs / 2,
                $"waited {elapsed.ElapsedMilliseconds}ms on a door that stayed open");
        }

        /// <summary>
        /// A connected machine reports continuously. The catch-up allowance is counted in
        /// reports, so a mock that never reports would wait out the whole timeout.
        /// </summary>
        private static IDisposable PumpStatusReports(MockMachine machine)
        {
            var stop = new CancellationTokenSource();
            var pump = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    machine.StatusReportCount++;
                    await Task.Delay(StatusPollIntervalMs / 2);
                }
            });
            return new StatusReportPump(stop, pump);
        }

        /// <summary>
        /// Stops the pump on dispose. Disposing a CancellationTokenSource does not cancel
        /// it, so a `using` over the source alone leaves the loop running.
        /// </summary>
        private sealed class StatusReportPump : IDisposable
        {
            private readonly CancellationTokenSource _stop;
            private readonly Task _pump;

            public StatusReportPump(CancellationTokenSource stop, Task pump)
            {
                _stop = stop;
                _pump = pump;
            }

            public void Dispose()
            {
                _stop.Cancel();
                try { _pump.Wait(HangDetectTimeoutMs); } catch (AggregateException) { }
                _stop.Dispose();
            }
        }

        [Fact]
        public async Task DoorClosedOnALaterPoll_IsStillReleased()
        {
            // The substate arrives on the status poll, so the report in hand when the
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
            // A caller asking for N milliseconds waits N in total, not N per wait. The
            // restore here would finish inside a second full timeout.
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
                $"spent {elapsed.ElapsedMilliseconds}ms on a budget of {StatusPollIntervalMs * 6}ms");
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

        // =========================================================================
        // Waiting out a door hold
        // =========================================================================

        /// <summary>
        /// Releasing a door hold waits for the machine to leave Door. WaitForIdleAsync cannot
        /// do that job: it treats a door as a reason to stop waiting and gives up on its
        /// first poll, which is why the prompt used to reappear at once.
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
        /// A switch that reads closed but never lets GRBL resume leaves the machine where it
        /// was, and the caller is told so rather than being told the door cleared.
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

        // =========================================================================
        // EnsureMachineReadyAsync tests
        // =========================================================================

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
        /// Readiness must never resume the machine on its own. Sending CycleStart to
        /// release a door hold restarts the spindle and resumes motion while the operator
        /// may be reaching in. Only the operator may ask for it, through
        /// MillingController's prompt.
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

        // =========================================================================
        // SafetyRetractZAsync tests
        // =========================================================================

        [Fact]
        public async Task SafetyRetractZAsync_SendsCorrectCommands()
        {
            var machine = new MockMachine
            {
                Status = "Idle",
                MachinePosition = new Vector3(0, 0, -50)
            };

            // Simulate position update
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                machine.MachinePosition = new Vector3(0, 0, -1.0);
                machine.SimulateStatusChange("Run");
                await Task.Delay(50);
                machine.SimulateStatusChange("Idle");
            });

            await MachineWait.SafetyRetractZAsync(machine, -1.0, 2000);

            // Verify G90 (absolute) was sent
            Assert.True(machine.WasCommandSent("G90"));

            // Verify G53 G0 Z-1 was sent (machine coords retract)
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

            // Monotonic: a clock step here would make a prompt return look slow, or a slow
            // one look prompt.
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
        /// The three states cover Door between them, and each maps to the substate GRBL uses
        /// for it. Every screen that shows door text reads this.
        /// </summary>
        [Theory]
        [InlineData(GrblProtocol.DoorSubStateClosed, DoorState.WaitingForResume)]
        [InlineData(GrblProtocol.DoorSubStateAjar, DoorState.Open)]
        [InlineData(GrblProtocol.DoorSubStateRetracting, DoorState.Open)]
        [InlineData(GrblProtocol.DoorSubStateResuming, DoorState.Resuming)]
        [InlineData("9", DoorState.Open)]
        [InlineData("", DoorState.Open)]
        public void GetDoorState_NamesEachDoorState(string subState, DoorState expected)
        {
            Assert.Equal(expected, MachineWait.GetDoorState(MockMachine.AtADoor(subState)));
        }

        /// <summary>
        /// One mapping for the whole enum. A missing case would send a screen back to
        /// branching on GRBL's status word.
        /// </summary>
        [Theory]
        [InlineData(GrblProtocol.StatusIdle, "", MachineActivity.Idle)]
        [InlineData(GrblProtocol.StatusRun, "", MachineActivity.Running)]
        [InlineData(GrblProtocol.StatusHold, GrblProtocol.HoldSubStateComplete, MachineActivity.Hold)]
        [InlineData(GrblProtocol.StatusAlarm, GrblProtocol.AlarmSubStateHardLimit, MachineActivity.Alarm)]
        [InlineData(GrblProtocol.StatusDoor, GrblProtocol.DoorSubStateAjar, MachineActivity.DoorOpen)]
        [InlineData(GrblProtocol.StatusDoor, GrblProtocol.DoorSubStateRetracting, MachineActivity.DoorOpen)]
        [InlineData(GrblProtocol.StatusDoor, GrblProtocol.DoorSubStateClosed, MachineActivity.DoorHolding)]
        [InlineData(GrblProtocol.StatusDoor, GrblProtocol.DoorSubStateResuming, MachineActivity.DoorResuming)]
        [InlineData(GrblProtocol.StatusDisconnected, "", MachineActivity.Disconnected)]
        [InlineData(GrblProtocol.StatusHome, "", MachineActivity.Other)]
        [InlineData(GrblProtocol.StatusJog, "", MachineActivity.Other)]
        [InlineData(GrblProtocol.StatusCheck, "", MachineActivity.Other)]
        [InlineData(GrblProtocol.StatusSleep, "", MachineActivity.Sleep)]
        public void GetActivity_NamesWhatTheMachineIsDoing(
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
        /// Every derived answer, one row per activity, so a new activity cannot be added
        /// without deciding what each control does in it.
        /// </summary>
        [Theory]
        [InlineData(MachineActivity.Disconnected, false, false, false, false, true, false)]
        [InlineData(MachineActivity.Alarm, true, true, false, false, true, true)]
        [InlineData(MachineActivity.DoorOpen, true, true, false, false, true, false)]
        [InlineData(MachineActivity.DoorHolding, true, true, false, false, true, false)]
        [InlineData(MachineActivity.DoorResuming, true, true, false, false, true, false)]
        [InlineData(MachineActivity.Hold, true, false, false, true, false, false)]
        [InlineData(MachineActivity.Running, true, false, true, false, false, false)]
        [InlineData(MachineActivity.Idle, true, false, false, false, false, false)]
        [InlineData(MachineActivity.Sleep, true, true, false, false, true, true)]
        [InlineData(MachineActivity.Other, true, false, false, false, false, false)]
        public void EveryActivity_HasControlAnswers(
            MachineActivity activity, bool isAnswering, bool needsAttention,
            bool canPause, bool canResume, bool isUnavailable, bool blocksJobStart)
        {
            Assert.Equal(isAnswering, MachineWait.IsResponding(activity));
            Assert.Equal(needsAttention, MachineWait.NeedsAttention(activity));
            Assert.Equal(canPause, MachineWait.CanPause(activity));
            Assert.Equal(canResume, MachineWait.CanResume(activity));
            Assert.Equal(isUnavailable, MachineWait.IsUnavailable(activity));

            // A door must not block a start: the run prompts about it. Folding this back
            // into NeedsAttention greys out Mill and Probe at an open enclosure.
            Assert.Equal(blocksJobStart, MachineWait.BlocksJobStart(activity));
        }

        [Fact]
        public void EveryActivity_IsCoveredByTheControlAnswers()
        {
            var decided = typeof(MachineWaitTests)
                .GetMethod(nameof(EveryActivity_HasControlAnswers))!
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

        // =========================================================================
        // Waits must not sit on a state only a person can clear
        //
        // A door opened mid-cycle leaves GRBL holding. It will not reach Idle, will not
        // move Z, and will not start a move until the operator acts. A wait that only
        // watches the clock sits out the full timeout and then reports an error that does
        // not mention the door.
        // =========================================================================

        [Fact]
        public async Task WaitingForIdleGivesUpAsSoonAsTheDoorHolds()
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
        public async Task WaitForIdle_GivesUpWhenTheMachineAlarms()
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
        public async Task WaitingForAZHeightGivesUpAsSoonAsTheDoorHolds()
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
        /// up as soon as it parks. Without this the run sat out the full
        /// ProbeReplyTimeoutMs: three minutes with the tool down and nothing on screen.
        /// </summary>
        [Fact]
        public async Task ReplyWait_GivesUpWhenTheMachineStopsResponding()
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
        /// Every wait in MachineWait runs through WaitUntilAsync, and the condition can be
        /// true at the moment the budget runs out. Reported as a timeout, a retract that did
        /// land reads as one that did not.
        ///
        /// A zero budget skips the loop body, so only the re-check after it is under test.
        /// </summary>
        [Fact]
        public async Task AConditionAlreadyTrueWhenTheBudgetIsGone_IsNotATimeout()
        {
            using var machine = new MockMachine { Status = GrblProtocol.StatusIdle, Connected = true };

            Assert.True(
                await MachineWait.WaitForIdleAsync(machine, 0),
                "a machine already Idle when the budget ran out was reported as a timeout");
        }

        /// <summary>The other half: still false when the budget is gone is a timeout.</summary>
        [Fact]
        public async Task AConditionStillFalseWhenTheBudgetIsGone_IsATimeout()
        {
            using var machine = new MockMachine { Status = GrblProtocol.StatusRun, Connected = true };

            Assert.False(await MachineWait.WaitForIdleAsync(machine, 0));
        }
    }
}
