using coppercli.Core.Util;
using System;
using System.Collections.Generic;
using System.Linq;

namespace coppercli.Core.GCode.GCodeCommands
{
    public enum ArcPlane
    {
        XY = 0,
        YZ = 1,
        ZX = 2
    }

    public enum ArcDirection
    {
        CW,
        CCW
    }

    public class Arc : Motion
    {
        private const double FullTurn = 2 * Math.PI;

        public ArcPlane Plane;
        public ArcDirection Direction;
        public double U;    // absolute position of center in first axis of plane
        public double V;    // absolute position of center in second axis of plane

        /// <summary>The length of the path: a helix when the arc also moves along the plane's axis.</summary>
        public override double Length
        {
            get
            {
                double aroundTheCircle = AngleSpan * Radius;
                double alongTheAxis = Delta.RollComponents(-(int)Plane).Z;
                return Math.Sqrt(aroundTheCircle * aroundTheCircle + alongTheAxis * alongTheAxis);
            }
        }

        public double StartAngle
        {
            get
            {
                Vector3 StartInPlane = Start.RollComponents(-(int)Plane);
                double X = StartInPlane.X - U;
                double Y = StartInPlane.Y - V;
                return Math.Atan2(Y, X);
            }
        }

        public double EndAngle
        {
            get
            {
                Vector3 EndInPlane = End.RollComponents(-(int)Plane);
                double X = EndInPlane.X - U;
                double Y = EndInPlane.Y - V;
                return Math.Atan2(Y, X);
            }
        }

        public double AngleSpan
        {
            get
            {
                double span = EndAngle - StartAngle;

                if (Direction == ArcDirection.CW)
                {
                    if (span >= 0)
                    {
                        span -= FullTurn;
                    }
                }
                else
                {
                    if (span <= 0)
                    {
                        span += FullTurn;
                    }
                }

                return span;
            }
        }

        public double Radius
        {
            get
            {
                Vector3 startplane = Start.RollComponents(-(int)Plane);
                Vector3 endplane = End.RollComponents(-(int)Plane);

                return (
                    Math.Sqrt(Math.Pow(startplane.X - U, 2) + Math.Pow(startplane.Y - V, 2)) +
                    Math.Sqrt(Math.Pow(endplane.X - U, 2) + Math.Pow(endplane.Y - V, 2))
                ) / 2;
            }
        }

        /// <inheritdoc/>
        public override bool MovesAcrossTheBoard => true;

        /// <inheritdoc/>
        /// <remarks>
        /// An arc outside the XY plane gives its ends only, because its turning points in X and
        /// Y are not at these angles.
        /// </remarks>
        public override IEnumerable<Vector3> ExtremePoints =>
            Plane != ArcPlane.XY
                ? base.ExtremePoints
                : base.ExtremePoints.Concat(
                    RatiosAtAngles(0, Math.PI / 2, Math.PI, -Math.PI / 2).Select(Interpolate));

        protected override Vector3 PointAlongPath(double ratio)
        {
            double angle = StartAngle + AngleSpan * ratio;

            Vector3 onPlane = new Vector3(U + (Radius * Math.Cos(angle)), V + (Radius * Math.Sin(angle)), 0);

            double helix = (Start + (ratio * Delta)).RollComponents(-(int)Plane).Z;

            onPlane.Z = helix;

            Vector3 interpolation = onPlane.RollComponents((int)Plane);

            return interpolation;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The point at the same angle around the center is tried, and both ends: a point past a
        /// partial arc is nearest an end, and the start and end of a helical full circle lie at
        /// the same angle, told apart by height. Called for each status report, so it allocates
        /// nothing.
        /// </remarks>
        public override (double Distance, double Ratio) Nearest(Vector3 point)
        {
            Vector3 inPlane = point.RollComponents(-(int)Plane);
            double atTheAngle = RatioAtAngle(Math.Atan2(inPlane.Y - V, inPlane.X - U));

            var nearest = Nearer(DistanceAt(point, 0), DistanceAt(point, 1));
            return atTheAngle < 1 ? Nearer(nearest, DistanceAt(point, atTheAngle)) : nearest;
        }

        private (double Distance, double Ratio) DistanceAt(Vector3 point, double ratio) =>
            ((point - Interpolate(ratio)).Magnitude, ratio);

        private static (double Distance, double Ratio) Nearer((double Distance, double Ratio) a, (double Distance, double Ratio) b) =>
            b.Distance < a.Distance ? b : a;

        /// <inheritdoc/>
        /// <remarks>For an arc in the XY plane only.</remarks>
        public override IEnumerable<double> RatiosWhereXIs(double x)
        {
            ThrowUnlessInTheXYPlane();
            double angle = Math.Acos((x - U) / Radius);
            return RatiosAtAngles(angle, -angle);
        }

        /// <inheritdoc/>
        /// <remarks>For an arc in the XY plane only.</remarks>
        public override IEnumerable<double> RatiosWhereYIs(double y)
        {
            ThrowUnlessInTheXYPlane();
            double angle = Math.Asin((y - V) / Radius);
            return RatiosAtAngles(angle, Math.PI - angle);
        }

        /// <summary>
        /// The ratios, strictly between 0 and 1, at which the arc passes the given angles. An
        /// angle the arc never reaches, or NaN for a line the circle does not meet, gives none.
        /// </summary>
        private IEnumerable<double> RatiosAtAngles(params double[] angles)
        {
            foreach (double angle in angles)
            {
                double ratio = RatioAtAngle(angle);
                if (ratio > 0 && ratio < 1)
                {
                    yield return ratio;
                }
            }
        }

        /// <summary>
        /// How far the arc turns from its start to reach <paramref name="angle"/>, in its own
        /// direction and within one turn, as a share of its span: past 1 for an angle it never
        /// reaches, and NaN for NaN.
        /// </summary>
        private double RatioAtAngle(double angle)
        {
            double span = AngleSpan;
            double turned = span > 0 ? angle - StartAngle : StartAngle - angle;
            turned -= FullTurn * Math.Floor(turned / FullTurn);
            return turned / Math.Abs(span);
        }

        private void ThrowUnlessInTheXYPlane()
        {
            if (Plane != ArcPlane.XY)
            {
                throw new InvalidOperationException(Constants.ErrorArcsOutsideXYPlane);
            }
        }
    }
}
