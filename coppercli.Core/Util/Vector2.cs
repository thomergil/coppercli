using System;

namespace coppercli.Core.Util
{
    public struct Vector2 : IEquatable<Vector2>
    {
        private double x;
        private double y;

        public Vector2(double x, double y)
        {
            this.x = x;
            this.y = y;
        }

        public double X
        {
            get { return x; }
            set { x = value; }
        }

        public double Y
        {
            get { return y; }
            set { y = value; }
        }

        public static Vector2 operator +(Vector2 v1, Vector2 v2)
        {
            return new Vector2(v1.X + v2.X, v1.Y + v2.Y);
        }

        public static Vector2 operator -(Vector2 v1, Vector2 v2)
        {
            return new Vector2(v1.X - v2.X, v1.Y - v2.Y);
        }

        public static Vector2 operator *(Vector2 v1, double s2)
        {
            return new Vector2(v1.X * s2, v1.Y * s2);
        }

        public static Vector2 operator *(double s1, Vector2 v2)
        {
            return v2 * s1;
        }

        public static Vector2 operator /(Vector2 v1, double s2)
        {
            return new Vector2(v1.X / s2, v1.Y / s2);
        }

        public static Vector2 operator -(Vector2 v1)
        {
            return new Vector2(-v1.X, -v1.Y);
        }

        public static bool operator ==(Vector2 v1, Vector2 v2)
        {
            return Math.Abs(v1.X - v2.X) <= EqualityTolerance &&
                   Math.Abs(v1.Y - v2.Y) <= EqualityTolerance;
        }

        public static bool operator !=(Vector2 v1, Vector2 v2)
        {
            return !(v1 == v2);
        }

        public bool Equals(Vector2 other)
        {
            return other == this;
        }

        public override bool Equals(object other)
        {
            if (other is Vector2)
            {
                Vector2 otherVector = (Vector2)other;
                return otherVector == this;
            }
            return false;
        }

        public override int GetHashCode()
        {
            return (int)((X + Y) % Int32.MaxValue);
        }

        public double Magnitude { get { return Math.Sqrt(X * X + Y * Y); } }

        // Tolerance for floating-point equality comparison.
        // Using 1e-9 (one billionth) rather than double.Epsilon (~5e-324) because
        // double.Epsilon is too small for practical comparison - floating-point
        // arithmetic errors routinely exceed it, making equality checks fail.
        public const double EqualityTolerance = 1e-9;
    }
}
