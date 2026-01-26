using System;
using System.Globalization;

namespace coppercli.Core.Util
{
    public class Constants
    {
        public static NumberFormatInfo DecimalParseFormat = new NumberFormatInfo() { NumberDecimalSeparator = "." };

        public static NumberFormatInfo DecimalOutputFormat
        {
            get
            {
                return new NumberFormatInfo() { NumberDecimalSeparator = ".", NumberDecimalDigits = 3 };
            }
        }

        public static string FileFilterGCode = "GCode|*.tap;*.nc;*.ngc|All Files|*.*";
        public static string FileFilterProbeGrid = "Probe Grids|*.pgrid|All Files|*.*";
        public static string FileFilterSettings = "Grbl settings|*.gbl;*.nc;*.ngc|All Files|*.*";

        public static string LogFile = "log.txt";

        public static char[] NewLines = new char[] { '\n', '\r' };

        public static Version MinimumGrblVersion = new Version(1, 1, (int)'f');
    }
}
