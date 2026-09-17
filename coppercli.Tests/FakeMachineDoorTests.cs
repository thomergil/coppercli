using System;
using System.Linq;
using System.Threading.Tasks;
using coppercli.Core.Controllers;
using coppercli.Core.Util;
using coppercli.Tests.Fakes;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// The doubles must hold at the door the way GRBL does. One that moved instead would hide
    /// the defects the door tests exist to catch: GRBL executes nothing while it holds at the
    /// door, so lines arrive, queue, and wait for the cycle start.
    /// </summary>
    public class FakeMachineDoorTests
    {
        private const double FastSpeed = 10000.0;

        /// <summary>A park restore long enough to still be running when the test reads it.</summary>
        private const int StillRestoringMs = 5000;

        /// <summary>
        /// Both halves in one test, because the second is what makes the first mean anything:
        /// a fake that dropped the line entirely would also leave the tool where it was.
        /// </summary>
        [Fact]
        public async Task AQueuedMove_WaitsForTheHoldAndThenRuns()
        {
            using var machine = FastMachine();
            machine.SimulateDoorClosedAndHolding();

            machine.SendLine(DoorTestMove.Line());
            await Task.Delay(DoorTestMove.SettleMs);

            Assert.True(MachineWait.IsDoor(machine), "a door hold must outlast a queued move");
            Assert.Equal(0, machine.MachinePosition.Z);

            Assert.Equal(DoorState.None,
                await MachineWait.ReleaseDoorHoldAsync(machine, ControllerConstants.DoorResumeTimeoutMs));

            DoorTestMove.WaitUntilItLands(machine);

            Assert.Equal(DoorTestMove.TargetZ, machine.MachinePosition.Z, DoorTestMove.PositionDecimals);
        }

        [Fact]
        public void ACycleStart_AtAnOpenDoor_DoesNothing()
        {
            // GRBL ignores a resume while the switch reads open. A fake that resumed anyway
            // would let a real machine with a loose door switch pass unnoticed.
            using var machine = FastMachine();
            machine.SimulateDoorOpen();

            machine.CycleStart();

            Assert.Equal(DoorState.Open, MachineWait.GetDoorState(machine));
        }

        [Fact]
        public void ACycleStart_AtAnOpenDoor_DoesNothingOnTheMockEither()
        {
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateAjar);

            machine.CycleStart();

            Assert.Equal(DoorState.Open, MachineWait.GetDoorState(machine));
        }

        /// <summary>
        /// The restore after a cycle start is a real move, reported as Door:3 until the
        /// retracted axes are back. A double that jumped to Idle would let a caller that does
        /// not wait for the restore pass.
        /// </summary>
        [Fact]
        public void TheRestore_IsReportedUntilTheAxesAreBack()
        {
            using var machine = FastMachine();
            // Longer than this test takes, so the restore is still running when it reads the
            // state - which is the case it is about.
            machine.DoorRestoreMs = StillRestoringMs;
            machine.SimulateDoorClosedAndHolding();

            machine.CycleStart();

            Assert.Equal(DoorState.Resuming, MachineWait.GetDoorState(machine));
        }

        private static FakeMachine FastMachine() =>
            new() { RapidSpeed = FastSpeed, FeedSpeed = FastSpeed };
    }
}
