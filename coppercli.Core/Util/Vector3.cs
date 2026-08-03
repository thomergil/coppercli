using System;
using System.Xml.Serialization;

namespace coppercli.Core.Util
{
    [Serializable]
    public struct Vector3 : IComparable, IComparable<Vector3>, IEquatable<Vector3>, IFormattable
    {
        private double x;
        private double y;
        private double z;

        public Vector3(double x, double y, double z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public Vector3(double[] xyz)
        {
            if (xyz.Length != 3)
            {
                throw new ArgumentException(THREE_COMPONENTS);
            }
            x = xyz[0];
            y = xyz[1];
            z = xyz[2];
        }

        public Vector3(Vector3 v1)
        {
            x = v1.X;
            y = v1.Y;
            z = v1.Z;
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

        public double Z
        {
            get { return z; }
            set { z = value; }
        }

        public double Magnitude
        {
            get { return (double)Math.Sqrt(SumComponentSqrs()); }
            set
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException("value", value, NEGATIVE_MAGNITUDE);
                }
                if (this == new Vector3(0, 0, 0))
                {
                    throw new ArgumentException(ORAGIN_VECTOR_MAGNITUDE, "this");
                }
                this = this * (value / Magnitude);
            }
        }

        [XmlIgnore]
        public double[] Array
        {
            get { return new double[] { x, y, z }; }
            set
            {
                if (value.Length == 3)
                {
                    x = value[0];
                    y = value[1];
                    z = value[2];
                }
                else
                {
                    throw new ArgumentException(THREE_COMPONENTS);
                }
            }
        }

        public double this[int index]
        {
            get
            {
                switch (index)
                {
                    case 0: return X;
                    case 1: return Y;
                    case 2: return Z;
                    default: throw new ArgumentException(THREE_COMPONENTS, "index");
                }
            }
            set
            {
                switch (index)
                {
                    case 0: X = value; break;
                    case 1: Y = value; break;
                    case 2: Z = value; break;
                    default: throw new ArgumentException(THREE_COMPONENTS, "index");
                }
            }
        }

        public static Vector3 operator +(Vector3 v1, Vector3 v2)
        {
            return new Vector3(v1.X + v2.X, v1.Y + v2.Y, v1.Z + v2.Z);
        }

        public static Vector3 operator -(Vector3 v1, Vector3 v2)
        {
            return new Vector3(v1.X - v2.X, v1.Y - v2.Y, v1.Z - v2.Z);
        }

        public static Vector3 operator *(Vector3 v1, double s2)
        {
            return new Vector3(v1.X * s2, v1.Y * s2, v1.Z * s2);
        }

        public static Vector3 operator *(double s1, Vector3 v2)
        {
            return v2 * s1;
        }

        public static Vector3 operator /(Vector3 v1, double s2)
        {
            return new Vector3(v1.X / s2, v1.Y / s2, v1.Z / s2);
        }

        public static Vector3 operator -(Vector3 v1)
        {
            return new Vector3(-v1.X, -v1.Y, -v1.Z);
        }

        public static Vector3 operator +(Vector3 v1)
        {
            return new Vector3(+v1.X, +v1.Y, +v1.Z);
        }

        public static bool operator <(Vector3 v1, Vector3 v2)
        {
            return v1.SumComponentSqrs() < v2.SumComponentSqrs();
        }

        public static bool operator >(Vector3 v1, Vector3 v2)
        {
            return v1.SumComponentSqrs() > v2.SumComponentSqrs();
        }

        public static bool operator <=(Vector3 v1, Vector3 v2)
        {
            return v1.SumComponentSqrs() <= v2.SumComponentSqrs();
        }

        public static bool operator >=(Vector3 v1, Vector3 v2)
        {
            return v1.SumComponentSqrs() >= v2.SumComponentSqrs();
        }

        public static bool operator ==(Vector3 v1, Vector3 v2)
        {
            return Math.Abs(v1.X - v2.X) <= EqualityTolerance &&
                   Math.Abs(v1.Y - v2.Y) <= EqualityTolerance &&
                   Math.Abs(v1.Z - v2.Z) <= EqualityTolerance;
        }

        public static bool operator !=(Vector3 v1, Vector3 v2)
        {
            return !(v1 == v2);
        }

        public static double Abs(Vector3 v1)
        {
            return v1.Magnitude;
        }

        public double Abs()
        {
            return Magnitude;
        }

        public static double SumComponents(Vector3 v1)
        {
            return v1.X + v1.Y + v1.Z;
        }

        public double SumComponents()
        {
            return SumComponents(this);
        }

        public static double SumComponentSqrs(Vector3 v1)
        {
            Vector3 v2 = SqrComponents(v1);
            return v2.SumComponents();
        }

