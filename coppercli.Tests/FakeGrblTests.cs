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
    /// FakeGrbl is driven here through the real Machine over a loopback socket. Where the
    /// simulator diverges from GRBL, every test built on it passes on behavior the machine
    /// does not have.
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

                // Status arrives on the worker thread, so Connect returns before the first
                // report.
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
        /// the cycle start. A double that moved anyway would pass a retract queued at the
        /// door, which on the real machine runs only once the hold lifts.
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
        /// sends that unlock; a double that never alarms would not show whether it does.
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
