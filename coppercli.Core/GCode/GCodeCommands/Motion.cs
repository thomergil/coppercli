using coppercli.Core.Util;
using System.Collections.Generic;

namespace coppercli.Core.GCode.GCodeCommands
{
    public abstract class Motion : Command
    {
        public Vector3 Start;
        public Vector3 End;
        public double Feed;

        /// <summary>
        /// False when the machine had moved somewhere the parser could not model (a G53
        /// or G92 block) before this motion, so Start is only a guess. A move with an
        /// untrusted start must never be discarded for "going nowhere" - the file is
        /// recovering to a known height and that recovery is the point.
        /// </summary>
        public bool StartTrusted = true;

        public Vector3 Delta
        {
            get { return End - Start; }
        }

        public abstract double Length { get; }

        /// <param name="ratio">0 at <see cref="Start"/>, 1 at <see cref="End"/>.</param>
        public abstract Vector3 Interpolate(double ratio);

        /// <summary>Splits the motion into fragments that still follow the same path.</summary>
        /// <param name="length">The longest any returned segment may be.</param>
        public abstract IEnumerable<Motion> Split(double length);
    }
}
