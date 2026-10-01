#nullable enable
using System;
using coppercli.Core.Communication;
using coppercli.Core.Util;
using static coppercli.Core.Util.GCodeFormat;
using static coppercli.Core.Util.GrblProtocol;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Where a move ends, on the axes it names. An axis left null is neither sent nor checked,
    /// so a Z retract does not depend on where X and Y are. The line sent and the position
    /// waited for both come from here, so they cannot disagree.
    /// </summary>
    /// <param name="InMachineCoordinates">True for a G53 move.</param>
    public readonly record struct MoveTarget(
        double? X = null, double? Y = null, double? Z = null, bool InMachineCoordinates = false)
    {
        /// <summary>A rapid to this target, or a feed move at <paramref name="feed"/> mm/min.</summary>
        public string ToGCode(double? feed = null) => MoveLine(X, Y, Z, InMachineCoordinates, feed);

        /// <summary>
        /// Sends G90 and the move, and returns without waiting: GRBL buffers it behind
        /// whatever is already queued.
        /// </summary>
        public void Send(IMachine machine, double? feed = null)
        {
            machine.SendLine(CmdAbsolute);
            machine.SendLine(ToGCode(feed));
        }

        public bool IsReachedBy(IMachine machine)
        {
            var position = InMachineCoordinates ? machine.MachinePosition : machine.WorkPosition;
            return IsNear(X, position.X) && IsNear(Y, position.Y) && IsNear(Z, position.Z);
        }

        private static bool IsNear(double? target, double actual) =>
            target is not double t || Math.Abs(actual - t) < Constants.PositionToleranceMm;
    }
}
