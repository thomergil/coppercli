namespace coppercli
{
    /// <summary>
    /// CLI-specific constants for the coppercli application.
    /// Core/shared constants belong in coppercli.Core/Util/Constants.cs.
    /// GRBL protocol constants belong in coppercli.Core/Util/GrblProtocol.cs.
    /// </summary>
    internal static class CliConstants
    {
        // =========================================================================
        // Application info
        // =========================================================================

        /// <summary>Application name shown in UI and logs.</summary>
        public const string AppTitle = "coppercli";

        /// <summary>Current version (with 'v' prefix for display).</summary>
        public const string AppVersion = "v0.3.0";

        // =========================================================================
        // Timing: Connection and communication
        // =========================================================================

        /// <summary>Timeout for establishing serial/network connection (ms).</summary>
        public const int ConnectionTimeoutMs = 5000;

        /// <summary>Timeout waiting for GRBL response to a command (ms).</summary>
        public const int GrblResponseTimeoutMs = 3000;

        /// <summary>Timeout for auto-detecting serial ports during connection (ms).</summary>
        public const int AutoDetectTimeoutMs = 2000;

        /// <summary>Interval for polling machine status during operations (ms).</summary>
        public const int StatusPollIntervalMs = 100;

        /// <summary>Interval for polling during jog operations - faster for responsiveness (ms).</summary>
        public const int JogPollIntervalMs = 50;

        /// <summary>Delay after sending a command before sending another (ms).</summary>
        public const int CommandDelayMs = 200;

        /// <summary>Duration to display confirmation messages before continuing (ms).</summary>
        public const int ConfirmationDisplayMs = 1000;

        /// <summary>Wait time after soft reset for GRBL to reinitialize (ms).</summary>
        public const int ResetWaitMs = 500;

        // =========================================================================
        // Timing: Idle detection and settling
        // =========================================================================

        /// <summary>Timeout waiting for machine to become idle (ms).</summary>
        public const int IdleWaitTimeoutMs = 3000;

        /// <summary>
        /// Settle time after file load before starting mill (ms).
        /// Allows user to verify setup before motion begins.
        /// </summary>
        public const int PostIdleSettleMs = 5000;

        /// <summary>
        /// Duration machine must be continuously idle to confirm stable state (ms).
        /// Used to detect true completion vs. brief pauses.
        /// </summary>
        public const int IdleSettleMs = 1000;

        /// <summary>One second in milliseconds. Used for countdown calculations.</summary>
        public const int OneSecondMs = 1000;

        // =========================================================================
        // Timing: Long operations
        // =========================================================================

        /// <summary>Timeout for homing operation to complete (ms). 60 seconds.</summary>
        public const int HomingTimeoutMs = 60000;

        /// <summary>Timeout waiting for Z axis to reach target height (ms). 30 seconds.</summary>
        public const int ZHeightWaitTimeoutMs = 30000;

        /// <summary>Timeout waiting for any move to complete (ms). 60 seconds.</summary>
        public const int MoveCompleteTimeoutMs = 60000;

        // =========================================================================
        // Serial port configuration
        // =========================================================================

        /// <summary>
        /// Common baud rates for GRBL controllers, ordered by likelihood.
        /// 115200: GRBL v0.9+ default (most common).
        /// 250000: Some high-speed configurations.
        /// 9600: Older GRBL, some Bluetooth modules.
        /// </summary>
        public static readonly int[] CommonBaudRates = { 115200, 250000, 9600, 57600, 38400, 19200 };

        /// <summary>
        /// Glob patterns for Unix serial ports that typically connect to CNC controllers.
        /// Used for auto-detection on macOS and Linux.
        /// </summary>
        public static readonly string[] UnixSerialPortPatterns =
            { "ttyUSB*", "ttyACM*", "tty.usbserial*", "cu.usbmodem*", "tty.usbmodem*" };

        // =========================================================================
        // File paths and extensions
        // =========================================================================

        /// <summary>Settings file name (stored in app directory).</summary>
        public const string SettingsFileName = "settings.json";

        /// <summary>Session state file name (stored in app directory).</summary>
        public const string SessionFileName = "session.json";

        /// <summary>Auto-saved probe data file name (stored in app directory).</summary>
        public const string ProbeAutoSaveFileName = "probe_autosave.pgrid";

        /// <summary>Recognized G-code file extensions for file dialogs and filtering.</summary>
        public static readonly string[] GCodeExtensions = { ".nc", ".gcode", ".ngc", ".gc", ".tap", ".cnc" };

        /// <summary>Probe grid file extension.</summary>
        public static readonly string[] ProbeGridExtensions = { ".pgrid" };

        /// <summary>Macro file extension.</summary>
        public const string MacroExtension = ".cmacro";

        /// <summary>Comment character in macro files.</summary>
        public const char MacroCommentChar = '#';

        /// <summary>Date format for auto-generated probe file names (yyyy-MM-dd-HH-mm).</summary>
        public const string ProbeDateFormat = "yyyy-MM-dd-HH-mm";

        // =========================================================================
        // Position and movement: Tolerances
        // =========================================================================

        /// <summary>
        /// Tolerance for position comparisons (mm).
        /// Positions within this distance are considered equal.
        /// </summary>
        public const double PositionToleranceMm = 0.1;

        // =========================================================================
        // Position and movement: Z heights (work coordinates)
        // =========================================================================

        /// <summary>
        /// Default Z height for retracting tool away from workpiece (mm, work coordinates).
        /// Used during probing sequences and manual operations.
        /// </summary>
        public const double RetractZMm = 6.0;

        /// <summary>
        /// Reference Z height above workpiece for measurements (mm, work coordinates).
        /// Used as starting point for probe operations.
        /// </summary>
        public const double ReferenceZHeightMm = 1.0;

        // =========================================================================
        // Position and movement: Z heights (machine coordinates)
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
        // Menu shortcuts: 0-9 then A-Z for 36 items
        // =========================================================================

        /// <summary>Maximum number of menu items with keyboard shortcuts (0-9 + A-Z).</summary>
        public const int MaxMenuShortcuts = 36;

        /// <summary>Index where numeric shortcuts end and alphabetic begin (0-9 = indices 0-9).</summary>
        public const int MenuShortcutAlphaStart = 10;

        /// <summary>Index of the '0' key shortcut (comes after 1-9).</summary>
        public const int MenuShortcutZeroIndex = 9;

        // =========================================================================
        // Jog modes: vi-style with digit prefix for distance multiplier
        // =========================================================================

        /// <summary>
        /// Defines a jog mode with speed and distance settings.
        /// </summary>
        /// <param name="Name">Display name for the mode.</param>
        /// <param name="Feed">Feed rate in mm/min.</param>
        /// <param name="BaseDistance">Base distance per jog in mm.</param>
        /// <param name="MaxMultiplier">Maximum digit multiplier (5 for Fast, 9 for others).</param>
        public record JogMode(string Name, double Feed, double BaseDistance, int MaxMultiplier)
        {
            /// <summary>Formats the distance for display given the current multiplier.</summary>
            public string FormatDistance(int multiplier) =>
                BaseDistance >= 1 ? $"{BaseDistance * multiplier:F0}mm"
                                  : $"{BaseDistance * multiplier:G}mm";
        }

        /// <summary>
        /// Available jog modes from fastest to finest.
        /// Multiplier is set by pressing 1-9 before jog key (default 1).
        /// Fast mode limits to 1-5 to keep distances reasonable.
        /// </summary>
        public static readonly JogMode[] JogModes =
        {
            new("Fast",   5000, 10.0,  5),  // 1-5: 10, 20, 30, 40, 50mm
            new("Normal",  500,  1.0,  9),  // 1-9: 1, 2, 3... 9mm
            new("Slow",     50,  0.1,  9),  // 1-9: 0.1, 0.2... 0.9mm
            new("Creep",     5,  0.01, 9),  // 1-9: 0.01, 0.02... 0.09mm
        };

        // =========================================================================
        // Probing defaults
        // =========================================================================

        /// <summary>Default margin around workpiece for probe grid (mm).</summary>
        public const double DefaultProbeMargin = 0.5;

        /// <summary>Default grid cell size for probe grid (mm).</summary>
        public const double DefaultProbeGridSize = 5.0;

        // =========================================================================
        // Mill progress display: Grid sizing
        // Terminal characters are ~2:1 aspect ratio (taller than wide), so we use
        // 2 characters per cell horizontally to approximate square cells.
        // =========================================================================

        /// <summary>Characters per cell horizontally (2 for square appearance).</summary>
        public const int MillGridCharsPerCell = 2;

        /// <summary>Maximum grid width in cells.</summary>
        public const int MillGridMaxWidth = 50;

        /// <summary>Maximum grid height in cells.</summary>
        public const int MillGridMaxHeight = 20;

        /// <summary>Minimum terminal width (in cells) required to show grid.</summary>
        public const int MillGridMinWidth = 10;

        /// <summary>Minimum terminal height (in cells) required to show grid.</summary>
        public const int MillGridMinHeight = 3;

        // =========================================================================
        // Mill progress display: Terminal layout
        // =========================================================================

        /// <summary>Fallback terminal width when Console.WindowWidth fails.</summary>
        public const int FallbackTerminalWidth = 80;

        /// <summary>Fallback terminal height when Console.WindowHeight fails.</summary>
        public const int FallbackTerminalHeight = 24;

        /// <summary>Vertical padding for header and borders (lines).</summary>
        public const int MillTermHeightPadding = 12;

        /// <summary>Space for box border characters.</summary>
        public const int MillBorderPadding = 2;

        /// <summary>Width of the progress bar in characters.</summary>
        public const int MillProgressBarWidth = 30;

        // =========================================================================
        // Mill progress display: ETA calculation
        // =========================================================================

        /// <summary>Minimum lines processed before showing ETA (avoids wild estimates).</summary>
        public const int MillMinLinesForEta = 10;

        /// <summary>Minimum elapsed seconds before showing ETA.</summary>
        public const double MillMinSecondsForEta = 1.0;

        // =========================================================================
        // Mill progress display: Cutting detection
        // =========================================================================

        /// <summary>
        /// Z values below this threshold (relative to work zero) indicate cutting (mm).
        /// Used to mark cells as "visited" in the progress grid display.
        /// </summary>
        public const double MillCuttingDepthThreshold = 0.1;

        /// <summary>Minimum coordinate range for grid mapping (prevents division issues).</summary>
        public const double MillMinRangeThreshold = 0.001;

        // =========================================================================
        // Mill progress display: Visual markers
        // =========================================================================

        /// <summary>Marker for current tool position in grid.</summary>
        public const string MillCurrentPosMarker = "● ";

        /// <summary>Marker for cells where cutting has occurred.</summary>
        public const string MillVisitedMarker = "░░";

        /// <summary>Marker for cells not yet visited.</summary>
        public const string MillEmptyMarker = "··";

        /// <summary>Overlay message when machine is in feed hold.</summary>
        public const string OverlayHoldMessage = "HOLD - Press R to resume";

        /// <summary>Safety checklist message shown before milling starts.</summary>
        public const string SafetyChecklistMessage = "Probe clip removed? Door closed?";

        /// <summary>Safety checklist sub-message with key instructions.</summary>
        public const string SafetyChecklistSubMessage = "Y=Start  N=Cancel";

        /// <summary>Overlay message when machine is in alarm state.</summary>
        public const string OverlayAlarmMessage = "ALARM - Press X to stop";

        /// <summary>Warning message when no machine profile is selected.</summary>
        public const string NoMachineProfileWarning = "No machine profile selected";

        /// <summary>Sub-message for no machine profile warning.</summary>
        public const string NoMachineProfileSubMessage = "Y=Continue  X=Cancel";

        /// <summary>Warning message when sleep prevention unavailable in network mode.</summary>
        public const string SleepPreventionWarning = "Sleep prevention unavailable";

        /// <summary>Sub-message for sleep prevention warning.</summary>
        public const string SleepPreventionSubMessage = "System may sleep during job. Y=Continue  X=Cancel";

        // =========================================================================
        // Probe grid display sizing
        // =========================================================================

        /// <summary>Padding around probe grid display (characters).</summary>
        public const int ProbeGridConsolePadding = 4;

        /// <summary>Maximum width for probe grid display (cells).</summary>
        public const int ProbeGridMaxDisplayWidth = 50;

        /// <summary>Maximum height for probe grid display (cells).</summary>
        public const int ProbeGridMaxDisplayHeight = 20;

        /// <summary>Vertical padding for probe grid header (lines).</summary>
        public const int ProbeGridHeaderPadding = 8;

        // =========================================================================
        // Proxy server (CLI-specific settings)
        // See also coppercli.Core/Util/Constants.cs for core proxy constants.
        // =========================================================================

        /// <summary>Default TCP port for proxy server.</summary>
        public const int ProxyDefaultPort = 34000;

        /// <summary>Interval for updating proxy status display (ms).</summary>
        public const int ProxyStatusUpdateIntervalMs = 500;

        /// <summary>Status message when no client is connected.</summary>
        public const string ProxyNoClientStatus = "No client connected";

        // =========================================================================
        // Network scanning (for Ethernet GRBL auto-detection)
        // =========================================================================

        /// <summary>Timeout for each connection attempt during network scan (ms).</summary>
        public const int NetworkScanTimeoutMs = 200;

        /// <summary>Number of concurrent connection attempts during scan.</summary>
        public const int NetworkScanParallelism = 100;

        // =========================================================================
        // Macro system timing
        // =========================================================================

        /// <summary>Pause to display parsed macro command count before execution (ms).</summary>
        public const int MacroParseDisplayMs = 500;

        // =========================================================================
        // Prompt text (user interaction)
        // =========================================================================

        /// <summary>Prompt shown when waiting for user to continue.</summary>
        public const string PromptEnter = "Press Enter to continue";

        /// <summary>Prompt shown during interruptible operations.</summary>
        public const string PromptEscapeToStop = "Press Escape to stop";

        /// <summary>Prompt shown during interruptible operations (cancel variant).</summary>
        public const string PromptEscapeToCancel = "Press Escape to cancel";

        /// <summary>Hint shown for quit option in prompts.</summary>
        public const string PromptQuitHint = "q to cancel";

        // =========================================================================
        // Color theme (Spectre.Console markup names)
        // =========================================================================

        /// <summary>Color for error messages.</summary>
        public const string ColorError = "red";

        /// <summary>Color for success messages and confirmations.</summary>
        public const string ColorSuccess = "green";

        /// <summary>Color for warnings and attention-needed states.</summary>
        public const string ColorWarning = "yellow";

        /// <summary>Color for prompts, defaults, and user input areas.</summary>
        public const string ColorPrompt = "blue";

        /// <summary>Color for headers, labels, and status info.</summary>
        public const string ColorInfo = "cyan";

        /// <summary>Color for disabled items, help text, and secondary info.</summary>
        public const string ColorDim = "dim";

        /// <summary>Color for emphasis and important items.</summary>
        public const string ColorBold = "bold";

        // =========================================================================
        // Tool change: Status messages
        // =========================================================================

        /// <summary>Header label for tool change overlay.</summary>
        public const string ToolChangeLabel = "TOOL CHANGE";

        /// <summary>Status: Raising Z to clearance height.</summary>
        public const string ToolChangeStatusRaisingZ = "Raising Z...";

        /// <summary>Status: Moving to tool setter position.</summary>
        public const string ToolChangeStatusMovingToSetter = "Moving to tool setter...";

        /// <summary>Status: Measuring reference tool length.</summary>
        public const string ToolChangeStatusMeasuringRef = "Measuring reference tool...";

        /// <summary>Status: Measuring new tool length.</summary>
        public const string ToolChangeStatusMeasuringNew = "Measuring new tool...";

        /// <summary>Status: Adjusting Z origin for tool length difference.</summary>
        public const string ToolChangeStatusAdjustingZ = "Adjusting Z origin...";

        /// <summary>Status: Returning to work position.</summary>
        public const string ToolChangeStatusReturning = "Returning to work position...";

        /// <summary>Status: Tool change complete.</summary>
        public const string ToolChangeStatusComplete = "Tool change complete";

        /// <summary>Status: Moving to work area.</summary>
        public const string ToolChangeStatusMovingToWork = "Moving to work area...";

        /// <summary>Status: Probing Z height.</summary>
        public const string ToolChangeStatusProbingZ = "Probing Z...";

        /// <summary>Status: Z zeroed, raising tool.</summary>
        public const string ToolChangeStatusZeroing = "Z zeroed, raising...";

        // =========================================================================
        // Tool setter: Probing parameters
        // =========================================================================

        /// <summary>Maximum depth to probe when seeking tool setter surface (mm).</summary>
        public const double ToolSetterProbeDepth = 50.0;

        /// <summary>Fast feed rate for initial tool setter seek (mm/min).</summary>
        public const double ToolSetterSeekFeed = 500.0;

        /// <summary>Slow feed rate for precise tool measurement (mm/min).</summary>
        public const double ToolSetterProbeFeed = 50.0;

        /// <summary>Retract distance between fast and slow probes (mm).</summary>
        public const double ToolSetterRetract = 10.0;

        /// <summary>Clearance above last known position for rapid approach (mm).</summary>
        public const double ToolSetterApproachClearance = 20.0;

        // =========================================================================
        // Sleep prevention
        // =========================================================================

        /// <summary>macOS command to prevent idle sleep.</summary>
        public const string CaffeinateCommand = "caffeinate";

        /// <summary>caffeinate argument: prevent idle sleep.</summary>
        public const string CaffeinateArgs = "-i";

        /// <summary>Linux command to prevent sleep via systemd.</summary>
        public const string SystemdInhibitCommand = "systemd-inhibit";

        /// <summary>systemd-inhibit arguments: block idle and sleep.</summary>
        public const string SystemdInhibitArgs = "--what=idle:sleep --why=\"CNC operation\" sleep infinity";

        /// <summary>Command to check if a program exists (Unix).</summary>
        public const string WhichCommand = "which";

        /// <summary>Timeout for checking program availability (ms).</summary>
        public const int ProgramCheckTimeoutMs = 1000;
    }
}
