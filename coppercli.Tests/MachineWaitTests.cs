using System;
using System.Threading;
using System.Threading.Tasks;
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

        [Fact]
        public async Task EnsureMachineReadyAsync_ClearsDoorFirst()
        {
            var machine = new MockMachine { Status = "Door:0" };

            // Simulate door clearing leads to Idle
            var task = Task.Run(async () =>
            {
                await Task.Delay(100);
                machine.SimulateStatusChange("Idle");
            });

            var result = await MachineWait.EnsureMachineReadyAsync(machine, 2000);

            Assert.Equal(1, machine.CycleStartCount);
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
    }
}
