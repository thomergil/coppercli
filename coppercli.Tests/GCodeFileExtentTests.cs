using System;
using coppercli.Core.GCode;
using Xunit;

namespace coppercli.Tests
{
    // A file's bounds and its time estimate cover the whole path of every move, arcs included.
    // The bounds lay out the mill picture and the board's sections; the time feeds the ETA.
    public class GCodeFileExtentTests
    {
        private const double Tolerance = 1e-9;
        private const double CircleCenterX = 10;
        private const double CircleCenterY = 5;
        private const double CircleRadius = 2;
        private const double ArcFeed = 100;
        private const double TravelZ = 1;
        private const double CutZ = -0.1;
        private const int MinutesDecimals = 6;

        /// <summary>A plunge from TravelZ to CutZ, then a full circle from its leftmost point, both at ArcFeed.</summary>
        private static readonly string[] FullCircle =
        {
            "G21", "G90",
            FormattableString.Invariant($"G0 X{CircleCenterX - CircleRadius} Y{CircleCenterY} Z{TravelZ}"),
            FormattableString.Invariant($"G1 Z{CutZ} F{ArcFeed}"),
            FormattableString.Invariant($"G2 X{CircleCenterX - CircleRadius} Y{CircleCenterY} I{CircleRadius} J0"),
            FormattableString.Invariant($"G0 Z{TravelZ}")
        };

        // Bounds taken from the ends of each move put a full circle's whole right side, and
        // its top and bottom, outside the board, so the sections left most of it in the edge
        // sections.
        [Fact]
        public void TheBounds_ReachTheFarSidesOfAnArc()
        {
            var file = GCodeFile.FromList(FullCircle);

            var (min, max) = file.CuttingBounds;

            Assert.Equal(CircleCenterX - CircleRadius, min.X, Tolerance);
            Assert.Equal(CircleCenterX + CircleRadius, max.X, Tolerance);
            Assert.Equal(CircleCenterY - CircleRadius, min.Y, Tolerance);
            Assert.Equal(CircleCenterY + CircleRadius, max.Y, Tolerance);
            Assert.Equal(CircleCenterX + CircleRadius, file.Max.X, Tolerance);
        }

        // A time estimate that counted straight moves only left out every arc, so a job of
        // milled holes ran well past the time it showed.
        [Fact]
        public void TheTimeEstimate_CountsArcs()
        {
            var file = GCodeFile.FromList(FullCircle);

            double circleMinutes = 2 * Math.PI * CircleRadius / ArcFeed;
            double plungeMinutes = (TravelZ - CutZ) / ArcFeed;

            Assert.Equal(circleMinutes + plungeMinutes, file.TotalTime.TotalMinutes, MinutesDecimals);
        }

        private const double HelixEndZ = -0.5;

        /// <summary>The same circle cut as a helix, from CutZ down to HelixEndZ, as a milled hole is.</summary>
        private static readonly string[] Helix =
        {
            "G21", "G90",
            FormattableString.Invariant($"G0 X{CircleCenterX - CircleRadius} Y{CircleCenterY} Z{TravelZ}"),
            FormattableString.Invariant($"G1 Z{CutZ} F{ArcFeed}"),
            FormattableString.Invariant($"G2 X{CircleCenterX - CircleRadius} Y{CircleCenterY} Z{HelixEndZ} I{CircleRadius} J0"),
            FormattableString.Invariant($"G0 Z{TravelZ}")
        };

        /// <summary>
        /// Catches a helix measured around the circle only, which leaves its descent out of the
        /// time estimate and out of the distance the tool travels.
        /// </summary>
        [Fact]
        public void TheTimeEstimate_CountsTheDescentOfAHelix()
        {
            var file = GCodeFile.FromList(Helix);

            double around = 2 * Math.PI * CircleRadius;
            double helixMinutes = Math.Sqrt(around * around + (CutZ - HelixEndZ) * (CutZ - HelixEndZ)) / ArcFeed;
            double plungeMinutes = (TravelZ - CutZ) / ArcFeed;

            Assert.Equal(helixMinutes + plungeMinutes, file.TotalTime.TotalMinutes, MinutesDecimals);
        }
    }
}
