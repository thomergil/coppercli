using coppercli.Core.Util;
using System;
using System.Collections.Generic;

namespace coppercli.Core.GCode.GCodeCommands
{
    public abstract class Motion : Command
    {
        public Vector3 Start;
        public Vector3 End;
        public double Feed;

        public Vector3 Delta
        {
            get { return End - Start; }
        }

        public abstract double Length { get; }

        /// <summary>Where the move leaves the tool, or null where the file has not said.</summary>
        public virtual Vector3? KnownEnd => End;

        /// <summary>How long the move takes at its feed: zero for a rapid, which the machine runs at its own speed.</summary>
        public TimeSpan FeedTime =>
            this is Line { Rapid: true } || Feed <= 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(Length / Feed);

        /// <summary>
        /// The points of the path that reach furthest in X and Y, which the path's bounds are
        /// taken from: its ends, and the points where an arc turns back.
        /// </summary>
        public virtual IEnumerable<Vector3> ExtremePoints => new[] { Start, End };

        /// <summary>True when both ends are known, so the move has a path.</summary>
        public virtual bool FullyKnown => true;

        /// <summary>
        /// True when the move takes material off: it reaches below work zero, the copper surface
        /// in the file's frame, while moving across the board or down into it; any other move is
        /// travel. This describes the file; during a run, coppercli compares the tool's Z with
        /// MillCuttingDepthThreshold to tell whether the tool is near the board.
        /// </summary>
        public bool IsCut => Math.Min(Start.Z, End.Z) < 0 && (MovesAcrossTheBoard || End.Z < Start.Z);

        /// <summary>True when the path moves in X or Y anywhere along it.</summary>
        public virtual bool MovesAcrossTheBoard => Start.X != End.X || Start.Y != End.Y;

        /// <summary>
        /// The point on the path at <paramref name="ratio"/>: exactly <see cref="Start"/> at 0
        /// and exactly <see cref="End"/> at 1.
        /// </summary>
        public Vector3 Interpolate(double ratio) =>
            ratio <= 0 ? Start : ratio >= 1 ? End : PointAlongPath(ratio);

        /// <summary>The point at <paramref name="ratio"/>, strictly between 0 and 1.</summary>
        protected abstract Vector3 PointAlongPath(double ratio);

        /// <summary>The ratios strictly between 0 and 1 at which the path crosses X = <paramref name="x"/>.</summary>
        public abstract IEnumerable<double> RatiosWhereXIs(double x);

        /// <summary>The ratios strictly between 0 and 1 at which the path crosses Y = <paramref name="y"/>.</summary>
        public abstract IEnumerable<double> RatiosWhereYIs(double y);

        /// <summary>
        /// The part of the path between two ratios, as a move of the same kind. A slice from 0 to
        /// 1 has this move's own ends.
        /// </summary>
        public Motion Slice(double fromRatio, double toRatio)
        {
            if (!FullyKnown)
            {
                throw new InvalidOperationException("Slice needs a move whose ends are both known");
            }

            Motion part = Copy();
            part.Start = Interpolate(fromRatio);
            part.End = Interpolate(toRatio);
            return part;
        }

        /// <summary>Splits the motion into segments along the same path.</summary>
        /// <param name="length">The longest any returned segment may be.</param>
        public IEnumerable<Motion> Split(double length)
        {
            int divisions = Math.Max(1, (int)Math.Ceiling(Length / length));

            for (int i = 1; i <= divisions; i++)
            {
                yield return Slice((double)(i - 1) / divisions, (double)i / divisions);
            }
        }

        /// <summary>
        /// A copy to change. A derived file shares the commands it does not change with the
        /// file it came from, so a transform edits a copy, never the command itself.
        /// </summary>
        public virtual Motion Copy() => (Motion)MemberwiseClone();
    }
}