        public double SumComponentSqrs()
        {
            return SumComponentSqrs(this);
        }

        public static Vector3 SqrComponents(Vector3 v1)
        {
            return new Vector3(v1.X * v1.X, v1.Y * v1.Y, v1.Z * v1.Z);
        }

        public override string ToString()
        {
            return ToString(null, null);
        }

        public string ToString(string format, IFormatProvider formatProvider)
        {
            if (format == null || format == "")
            {
                return String.Format("({0}, {1}, {2})", X, Y, Z);
            }

            char firstChar = format[0];
            string remainder = null;

            if (format.Length > 1)
            {
                remainder = format.Substring(1);
            }

            switch (firstChar)
            {
                case 'x': return X.ToString(remainder, formatProvider);
                case 'y': return Y.ToString(remainder, formatProvider);
                case 'z': return Z.ToString(remainder, formatProvider);
                default:
                    return String.Format(
                        "({0}, {1}, {2})",
                        X.ToString(format, formatProvider),
                        Y.ToString(format, formatProvider),
                        Z.ToString(format, formatProvider)
                    );
            }
        }

        public override int GetHashCode()
        {
            return (int)((X + Y + Z) % Int32.MaxValue);
        }

        public override bool Equals(object other)
        {
            if (other is Vector3)
            {
                Vector3 otherVector = (Vector3)other;
                return otherVector == this;
            }
            return false;
        }

        public bool Equals(Vector3 other)
        {
            return other == this;
        }

        public int CompareTo(Vector3 other)
        {
            if (this < other)
            {
                return -1;
            }
            else if (this > other)
            {
                return 1;
            }
            return 0;
        }

        public int CompareTo(object other)
        {
            if (other is Vector3)
            {
                return CompareTo((Vector3)other);
            }
            throw new ArgumentException(
                NON_VECTOR_COMPARISON + "\n" + ARGUMENT_TYPE + other.GetType().ToString(),
                "other"
            );
        }

        public static readonly Vector3 origin = new Vector3(0, 0, 0);
        public static readonly Vector3 xAxis = new Vector3(1, 0, 0);
        public static readonly Vector3 yAxis = new Vector3(0, 1, 0);
        public static readonly Vector3 zAxis = new Vector3(0, 0, 1);

        private const string THREE_COMPONENTS = "Array must contain exactly three components, (x,y,z)";
        private const string NON_VECTOR_COMPARISON = "Cannot compare a Vector3 to a non-Vector3";
        private const string ARGUMENT_TYPE = "The argument provided is a type of ";
        private const string NEGATIVE_MAGNITUDE = "The magnitude of a Vector3 must be a positive value";
        private const string ORAGIN_VECTOR_MAGNITUDE = "Cannot change the magnitude of Vector3(0,0,0)";

        // Tolerance for floating-point equality comparison.
        // Using 1e-9 (one billionth) rather than double.Epsilon (~5e-324) because
        // double.Epsilon is too small for practical comparison - floating-point
        // arithmetic errors routinely exceed it, making equality checks fail.
        public const double EqualityTolerance = 1e-9;
        public static readonly Vector3 MinValue = new Vector3(double.MinValue, double.MinValue, double.MinValue);
        public static readonly Vector3 MaxValue = new Vector3(double.MaxValue, double.MaxValue, double.MaxValue);
        public static readonly Vector3 Epsilon = new Vector3(double.Epsilon, double.Epsilon, double.Epsilon);

        public Vector2 GetXY()
        {
            return new Vector2(X, Y);
        }

        public Vector3 RollComponents(int turns)
        {
            Vector3 roll = new Vector3();
            for (int i = 0; i < 3; i++)
            {
                roll[i] = this[(i - turns + 300) % 3];
            }
            return roll;
        }

        public static Vector3 Parse(string input)
        {
            string[] components = input.Split(',');
            if (components.Length != 3)
            {
                throw new FormatException("string does not contain 3 components");
            }

            double[] values = new double[3];
            for (int i = 0; i < 3; i++)
            {
                values[i] = double.Parse(components[i], Constants.DecimalParseFormat);
            }

            return new Vector3(values);
        }

        public static Vector3 ElementwiseMax(Vector3 v1, Vector3 v2)
        {
            return new Vector3(
                Math.Max(v1.X, v2.X),
                Math.Max(v1.Y, v2.Y),
                Math.Max(v1.Z, v2.Z)
            );
        }

        public static Vector3 ElementwiseMin(Vector3 v1, Vector3 v2)
        {
            return new Vector3(
                Math.Min(v1.X, v2.X),
                Math.Min(v1.Y, v2.Y),
                Math.Min(v1.Z, v2.Z)
            );
        }
    }
}
