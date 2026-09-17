#nullable enable
using System;
using coppercli.Core.Communication;
using coppercli.Core.Controllers;
using coppercli.Core.Util;

namespace coppercli.Tests.Fakes
{
    /// <summary>
    /// The move the door tests send, and the wait for it. Two door test files run it against
    /// different doubles, so the target and the tolerance are defined here once.
    /// </summary>
    public static class DoorTestMove
    {
        /// <summary>Where the retract ends up, far enough from zero to tell a move that ran
        /// from one that did not.</summary>
        public const double TargetZ = -5.0;

        /// <summary>Decimals a position is compared to, matching what GRBL reports.</summary>
        public const int PositionDecimals = 3;

        private static string TargetZText =>
            TargetZ.ToString("F" + PositionDecimals, System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// Long enough that the move would have finished had it run. A move that never happens
        /// raises no event, so the test waits this out rather than waiting on one.
        /// </summary>
        public const int SettleMs = 500;

        public static string Line() =>
            GCodeFormat.Inv($"{GrblProtocol.CmdRapidMove} Z{TargetZText}");

        public static void WaitUntilItLands(IMachine machine) =>
            WebServerFixture.WaitUntil(
                () => Math.Abs(machine.MachinePosition.Z - TargetZ) < Constants.PositionToleranceMm,
                "the queued move to run on the resume",
                ControllerConstants.DoorResumeTimeoutMs);
    }
}
