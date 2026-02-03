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

        /// <summary>Default TCP port for network-connected GRBL controllers.</summary>
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

        // =========================================================================
        // Machine operation timing (used by controllers)
        // =========================================================================

        /// <summary>Delay after sending a command before sending another (ms).</summary>
        public const int CommandDelayMs = 200;

        /// <summary>Timeout waiting for machine to become idle (ms).</summary>
        public const int IdleWaitTimeoutMs = 3000;

        /// <summary>Timeout waiting for motion to start after sending command (ms).</summary>
        public const int MotionStartTimeoutMs = 1000;

        /// <summary>Timeout waiting for Z axis to reach target height (ms). 30 seconds.</summary>
        public const int ZHeightWaitTimeoutMs = 30000;

        /// <summary>Timeout waiting for any move to complete (ms). 60 seconds.</summary>
        public const int MoveCompleteTimeoutMs = 60000;

        /// <summary>Timeout for homing operation to complete (ms). 60 seconds.</summary>
        public const int HomingTimeoutMs = 60000;

        /// <summary>
        /// Duration machine must be continuously idle to confirm stable state (ms).
        /// Used to detect true completion vs. brief pauses.
        /// </summary>
        public const int IdleSettleMs = 1000;

        /// <summary>
        /// Settle time after file load before starting mill (ms).
        /// Allows user to verify setup before motion begins.
        /// </summary>
        public const int PostIdleSettleMs = 5000;

        /// <summary>One second in milliseconds. Used for countdown calculations.</summary>
        public const int OneSecondMs = 1000;

        /// <summary>Wait time after soft reset for GRBL to reinitialize (ms).</summary>
        public const int ResetWaitMs = 500;

        // =========================================================================
        // Position tolerances
        // =========================================================================

        /// <summary>
        /// Tolerance for position comparisons (mm).
        /// Positions within this distance are considered equal.
        /// </summary>
        public const double PositionToleranceMm = 0.1;

        /// <summary>
        /// Epsilon for height range comparisons.
        /// Used to determine if there's meaningful height variation in probe data.
        /// </summary>
        public const double HeightRangeEpsilon = 0.0001;

        // =========================================================================
        // Z heights (machine coordinates)
        // Machine Z=0 is at home (top), negative values are down toward workpiece.
        // =========================================================================

        /// <summary>
        /// Z position to retract to after milling completes (mm, machine coordinates).
        /// -1mm from top provides clearance while avoiding limit switch.
        /// </summary>
        public const double MillCompleteZ = -1.0;

        /// <summary>
        /// Z position to retract to before starting mill (mm, machine coordinates).
        /// Prevents dragging across workpiece if Z was left low from previous operation.
        /// </summary>
        public const double MillStartSafetyZ = -1.0;

        /// <summary>
        /// Z clearance height for tool changes (mm, machine coordinates).
        /// -1mm from top provides clearance while avoiding limit switch.
        /// </summary>
        public const double ToolChangeClearanceZ = -1.0;

        // =========================================================================
        // Tool setter defaults
        // =========================================================================

        /// <summary>Maximum depth to probe for tool setter (mm).</summary>
        public const double ToolSetterProbeDepth = 50.0;

        /// <summary>Fast seek feed rate for tool setter (mm/min).</summary>
        public const double ToolSetterSeekFeed = 500.0;

        /// <summary>Slow precise probe feed rate for tool setter (mm/min).</summary>
        public const double ToolSetterProbeFeed = 50.0;

        /// <summary>Retract distance after probing tool setter (mm).</summary>
        public const double ToolSetterRetract = 10.0;

        /// <summary>Clearance above last known tool setter position for rapid approach (mm).</summary>
        public const double ToolSetterApproachClearance = 20.0;

        // =========================================================================
        // G-code parsing
        // =========================================================================

        /// <summary>
        /// Number of lines to search backwards for tool info (Tn commands).
        /// When an M6 is found, we search up to this many lines backwards for the tool number.
        /// </summary>
        public const int ToolInfoSearchLines = 10;

        // =========================================================================
        // G-code warning prefixes
        // Used to tag warnings from GCodeParser and filter them for display.
        // =========================================================================

        /// <summary>Prefix for dangerous G-code warnings (e.g., G28 home commands).</summary>
        public const string WarningPrefixDanger = "DANGER";

        /// <summary>Prefix for imperial units warning.</summary>
        public const string WarningPrefixInches = "INCHES";

        // =========================================================================
        // Controller cancellation timeouts
        // =========================================================================

        /// <summary>Timeout waiting for controller to cancel cleanly (ms). 5 seconds.</summary>
        public const int ControllerCancelTimeoutMs = 5000;

        /// <summary>Timeout waiting for probe stop to complete (ms). 2 seconds.</summary>
        public const int ProbeStopTimeoutMs = 2000;

        // =========================================================================
        // Mill grid visualization
        // =========================================================================

        /// <summary>Maximum grid width in cells for mill visualization.</summary>
        public const int MillGridMaxWidth = 50;

        /// <summary>Maximum grid height in cells for mill visualization.</summary>
        public const int MillGridMaxHeight = 20;

        /// <summary>Z threshold below which the tool is considered cutting (mm, work coords).</summary>
        public const double MillCuttingDepthThreshold = 1.0;

        /// <summary>Minimum coordinate range to avoid division by zero in grid mapping.</summary>
        public const double MillMinRangeThreshold = 0.001;

        // =========================================================================
        // Probe defaults
        // =========================================================================

        /// <summary>Maximum depth to probe for PCB surface (mm).</summary>
        public const double ProbeMaxDepth = 50.0;

        /// <summary>Default probe feed rate (mm/min).</summary>
        public const double ProbeFeed = 100.0;

        /// <summary>Default Z retract height after probing (mm, work coordinates).</summary>
        public const double RetractZMm = 6.0;

        // =========================================================================
        // Connection error messages
        // =========================================================================

        /// <summary>Error sent to rejected proxy client before closing connection.</summary>
        public const string ProxyConnectionRejected = "Connection rejected: another client is already connected. Close the existing connection first.\r\n";

        /// <summary>Prefix to detect proxy connection rejection messages.</summary>
        public const string ProxyConnectionRejectedPrefix = "Connection rejected:";

        /// <summary>Prefix for serial port busy error from proxy.</summary>
        public const string ProxySerialPortBusyPrefix = "Cannot access";

        /// <summary>Error sent when serial port is in use by web client.</summary>
        public const string ProxySerialPortInUse = "Serial port in use: a web client is connected. Force disconnect to continue.\r\n";

        /// <summary>Prefix to detect serial port in use by web client.</summary>
        public const string ProxySerialPortInUsePrefix = "Serial port in use:";

        /// <summary>Message sent to client before force-disconnecting them.</summary>
        public const string ProxyForceDisconnect = "Force disconnect: another client is taking over.\r\n";

        /// <summary>Prefix to detect force disconnect messages.</summary>
        public const string ProxyForceDisconnectPrefix = "Force disconnect:";

        /// <summary>Delay after sending force disconnect message before closing connection.</summary>
        public const int ForceDisconnectMessageDelayMs = 200;
    }
}
