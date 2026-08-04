using System;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using coppercli.Core.Controllers;
using coppercli.Core.Util;
using coppercli.Tests.Fakes;
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
            var machine = new MockMachine { Status = "Alarm:1" };
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
            var machine = new MockMachine { Status = "Hold:0" };
            Assert.True(MachineWait.IsHold(machine));
        }

        [Fact]
        public void IsDoor_WhenDoor_ReturnsTrue()
        {
            var machine = new MockMachine { Status = "Door:0" };
            Assert.True(MachineWait.IsDoor(machine));
        }

        [Fact]
        public void IsProblematic_WhenAlarm_ReturnsTrue()
        {
            var machine = new MockMachine { Status = "Alarm:2" };
            Assert.True(MachineWait.IsProblematic(machine));
        }

        [Fact]
        public void IsProblematic_WhenDoor_ReturnsTrue()
        {
            var machine = new MockMachine { Status = "Door:1" };
            Assert.True(MachineWait.IsProblematic(machine));
        }

        [Fact]
        public void IsProblematic_WhenIdle_ReturnsFalse()
        {
            var machine = new MockMachine { Status = "Idle" };
            Assert.False(MachineWait.IsProblematic(machine));
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

        // =========================================================================
        // WaitForMachineZHeightAsync tests
        // =========================================================================

        [Fact]
        public async Task WaitForMachineZHeightAsync_UsesMachinePosition()
        {
            var machine = new MockMachine
            {
                MachinePosition = new Vector3(0, 0, -1.0),
                WorkPosition = new Vector3(0, 0, 10.0) // Different from machine
            };

            var result = await MachineWait.WaitForMachineZHeightAsync(machine, -1.0, 1000);

            Assert.True(result);
        }

        // =========================================================================
        // ClearDoorStateAsync tests
        // =========================================================================

        [Fact]
        public async Task ClearDoorStateAsync_WhenDoor_SendsCycleStart()
        {
            var machine = new MockMachine { Status = "Door:0" };

            var result = await MachineWait.ClearDoorStateAsync(machine);

            Assert.True(result);
            Assert.Equal(1, machine.CycleStartCount);
        }

        [Fact]
        public async Task ClearDoorStateAsync_WhenNotDoor_DoesNothing()
        {
            var machine = new MockMachine { Status = "Idle" };

            var result = await MachineWait.ClearDoorStateAsync(machine);

            Assert.False(result);
            Assert.Equal(0, machine.CycleStartCount);
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
            var machine = new MockMachine { Status = "Alarm:1" };

            var result = await MachineWait.EnsureMachineReadyAsync(machine, 200);

            Assert.False(result);
        }

        /// <summary>
        /// Readiness must never resume the machine on its own. Sending CycleStart to
        /// clear a Door restarts the spindle and resumes motion because the software
        /// decided to, while the enclosure is open and the operator may be reaching in.
        /// An open door blocks the start instead.
        /// </summary>
        [Fact]
        public async Task EnsureMachineReadyAsync_DoesNotResumeAnOpenDoor()
        {
            var machine = new MockMachine { Status = "Door:0" };

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

            var startTime = DateTime.Now;
            await MachineWait.SafetyRetractZAsync(machine, -1.0, 5000);
            var elapsed = DateTime.Now - startTime;

            // Should return quickly (less than 1 second)
            Assert.True(elapsed.TotalSeconds < 1);
        }
        // =========================================================================
        // Door substates
        //
        // GRBL stays in Door after the operator closes the enclosure, waiting to be
        // resumed. Only the substate separates "still open" from "closed, waiting on
        // you", and every UI derives its wording from these two predicates - so a
        // machinist who has shut the door is told so rather than left reading "Door".
        // =========================================================================

        [Theory]
        [InlineData(GrblProtocol.DoorSubStateAjar)]
        [InlineData(GrblProtocol.DoorSubStateOpening)]
        public void ADoorReportedAjarReadsAsOpen(string subState)
        {
            var machine = new MockMachine
            {
                Status = GrblProtocol.StatusDoor,
                StatusSubState = subState
            };

            Assert.True(MachineWait.IsDoorOpen(machine));
            Assert.False(MachineWait.IsDoorAwaitingResume(machine));
        }

        [Theory]
        [InlineData(GrblProtocol.DoorSubStateClosed)]
        [InlineData(GrblProtocol.DoorSubStateResuming)]
        public void ADoorReportedShutReadsAsWaitingToResume(string subState)
        {
            var machine = new MockMachine
            {
                Status = GrblProtocol.StatusDoor,
                StatusSubState = subState
            };

            Assert.False(MachineWait.IsDoorOpen(machine));
            Assert.True(MachineWait.IsDoorAwaitingResume(machine));
        }

        [Fact]
        public void ADoorWithNoSubStateIsTreatedAsOpen()
        {
            var machine = new MockMachine
            {
                Status = GrblProtocol.StatusDoor,
                StatusSubState = string.Empty
            };

            // Fail safe: being told to close a door that is already shut costs a moment.
            Assert.True(MachineWait.IsDoorOpen(machine));
        }

        [Fact]
        public void AMachineThatIsNotAtTheDoorIsNeitherOpenNorWaiting()
        {
            var machine = new MockMachine { Status = GrblProtocol.StatusIdle };

            Assert.False(MachineWait.IsDoorOpen(machine));
            Assert.False(MachineWait.IsDoorAwaitingResume(machine));
        }
        // =========================================================================
        // Waits must not sit on a state only a person can clear
        //
        // A door opened mid-cycle leaves GRBL holding. It will not reach Idle, will not
        // move Z, and will not start a move until the operator acts - so a wait that
        // only watches the clock leaves them in front of a frozen screen for the whole
        // timeout and then reports something that never mentions the door.
        // =========================================================================

        [Fact]
        public async Task WaitingForIdleGivesUpAsSoonAsTheDoorHolds()
        {
            var machine = new MockMachine
            {
                Status = GrblProtocol.StatusDoor,
                StatusSubState = GrblProtocol.DoorSubStateClosed
            };

            var elapsed = Stopwatch.StartNew();
            bool idle = await MachineWait.WaitForIdleAsync(machine, HangDetectTimeoutMs);
            elapsed.Stop();

            Assert.False(idle);
            Assert.True(elapsed.ElapsedMilliseconds < HangDetectTimeoutMs / 2,
                $"waited {elapsed.ElapsedMilliseconds}ms on a door hold that can never clear itself");
        }

        [Fact]
        public async Task WaitingForAStableIdleGivesUpAsSoonAsTheMachineAlarms()
        {
            var machine = new MockMachine { Status = GrblProtocol.StatusAlarm };

            var elapsed = Stopwatch.StartNew();
            bool idle = await MachineWait.WaitForStableIdleAsync(machine, HangDetectTimeoutMs);
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
    }
}
