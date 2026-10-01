using coppercli.Core.Util;
using System;
using System.Collections.Generic;
using System.Linq;

namespace coppercli.Core.GCode.GCodeCommands
{
    public class Line : Motion
    {
        public bool Rapid;
        // PositionValid[i] is true if the corresponding coordinate of the end position was defined in the file.
        // eg. for a file with "G0 Z15" as the first line, X and Y would still be false
        public bool[] PositionValid = new bool[] { false, false, false };
        public bool StartValid = false;

        /// <summary>Every axis of the end is known from the file.</summary>
        public bool EndKnown => PositionValid.All(isValid => isValid);

        /// <inheritdoc/>
        public override Vector3? KnownEnd => EndKnown ? End : null;

        /// <summary>The end's Z is known from the file, whether or not X and Y are.</summary>
        public bool ZKnown => PositionValid[2];

        /// <inheritdoc/>
        public override bool FullyKnown => StartValid && EndKnown;

        /// <inheritdoc/>
        public override Motion Copy()
        {
            var copy = (Line)base.Copy();
            copy.PositionValid = (bool[])PositionValid.Clone();
            return copy;
        }

        public override double Length
        {
            get
            {
                if (!FullyKnown)
                {
                    return 0;
                }
                return Delta.Magnitude;
            }
        }

        protected override Vector3 PointAlongPath(double ratio)
        {
            return Start + Delta * ratio;
        }

        /// <inheritdoc/>
        public override (double Distance, double Ratio) Nearest(Vector3 point)
        {
            if (!FullyKnown)
            {
                return KnownEnd is Vector3 end ? ((point - end).Magnitude, 1) : (double.PositiveInfinity, 0);
            }

            double lengthSquared = Vector3.Dot(Delta, Delta);
            double ratio = lengthSquared == 0 ? 0 : Math.Clamp(Vector3.Dot(point - Start, Delta) / lengthSquared, 0, 1);
            return ((point - Interpolate(ratio)).Magnitude, ratio);
        }

        public override IEnumerable<double> RatiosWhereXIs(double x) => RatiosWhere(Start.X, End.X, x);

        public override IEnumerable<double> RatiosWhereYIs(double y) => RatiosWhere(Start.Y, End.Y, y);

        private static IEnumerable<double> RatiosWhere(double from, double to, double value)
        {
            if (from == to)
            {
                yield break;
            }

            double ratio = (value - from) / (to - from);
            if (ratio > 0 && ratio < 1)
            {
                yield return ratio;
            }
        }
    }
}
