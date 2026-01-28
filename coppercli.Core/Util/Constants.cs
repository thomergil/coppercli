using System.Globalization;

namespace coppercli.Core.Util
{
    /// <summary>
    /// Core constants shared across the coppercli.Core library.
    /// CLI-specific constants belong in coppercli/CliConstants.cs.
    /// GRBL protocol constants belong in GrblProtocol.cs.
    /// </summary>
    public static class Constants
    {
        // =========================================================================
        // Number formatting (ensures consistent decimal parsing across locales)
        // =========================================================================

        /// <summary>
        /// Number format for parsing decimals from G-code and GRBL responses.
        /// Forces '.' as decimal separator regardless of system locale.
        /// </summary>
        public static readonly NumberFormatInfo DecimalParseFormat = new() { NumberDecimalSeparator = "." };

        /// <summary>
        /// Number format for outputting decimals in G-code commands.
        /// Forces '.' as decimal separator with 3 decimal places.
        /// </summary>
        public static readonly NumberFormatInfo DecimalOutputFormat = new() { NumberDecimalSeparator = ".", NumberDecimalDigits = 3 };

        // =========================================================================
        // Version requirements
        // =========================================================================

        /// <summary>
        /// Minimum supported GRBL version. The build number encodes the letter suffix (e.g., 'f' = 102).
        /// GRBL 1.1f introduced the real-time status report format we depend on.
        /// </summary>
        public static readonly Version MinimumGrblVersion = new(1, 1, 'f');

        // =========================================================================
        // Serial port timing
        // =========================================================================

        /// <summary>Read timeout for serial port operations (ms).</summary>
        public const int SerialReadTimeoutMs = 100;

        /// <summary>Write timeout for serial port operations (ms).</summary>
        public const int SerialWriteTimeoutMs = 1000;

        /// <summary>Default baud rate for GRBL controllers (GRBL v0.9+ default).</summary>
        public const int DefaultBaudRate = 115200;

        // =========================================================================
        // GRBL controller defaults
        // =========================================================================

        /// <summary>Default GRBL serial buffer size in bytes. Commands are queued until this fills.</summary>
        public const int GrblBufferSize = 127;

        /// <summary>Default value for feed/rapid/spindle overrides (100% = normal speed).</summary>
        public const int OverrideDefaultPercent = 100;

        /// <summary>Default interval for polling machine status via '?' command (ms).</summary>
        public const int StatusPollIntervalMs = 100;

        /// <summary>Default TCP port for Ethernet-connected GRBL controllers.</summary>
        public const int DefaultEthernetPort = 34000;

        // =========================================================================
        // Proxy server (for bridging serial to TCP)
        // =========================================================================

        /// <summary>Buffer size for proxy TCP read/write operations (bytes).</summary>
        public const int ProxyBufferSize = 4096;

        /// <summary>Sleep interval in proxy worker thread when no data available (ms).</summary>
        public const int ProxyThreadSleepMs = 1;

        /// <summary>Sleep interval in proxy accept loop when waiting for connections (ms).</summary>
        public const int ProxyAcceptLoopSleepMs = 100;

        /// <summary>Socket poll timeout for checking data/disconnection (microseconds). 100ms.</summary>
        public const int SocketPollTimeoutMicroseconds = 100000;

        // =========================================================================
        // Work loop timing
        // =========================================================================

        /// <summary>Interval for updating file position during G-code streaming (ms).</summary>
        public const int FilePosUpdateIntervalMs = 500;

        /// <summary>
        /// Grace period after connection to ignore spurious errors (ms).
        /// Some controllers send garbage during initialization.
        /// </summary>
        public const int ErrorGracePeriodMs = 200;

        // =========================================================================
        // Logging
        // =========================================================================

        /// <summary>
        /// Filename for raw serial traffic log (created when LogTraffic setting is enabled).
        /// This logs all bytes sent/received on the serial port for debugging.
        /// </summary>
        public const string SerialTrafficLogFile = "serial_traffic.log";
    }
}
