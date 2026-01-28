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

        // =========================================================================
        // Serial port defaults
        // =========================================================================
        public const int SerialPortReadTimeoutMs = 100;
        public const int SerialPortWriteTimeoutMs = 1000;
        public const int DefaultBaudRate = 115200;

        // =========================================================================
        // GRBL controller defaults
        // =========================================================================
        public const int DefaultControllerBufferSize = 127;
        public const int OverrideDefaultPercent = 100;  // Feed/Rapid/Spindle override reset value
        public const int DefaultStatusPollIntervalMs = 100;
        public const int DefaultEthernetPort = 34000;

        // =========================================================================
        // Proxy defaults
        // =========================================================================
        public const int ProxyBufferSize = 4096;
        public const int ProxyThreadSleepMs = 1;
        public const int ProxyAcceptLoopSleepMs = 100;

        // =========================================================================
        // Work loop timing
        // =========================================================================
        public const int FilePosUpdateIntervalMs = 500;
        public const int ErrorGracePeriodMs = 200;      // Ignore spurious errors during startup
    }
}
