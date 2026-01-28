// Extracted from Program.cs

namespace coppercli
{
    /// <summary>
    /// CLI-specific constants for the coppercli application.
    /// </summary>
    internal static class CliConstants
    {
        // =========================================================================
        // Connection and timing
        // =========================================================================
        public const int ConnectionTimeoutMs = 5000;
        public const int GrblResponseTimeoutMs = 3000;
        public const int AutoDetectTimeoutMs = 2000;
        public const int StatusPollIntervalMs = 100;
        public const int JogPollIntervalMs = 50;
        public const int CommandDelayMs = 200;
        public const int ConfirmationDisplayMs = 1000;
        public const int ResetWaitMs = 500;
        public const int IdleWaitTimeoutMs = 3000;
        public const int PostIdleSettleMs = 5000;
        public const int IdleSettleMs = 1000;         // Settle time for stable idle checks
        public const int OneSecondMs = 1000;
        public const int HomingTimeoutMs = 60000;
        public const int ZHeightWaitTimeoutMs = 30000;
        public const int MoveCompleteTimeoutMs = 60000;

        // =========================================================================
        // Baud rates in order of likelihood for GRBL/CNC controllers
        // 115200: GRBL v0.9+ default (most common)
        // 250000: Some high-speed configurations
        // 9600: Older GRBL, some Bluetooth modules
        // 57600, 38400, 19200: Less common alternatives
        // =========================================================================
        public static readonly int[] CommonBaudRates = { 115200, 250000, 9600, 57600, 38400, 19200 };

        // =========================================================================
        // File paths
        // =========================================================================
        public const string SettingsFileName = "settings.json";
        public const string SessionFileName = "session.json";
        public const string ProbeAutoSaveFileName = "probe_autosave.pgrid";

        // =========================================================================
        // App info
        // =========================================================================
        public const string AppTitle = "coppercli";
        public const string AppVersion = "v0.3.0";

        // =========================================================================
        // File extensions
        // =========================================================================
        public static readonly string[] GCodeExtensions = { ".nc", ".gcode", ".ngc", ".gc", ".tap", ".cnc" };
        public static readonly string[] ProbeGridExtensions = { ".pgrid" };

        // =========================================================================
        // Unix serial port patterns for CNC devices
        // =========================================================================
        public static readonly string[] UnixSerialPortPatterns =
            { "ttyUSB*", "ttyACM*", "tty.usbserial*", "cu.usbmodem*", "tty.usbmodem*" };

        // =========================================================================
        // Date/time formats
        // =========================================================================
        public const string ProbeDateFormat = "yyyy-MM-dd-HH-mm";

        // =========================================================================
        // Probing defaults
        // =========================================================================
        public const double DefaultProbeMargin = 0.5;
        public const double DefaultProbeGridSize = 5.0;

        // =========================================================================
        // Position and movement
        // =========================================================================
        public const double PositionToleranceMm = 0.1;
        public const double RetractZMm = 6.0;  // Clearance height to lift tool away from workpiece
        public const double ReferenceZHeightMm = 1.0;

        // =========================================================================
        // Jog modes: vi-style with digit prefix for multiplier
        // In Fast mode: 1-5 multiplier (10-50mm)
        // In other modes: 1-9 multiplier
        // =========================================================================
        public record JogMode(
            string Name,
            double Feed,         // mm/min
            double BaseDistance, // mm per unit
            int MaxMultiplier    // 5 for Fast, 9 for others
        )
        {
            public string FormatDistance(int multiplier) =>
                BaseDistance >= 1 ? $"{BaseDistance * multiplier:F0}mm"
                                  : $"{BaseDistance * multiplier:G}mm";
        }

        public static readonly JogMode[] JogModes =
        {
            new("Fast",   5000, 10.0,  5),  // 1-5: 10, 20, 30, 40, 50mm
            new("Normal",  500,  1.0,  9),  // 1-9: 1, 2, 3... 9mm
            new("Slow",     50,  0.1,  9),  // 1-9: 0.1, 0.2... 0.9mm
            new("Creep",     5,  0.01, 9),  // 1-9: 0.01, 0.02... 0.09mm
        };

        // =========================================================================
        // Mill progress display
        // =========================================================================

