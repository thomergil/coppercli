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

        public static Vector3 CrossProduct(Vector3 v1, Vector3 v2)
        {
            return new Vector3(
                v1.Y * v2.Z - v1.Z * v2.Y,
                v1.Z * v2.X - v1.X * v2.Z,
                v1.X * v2.Y - v1.Y * v2.X
            );
        }

        public Vector3 CrossProduct(Vector3 other)
        {
            return CrossProduct(this, other);
        }

        public static double DotProduct(Vector3 v1, Vector3 v2)
        {
            return v1.X * v2.X + v1.Y * v2.Y + v1.Z * v2.Z;
        }

        public double DotProduct(Vector3 other)
        {
            return DotProduct(this, other);
        }

        public static double MixedProduct(Vector3 v1, Vector3 v2, Vector3 v3)
        {
            return DotProduct(CrossProduct(v1, v2), v3);
        }

        public double MixedProduct(Vector3 other_v1, Vector3 other_v2)
        {
            return DotProduct(CrossProduct(this, other_v1), other_v2);
        }

        public static Vector3 Normalize(Vector3 v1)
        {
            if (v1.Magnitude == 0)
            {
                throw new DivideByZeroException(NORMALIZE_0);
            }

            double inverse = 1 / v1.Magnitude;
            return new Vector3(v1.X * inverse, v1.Y * inverse, v1.Z * inverse);
        }

        public void Normalize()
        {
            this = Normalize(this);
        }

        public static Vector3 Interpolate(Vector3 v1, Vector3 v2, double control, bool allowExtrapolation)
        {
            if (!allowExtrapolation && (control > 1 || control < 0))
            {
                throw new ArgumentOutOfRangeException("control", control,
                    INTERPOLATION_RANGE + "\n" + ARGUMENT_VALUE + control);
            }
            return new Vector3(
                v1.X * (1 - control) + v2.X * control,
                v1.Y * (1 - control) + v2.Y * control,
                v1.Z * (1 - control) + v2.Z * control
            );
        }

        public static Vector3 Interpolate(Vector3 v1, Vector3 v2, double control)
        {
            return Interpolate(v1, v2, control, false);
        }

        public Vector3 Interpolate(Vector3 other, double control)
        {
            return Interpolate(this, other, control);
        }

        public Vector3 Interpolate(Vector3 other, double control, bool allowExtrapolation)
        {
            return Interpolate(this, other, control, allowExtrapolation);
        }

        public static double Distance(Vector3 v1, Vector3 v2)
        {
            return (double)Math.Sqrt(
                (v1.X - v2.X) * (v1.X - v2.X) +
                (v1.Y - v2.Y) * (v1.Y - v2.Y) +
                (v1.Z - v2.Z) * (v1.Z - v2.Z)
            );
        }

        public double Distance(Vector3 other)
        {
            return Distance(this, other);
        }

        public static double Angle(Vector3 v1, Vector3 v2)
        {
            return (double)Math.Acos(Normalize(v1).DotProduct(Normalize(v2)));
        }

        public double Angle(Vector3 other)
        {
            return Angle(this, other);
        }

        public static Vector3 Max(Vector3 v1, Vector3 v2)
        {
            if (v1 >= v2)
            {
                return v1;
            }
            return v2;
        }

        public Vector3 Max(Vector3 other)
        {
            return Max(this, other);
        }

        public static Vector3 Min(Vector3 v1, Vector3 v2)
        {
            if (v1 <= v2)
            {
                return v1;
            }
            return v2;
        }

        public Vector3 Min(Vector3 other)
        {
            return Min(this, other);
        }

        public static Vector3 Yaw(Vector3 v1, double degree)
        {
            double x = (v1.Z * (double)Math.Sin(degree)) + (v1.X * (double)Math.Cos(degree));
            double y = v1.Y;
            double z = (v1.Z * (double)Math.Cos(degree)) - (v1.X * (double)Math.Sin(degree));
            return new Vector3(x, y, z);
        }

        public void Yaw(double degree)
        {
            this = Yaw(this, degree);
        }

        public static Vector3 Pitch(Vector3 v1, double degree)
        {
            double x = v1.X;
            double y = (v1.Y * (double)Math.Cos(degree)) - (v1.Z * (double)Math.Sin(degree));
            double z = (v1.Y * (double)Math.Sin(degree)) + (v1.Z * (double)Math.Cos(degree));
            return new Vector3(x, y, z);
        }

        public void Pitch(double degree)
        {
            this = Pitch(this, degree);
        }

        public static Vector3 Roll(Vector3 v1, double degree)
        {
            double x = (v1.X * (double)Math.Cos(degree)) - (v1.Y * (double)Math.Sin(degree));
            double y = (v1.X * (double)Math.Sin(degree)) + (v1.Y * (double)Math.Cos(degree));
            double z = v1.Z;
            return new Vector3(x, y, z);
        }

        public void Roll(double degree)
        {
            this = Roll(this, degree);
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

        public static Vector3 PowComponents(Vector3 v1, double power)
        {
            return new Vector3(
                (double)Math.Pow(v1.X, power),
                (double)Math.Pow(v1.Y, power),
                (double)Math.Pow(v1.Z, power)
            );
        }

        public void PowComponents(double power)
        {
            this = PowComponents(this, power);
        }

        public static Vector3 SqrtComponents(Vector3 v1)
        {
            return new Vector3(
                (double)Math.Sqrt(v1.X),
                (double)Math.Sqrt(v1.Y),
                (double)Math.Sqrt(v1.Z)
            );
        }

        public void SqrtComponents()
        {
            this = SqrtComponents(this);
        }

        public static Vector3 SqrComponents(Vector3 v1)
        {
            return new Vector3(v1.X * v1.X, v1.Y * v1.Y, v1.Z * v1.Z);
        }

        public void SqrComponents()
        {
            this = SqrComponents(this);
        }

        public override string ToString()
        {
            return ToString(null, null);
        }

        public string ToVerbString()
        {
            string output = null;

            if (IsUnitVector())
            {
                output += UNIT_VECTOR;
            }
            else
            {
                output += POSITIONAL_VECTOR;
            }

            output += string.Format("( x={0}, y={1}, z={2} )", X, Y, Z);
            output += MAGNITUDE + Magnitude;

            return output;
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

        public static bool IsUnitVector(Vector3 v1)
        {
            return Math.Abs(v1.Magnitude - 1) <= EqualityTolerance;
        }

        public bool IsUnitVector()
        {
            return IsUnitVector(this);
        }

        public static bool IsBackFace(Vector3 normal, Vector3 lineOfSight)
        {
            return normal.DotProduct(lineOfSight) < 0;
        }

        public bool IsBackFace(Vector3 lineOfSight)
        {
            return IsBackFace(this, lineOfSight);
        }

        public static bool IsPerpendicular(Vector3 v1, Vector3 v2)
        {
            return v1.DotProduct(v2) == 0;
        }

        public bool IsPerpendicular(Vector3 other)
        {
            return IsPerpendicular(this, other);
        }

        public static readonly Vector3 origin = new Vector3(0, 0, 0);
        public static readonly Vector3 xAxis = new Vector3(1, 0, 0);
        public static readonly Vector3 yAxis = new Vector3(0, 1, 0);
        public static readonly Vector3 zAxis = new Vector3(0, 0, 1);

        private const string THREE_COMPONENTS = "Array must contain exactly three components, (x,y,z)";
        private const string NORMALIZE_0 = "Cannot normalize a vector when its magnitude is zero";
        private const string INTERPOLATION_RANGE = "Control parameter must be a value between 0 & 1";
        private const string NON_VECTOR_COMPARISON = "Cannot compare a Vector3 to a non-Vector3";
        private const string ARGUMENT_TYPE = "The argument provided is a type of ";
        private const string ARGUMENT_VALUE = "The argument provided has a value of ";
        private const string ARGUMENT_LENGTH = "The argument provided has a length of ";
        private const string NEGATIVE_MAGNITUDE = "The magnitude of a Vector3 must be a positive value";
        private const string ORAGIN_VECTOR_MAGNITUDE = "Cannot change the magnitude of Vector3(0,0,0)";
        private const string UNIT_VECTOR = "Unit vector composing of ";
        private const string POSITIONAL_VECTOR = "Positional vector composing of ";
        private const string MAGNITUDE = " of magnitude ";

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
