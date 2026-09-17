#nullable enable
using System;
using coppercli.Core.Communication;
using coppercli.Core.Controllers;
using coppercli.Core.Util;

namespace coppercli.Tests.Fakes
{
    /// <summary>
    /// The one move the door tests send, and how they wait for it. Both door test files ask
    /// the same question of a different double, so a change to the move or the tolerance has
    /// to reach both.
    /// </summary>
    public static class DoorTestMove
    {
        /// <summary>Where the retract ends up, far enough from zero to be unmistakable.</summary>
        public const double TargetZ = -5.0;

        /// <summary>Decimals a position is compared to, matching what GRBL reports.</summary>
        public const int PositionDecimals = 3;

        /// <summary>The retract's Z, formatted the way the line below sends it.</summary>
        private static string TargetZText =>
            TargetZ.ToString("F" + PositionDecimals, System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// Long enough that the move would be over had it run. A move that did not happen
        /// cannot raise an event, so the test waits out the chance of one.
        /// </summary>
        public const int SettleMs = 500;

        /// <summary>The retract itself.</summary>
        public static string Line() =>
            GCodeFormat.Inv($"{GrblProtocol.CmdRapidMove} Z{TargetZText}");

        /// <summary>Waits until the machine reports the retract has run.</summary>
        public static void WaitUntilItLands(IMachine machine) =>
            WebServerFixture.WaitUntil(
                () => Math.Abs(machine.MachinePosition.Z - TargetZ) < Constants.PositionToleranceMm,
                "the queued move to run on the resume",
                ControllerConstants.DoorResumeTimeoutMs);
    }
}