        // Grid sizing: Terminal characters are roughly 2:1 (taller than wide), so we use
        // 2 characters per cell horizontally to approximate square cells on screen.
        public const int MillGridCharsPerCell = 2;
        public const int MillGridMaxWidth = 50;      // Max cells wide (100 chars)
        public const int MillGridMaxHeight = 20;     // Max cells tall
        public const int MillGridMinWidth = 10;      // Min available width to show grid (5 cells)
        public const int MillGridMinHeight = 3;      // Min available height to show grid (3 rows)

        // Terminal padding: Reserve space for header (~9 lines) + grid borders/labels (~3 lines)
        public const int MillTermWidthPadding = 4;   // Left/right margins
        public const int MillTermHeightPadding = 12; // Header + borders + bounds line
        public const int MillBorderPadding = 2;      // Box border characters

        // Progress bar
        public const int MillProgressBarWidth = 30;

        // ETA calculation
        public const int MillMinLinesForEta = 10;
        public const double MillMinSecondsForEta = 1.0;

        // Z values below this threshold (relative to work zero) indicate the tool is cutting.
        // Used to mark cells as "visited" in the milling progress grid display.
        public const double MillCuttingDepthThreshold = 0.1;

        // Minimum range for coordinate mapping
        public const double MillMinRangeThreshold = 0.001;

        // Visual markers for the 2D grid display
        public const string MillCurrentPosMarker = "● ";
        public const string MillVisitedMarker = "░░";
        public const string MillEmptyMarker = "··";

        // Overlay messages
        public const string OverlayHoldMessage = "HOLD - Press R to resume";
        public const string OverlayAlarmMessage = "ALARM - Press X to stop";

        // =========================================================================
        // Probe matrix display sizing
        // =========================================================================
        public const int ProbeGridConsolePadding = 4;
        public const int ProbeGridMaxDisplayWidth = 50;
        public const int ProbeGridMaxDisplayHeight = 20;
        public const int ProbeGridHeaderPadding = 8;

        // =========================================================================
        // Proxy (see also coppercli.Core.Util.Constants for shared proxy constants)
        // =========================================================================
        public const int ProxyDefaultPort = 34000;
        public const int ProxyStatusUpdateIntervalMs = 500;
        public const string ProxyNoClientStatus = "No client connected";
        public const string ProxyListeningStatus = "Listening...";

        // =========================================================================
        // Network scanning (for Ethernet auto-detect)
        // =========================================================================
        public const int NetworkScanTimeoutMs = 200;
        public const int NetworkScanParallelism = 100;

        // =========================================================================
        // Macro system
        // =========================================================================
        public const string MacroExtension = ".cmacro";
        public const char MacroCommentChar = '#';
        public const int MacroParseDisplayMs = 500;   // Brief pause to show parsed command count

        // =========================================================================
        // Tool change
        // =========================================================================
        public const string ToolChangeLabel = "TOOL CHANGE";
        public const string ToolChangeStatusRaisingZ = "Raising Z...";
        public const string ToolChangeStatusMovingToSetter = "Moving to tool setter...";
        public const string ToolChangeStatusMeasuringRef = "Measuring reference tool...";
        public const string ToolChangeStatusMeasuringNew = "Measuring new tool...";
        public const string ToolChangeStatusAdjustingZ = "Adjusting Z origin...";
        public const string ToolChangeStatusReturning = "Returning to work position...";
        public const string ToolChangeStatusComplete = "Tool change complete";
        public const string ToolChangeStatusMovingToWork = "Moving to work area...";
        public const string ToolChangeStatusProbingZ = "Probing Z...";
        public const string ToolChangeStatusZeroing = "Z zeroed, raising...";
        public const string ToolChangePromptWithSetter = "Change to new tool, then press P.";
        public const string ToolChangePromptWithoutSetter = "Change tool. Attach probe clip. Press P to probe.";
        public const double ToolSetterProbeDepth = 50.0;    // Max depth to probe tool setter (mm)
        public const double ToolSetterSeekFeed = 500.0;   // Fast seek feed rate (mm/min)
        public const double ToolSetterProbeFeed = 50.0;   // Slow precise probe feed rate (mm/min)
        public const double ToolSetterRetract = 10.0;     // Retract distance between probes (mm)
        public const double ToolSetterApproachClearance = 20.0;  // Clearance above last known position for rapid approach (mm)
        public const double ToolChangeClearanceZ = -5.0;  // Machine Z for tool change (-5mm from top to avoid limit switch)
    }
}
