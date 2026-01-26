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
        public const string AppVersion = "v0.1.1";

        // =========================================================================
        // File extensions
        // =========================================================================
        public static readonly string[] GCodeExtensions = { ".nc", ".gcode", ".ngc", ".gc", ".tap", ".cnc" };
        public static readonly string[] ProbeGridExtensions = { ".pgrid" };

        // =========================================================================
        // Probing defaults
        // =========================================================================
        public const double DefaultProbeMargin = 0.5;
        public const double DefaultProbeGridSize = 5.0;

        // =========================================================================
        // Position and movement
        // =========================================================================
        public const double PositionToleranceMm = 0.1;
        public const double SafeZHeightMm = 6.0;
        public const double ReferenceZHeightMm = 1.0;

        // =========================================================================
        // Jog presets: (feed mm/min, distance mm, label)
        // =========================================================================
        public static readonly (double Feed, double Distance, string Label)[] JogPresets =
        {
            (5000, 10.0,  "Fast   5000mm/min  10mm"),
            (500,  1.0,   "Normal  500mm/min   1mm"),
            (50,   0.1,   "Slow     50mm/min 0.1mm"),
            (5,    0.01,  "Creep     5mm/min 0.01mm"),
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

        // Z threshold for marking cells as "visited"
        public const double MillCuttingDepthThreshold = 0.1;

        // Minimum range for coordinate mapping
        public const double MillMinRangeThreshold = 0.001;

        // Visual markers for the 2D grid display
        public const string MillCurrentPosMarker = "● ";
        public const string MillVisitedMarker = "░░";
        public const string MillEmptyMarker = "··";

    }
}
