using System;
using System.Threading.Tasks;
using coppercli.Core.Controllers;
using System.Threading;
using coppercli.Core.Communication;
using coppercli.Core.Settings;
using coppercli.Core.Util;
using coppercli.Tests.Fakes;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// The simulator has to be convincing enough for the real Machine, or every test built
    /// on it is testing the simulator instead of coppercli.
    /// </summary>
    public class FakeGrblTests
    {
        private const string Loopback = "127.0.0.1";

        [Fact]
        public void TheRealMachine_ConnectsAndReadsStatus()
        {
            using var grbl = new FakeGrbl();
            var settings = new MachineSettings
            {
                ConnectionType = ConnectionType.Ethernet,
                EthernetIP = Loopback,
                EthernetPort = grbl.Port
            };

            var machine = new Machine(settings);
            try
            {
                machine.Connect();
                Assert.True(machine.Connected);

                // The status poll runs on the worker thread, so wait for the first report.
                WebServerFixture.WaitUntil(
                    () => machine.Status == GrblProtocol.StatusIdle,
                    "the simulated machine to report Idle");

                machine.SendLine(DoorTestMove.Line());
                DoorTestMove.WaitUntilItLands(machine);

                Assert.Equal(DoorTestMove.TargetZ, machine.MachinePosition.Z, DoorTestMove.PositionDecimals);
            }
            finally
            {
                machine.Disconnect();
            }
        }

        /// <summary>
        /// GRBL executes nothing while it holds at the door: lines arrive, queue, and run on
        /// the cycle start. A double that moved anyway would let a retract queued at the door
        /// pass every web test, while the real machine lunges when the hold lifts.
        /// </summary>
        [Fact]
        public void AMoveSentWhileHoldingAtTheDoor_SitsInThePlanner()
        {
            using var grbl = new FakeGrbl();
            var machine = Connected(grbl);
            try
            {
                grbl.SimulateDoorClosedAndHolding();
                WebServerFixture.WaitUntil(() => MachineWait.IsDoor(machine), "the door hold");

                machine.SendLine(DoorTestMove.Line());
                Thread.Sleep(DoorTestMove.SettleMs);

                Assert.Equal(0.0, machine.MachinePosition.Z, DoorTestMove.PositionDecimals);

                machine.CycleStart();
                DoorTestMove.WaitUntilItLands(machine);
            }
            finally
            {
                machine.Disconnect();
            }
        }

        /// <summary>
        /// GRBL alarms on a soft reset out of a door hold, and $X clears it. StopAndResetAsync
        /// sends that unlock; against a double that never alarms, nothing proves it does.
        /// </summary>
        [Fact]
        public async Task ASoftResetOutOfADoorHold_AlarmsAndIsClearedByUnlock()
        {
            using var grbl = new FakeGrbl();
            var machine = Connected(grbl);
            try
            {
                grbl.SimulateDoorClosedAndHolding();
                WebServerFixture.WaitUntil(() => MachineWait.IsDoor(machine), "the door hold");

                Assert.True(
                    await MachineWait.StopAndResetAsync(machine), "the stop did not see the door");

                WebServerFixture.WaitUntil(
                    () => MachineWait.IsIdle(machine), "the unlock to clear the alarm the reset raised");
                Assert.Contains(GrblProtocol.CmdUnlock, grbl.Received);
            }
            finally
            {
                machine.Disconnect();
            }
        }

        /// <summary>A real Machine talking to the simulator over the loopback port.</summary>
        private static Machine Connected(FakeGrbl grbl)
        {
            var machine = new Machine(new MachineSettings
            {
                ConnectionType = ConnectionType.Ethernet,
                EthernetIP = Loopback,
                EthernetPort = grbl.Port
            });

            machine.Connect();
            WebServerFixture.WaitUntil(
                () => machine.Status == GrblProtocol.StatusIdle,
                "the simulated machine to report Idle");
            return machine;
        }
    }
}
