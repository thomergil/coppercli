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
    /// FakeMachine and MockMachine must hold at the door the way GRBL does: nothing executes
    /// while the hold is on, so lines arrive, queue, and wait for the cycle start. A double
    /// that moved instead would hide the defects the door tests cover.
    /// </summary>
    public class FakeMachineDoorTests
    {
        private const double FastSpeed = 10000.0;

        /// <summary>Longer than the test takes, so the restore is still running when it is read.</summary>
        private const int StillRestoringMs = 5000;

        /// <summary>
        /// The second half is what makes the first mean anything: a fake that dropped the line
        /// entirely would also leave the tool where it was.
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
            // would hide a caller that resumes at an open door.
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
        /// retracted axes are back. A double that jumped straight to Idle would hide a caller
        /// that does not wait for it.
        /// </summary>
        [Fact]
        public void TheRestore_IsReportedUntilTheAxesAreBack()
        {
            using var machine = FastMachine();
            machine.DoorRestoreMs = StillRestoringMs;
            machine.SimulateDoorClosedAndHolding();

            machine.CycleStart();

            Assert.Equal(DoorState.Resuming, MachineWait.GetDoorState(machine));
        }

        private static FakeMachine FastMachine() =>
            new() { RapidSpeed = FastSpeed, FeedSpeed = FastSpeed };
    }
}
