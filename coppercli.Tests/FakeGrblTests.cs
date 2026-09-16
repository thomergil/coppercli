using System;
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
        private const double TestMoveZ = -5.0;
        private const int PositionDecimals = 3;
        private const string Loopback = "127.0.0.1";

        [Fact]
        public void TheRealMachineConnectsAndReadsStatus()
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

                machine.SendLine(GCodeFormat.Inv($"{GrblProtocol.CmdRapidMove} Z{TestMoveZ:F3}"));
                WebServerFixture.WaitUntil(
                    () => Math.Abs(machine.MachinePosition.Z - TestMoveZ) < Constants.PositionToleranceMm,
                    "the reported position to follow the move");

                Assert.Equal(TestMoveZ, machine.MachinePosition.Z, PositionDecimals);
            }
            finally
            {
                machine.Disconnect();
            }
        }
    }
}
