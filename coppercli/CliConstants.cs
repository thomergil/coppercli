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
        public const string AppVersion = "v0.5.0-pre";

        // =========================================================================
        // Timing: CLI-specific
        // Core timing constants are in coppercli.Core/Util/Constants.cs
        // =========================================================================

        /// <summary>Timeout for establishing serial/network connection (ms).</summary>
        public const int ConnectionTimeoutMs = 5000;

        /// <summary>Timeout waiting for GRBL response to a command (ms).</summary>
        public const int GrblResponseTimeoutMs = 3000;

        /// <summary>Timeout for auto-detecting serial ports during connection (ms).</summary>
        public const int AutoDetectTimeoutMs = 2000;

        /// <summary>Delay after force-disconnecting web clients before reconnecting (ms).</summary>
        public const int ForceDisconnectDelayMs = 500;

        /// <summary>Timeout for force-disconnect API call to remote server (ms).</summary>
        public const int ForceDisconnectApiTimeoutMs = 5000;

        /// <summary>Interval for polling during jog operations - faster for responsiveness (ms).</summary>
        public const int JogPollIntervalMs = 50;

        /// <summary>Duration to display confirmation messages before continuing (ms).</summary>
        public const int ConfirmationDisplayMs = 1000;

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
        public const string BaudMenuTitle = "Select baud rate:";

        /// <summary>
        /// The last entry of the baud menu. Escape returns the last index, so a menu without
        /// one answers a cancel by picking whatever sits at the end of the list.
        /// </summary>
        public const string BaudMenuKeepCurrent = "Keep the current rate";

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
        public const string ProbeAutoSaveFileName = "probe_autosave" + ProbeGridExtension;

        /// <summary>Recognized G-code file extensions for file dialogs and filtering.</summary>
        public static readonly string[] GCodeExtensions = { ".nc", ".gcode", ".ngc", ".gc", ".tap", ".cnc" };

        /// <summary>What a saved height map is called on disk.</summary>
        public const string ProbeGridExtension = ".pgrid";

        /// <summary>Every extension the height map browser lists.</summary>
        public static readonly string[] ProbeGridExtensions = { ProbeGridExtension };

        /// <summary>Macro file extension.</summary>
        public const string MacroExtension = ".cmacro";

        /// <summary>Comment character in macro files.</summary>
        public const char MacroCommentChar = '#';

        /// <summary>Date format for auto-generated probe file names (yyyy-MM-dd-HH-mm).</summary>
        public const string ProbeDateFormat = "yyyy-MM-dd-HH-mm";

        // =========================================================================
        // Position and movement: Z heights (work coordinates)
        // =========================================================================

        /// <summary>
        /// Reference Z height above workpiece for measurements (mm, work coordinates).
        /// Used as starting point for probe operations.
        /// </summary>
        public const double ReferenceZHeightMm = 1.0;

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

        /// <summary>The mode a session starts in, and the one a jog falls back to.</summary>
        public const int DefaultJogModeIndex = 1;  // Normal

        // =========================================================================
        // Probing defaults
        // =========================================================================

        /// <summary>Default margin around workpiece for probe grid (mm).</summary>
        public const double DefaultProbeMargin = 0.5;

        /// <summary>Default grid cell size for probe grid (mm).</summary>
        public const double DefaultProbeGridSize = 5.0;

        // =========================================================================
        // Depth adjustment (re-milling)
        // =========================================================================

        /// <summary>Increment for depth adjustment per keypress (mm).</summary>
        public const double DepthAdjustmentIncrement = 0.02;

        /// <summary>Maximum depth adjustment in either direction (mm).</summary>
        public const double DepthAdjustmentMax = 1.0;

        // =========================================================================
        // Mill progress display: Grid sizing
        // Terminal characters are ~2:1 aspect ratio (taller than wide), so we use
        // 2 characters per cell horizontally to approximate square cells.
        // =========================================================================

        /// <summary>Characters per cell horizontally (2 for square appearance).</summary>
        public const int MillGridCharsPerCell = 2;

        /// <summary>Minimum terminal width (in cells) required to show grid.</summary>
        public const int MillGridMinWidth = 10;

        /// <summary>Minimum terminal height (in cells) required to show grid.</summary>
        public const int MillGridMinHeight = 3;

        /// <summary>Default max grid width for Web UI (used as fallback if client doesn't specify).</summary>
        public const int WebMillGridDefaultWidth = 50;

        /// <summary>Default max grid height for Web UI (used as fallback if client doesn't specify).</summary>
        public const int WebMillGridDefaultHeight = 20;

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

        /// <summary>Padding around progress bar line (indent + percentage display).</summary>
        public const int MillProgressLinePadding = 15;

        /// <summary>Horizontal padding for milling grid area (left + right margin).</summary>
        public const int MillGridHorizontalPadding = 4;

        // =========================================================================
        // Mill progress display: ETA calculation
        // =========================================================================

        /// <summary>Shown for the ETA before milling has begun and a figure exists.</summary>
        public const string EtaUnknown = "--:--:--";

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
        public const string SafetyChecklistMessage = "Probing equipment removed?";

        /// <summary>Combined safety/depth sub-message with key instructions.</summary>
        public const string SafetyDepthSubMessage = "↑/↓=Depth  Y=Start  Esc=Cancel";

        /// <summary>Overlay message when machine is in alarm state.</summary>
        public const string OverlayAlarmMessage = "ALARM - Press X to stop";

        /// <summary>Warning message when no machine profile is selected.</summary>
        public const string NoMachineProfileWarning = "No machine profile selected";

        /// <summary>Key hint under a prompt the operator answers with Y or Escape.</summary>
        public const string ContinueOrCancelKeyHint = "Y=Continue  Esc=Cancel";

        /// <summary>Key hint under the Z-zero prompt, where jogging is offered as well.</summary>
        public const string JogContinueOrCancelKeyHint = "J=Jog  " + ContinueOrCancelKeyHint;

        /// <summary>Warning message when sleep prevention unavailable in network mode.</summary>
        public const string SleepPreventionWarning = "Sleep prevention unavailable";

        /// <summary>Shown while GRBL reports the enclosure open.</summary>
        public const string DoorOpenMessage = "DOOR OPEN";

        /// <summary>Shown once the door is closed and the machine is waiting to be resumed.</summary>
        public const string DoorClosedMessage = "DOOR CLOSED - machine holding";

        /// <summary>Mill screen status while GRBL moves the tool back after a resume.</summary>
        public const string DoorResumingStatus = "DOOR CLOSED - machine resuming";

        /// <summary>Shown while the machine is alarmed.</summary>
        public const string MillAlarmStatus = "ALARM";

        /// <summary>Shown while the machine is asleep ($SLP).</summary>
        public const string MillSleepStatus = "ASLEEP - reset to wake";

        /// <summary>Mill screen status while the run itself is paused.</summary>
        public const string MillPausedStatus = "PAUSED";

        /// <summary>Confirmed on the jog screen after zeroing.</summary>
        public const string ZeroedZ = "Z zeroed";

        /// <inheritdoc cref="ZeroedZ"/>
        public const string ZeroedAllAxes = "All axes zeroed";

        /// <summary>Added to a zero confirmation to say what became of the height map.</summary>
        public const string ZeroedMapReapplied = "{0} - height map re-applied";

        /// <inheritdoc cref="ZeroedMapReapplied"/>
        public const string ZeroedMapDiscarded = "{0} - height map discarded";

        /// <inheritdoc cref="ZeroedMapReapplied"/>
        public const string ZeroedFileLeftAlone = "{0} - height map kept for this run";

        /// <inheritdoc cref="ZeroedMapReapplied"/>
        public const string ZeroedMapNotReapplied =
            "{0} - height map was not re-applied. Reload the file before milling.";

        /// <inheritdoc cref="ZeroedMapReapplied"/>
        public const string ZeroedMapNotDiscarded =
            "{0} - height map was not removed. Reload the file before milling.";

        /// <summary>A macro stopped because the height map no longer matches the origin.</summary>
        public const string MacroErrorHeightMapWrong =
            "Height map no longer matches the origin. Reload the file.";

        /// <summary>Confirmed on the jog screen after an unlock ($X).</summary>
        public const string UnlockedMessage = "Unlocked";

        /// <summary>Confirmed on the jog screen after a soft reset.</summary>
        public const string ResetMessage = "Reset";

        /// <summary>Status shown once an operator asks to abort a moving tool change.</summary>
        public const string ToolChangeAbortingMessage = "Stopping the tool change...";

        /// <summary>Key hint under a full-screen overlay: Escape stops the run.</summary>
        public const string StopKeyHint = "Esc=Stop";

        /// <summary>
        /// Appended to the tool-change status while the machine is moving on its own. Escape
        /// works throughout, so the operator watching it move is told how to stop it.
        /// </summary>
        public const string ToolChangeAbortHint = "{0}  (" + StopKeyHint + ")";

        /// <summary>
        /// Shown when a job does not finish stopping in the time allowed. Used by both the
        /// terminal and the browser.
        /// </summary>
        public const string StopTimedOutWarning =
            "Stop did not finish. The machine may still be moving - check it.";

        /// <summary>Sub-message for sleep prevention warning.</summary>
        public const string SleepPreventionSubMessage = "System may sleep during job. Y=Continue  Esc=Cancel";

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

        /// <summary>Default TCP port for web server.</summary>
        public const int WebDefaultPort = 34001;

        /// <summary>Default subnet mask for Ethernet device scan (class C network).</summary>
        public const int NetworkScanDefaultMask = 24;

        /// <summary>Minimum subnet mask for Ethernet device scan (class B network).</summary>
        public const int NetworkScanMinMask = 16;

        /// <summary>Delay after mill stop before raising Z to safe height (ms).</summary>
        public const int MillStopDelayMs = 500;

        /// <summary>Interval for updating proxy status display (ms).</summary>
        public const int ProxyStatusUpdateIntervalMs = 500;

        /// <summary>Status text indicating none/empty.</summary>
        public const string StatusNone = "None";

        // =========================================================================
        // Network scanning (for GRBL auto-detection)
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

        // =========================================================================
        // File browser UI
        // =========================================================================

        /// <summary>Title for file selection dialog.</summary>
        public const string FileBrowserSelectTitle = "Select File";

        /// <summary>Title for file save dialog.</summary>
        public const string FileBrowserSaveTitle = "Save File";

        /// <summary>Label for filename input in save mode.</summary>
        public const string FileBrowserFilenameLabel = "Filename: ";

        /// <summary>Label for directory display/input.</summary>
        public const string FileBrowserDirectoryLabel = "Directory";

        /// <summary>Error when directory not found.</summary>
        public const string FileBrowserErrorDirNotFound = "Directory not found: {0}";

        /// <summary>Menu item: Save.</summary>
        public const string FileBrowserMenuSave = "Save";

        /// <summary>Menu item: Change filename.</summary>
        public const string FileBrowserMenuChangeName = "Change filename";

        /// <summary>Menu item: Change directory.</summary>
        public const string FileBrowserMenuChangeDir = "Change directory";

        /// <summary>Menu item: Cancel.</summary>
        public const string MenuCancel = "Cancel";

        /// <summary>File browser: select current directory option.</summary>
        public const string FileBrowserSelectDir = "[Select this directory]";

        /// <summary>Help text for file browser in select mode.</summary>
        public const string FileBrowserHelpSelect = "↑↓ navigate, 1-9 select, Enter select, / filter, Esc cancel";

        /// <summary>Help text for file browser when filter is active.</summary>
        public const string FileBrowserHelpFilter = "↑↓ navigate, 1-9 select, Enter select, type to filter, Esc clear";

        /// <summary>Help text for file browser in save mode.</summary>
        public const string FileBrowserHelpSave = "↑↓ navigate, 1-9 select, n edit name, Enter save, Esc cancel";

        /// <summary>Help text for file browser when editing filename.</summary>
        public const string FileBrowserHelpEditName = "Type filename, Enter save, Esc cancel";

        /// <summary>Width allocated for filename column in file browser.</summary>
        public const int FileBrowserNameColumnWidth = 30;

        /// <summary>Maximum number of file load warnings to display before truncating.</summary>
        public const int MaxFileLoadWarningsShown = 5;

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

        // =========================================================================
        // Probe menu: Labels and options
        // =========================================================================

        /// <summary>Menu header for probe options.</summary>
        public const string ProbeMenuHeader = "Probe options:";

        /// <summary>Menu item: Continue probing an incomplete grid.</summary>
        public const string ProbeMenuContinue = "Continue Probing";

        /// <summary>Menu item: Discard partial/unsaved probe data.</summary>
        public const string ProbeMenuDiscard = "Discard Probe Data";

        /// <summary>Menu item: Clear probe data (from saved file).</summary>

        /// <summary>Menu item: Discard existing data and start fresh.</summary>
        public const string ProbeMenuDiscardAndStart = "Discard and Start Probing";

        /// <summary>Menu item: Start a new probing session.</summary>
        public const string ProbeMenuStart = "Start Probing";

        /// <summary>Menu item: Load probe data from file.</summary>
        public const string ProbeMenuLoad = "Load from File";

        /// <summary>Menu item: Recover probe data from autosave.</summary>
        public const string ProbeMenuRecover = "Recover from Autosave";

        /// <summary>Menu item: Save probe data to file.</summary>
        public const string ProbeMenuSave = "Save to File";

        /// <summary>Menu item: Save unsaved probe data (shown prominently).</summary>
        public const string ProbeMenuSaveUnsaved = "Save Probe Data (unsaved)";

        /// <summary>Menu item: Apply probe data to G-code.</summary>
        public const string ProbeMenuApply = "Apply to G-Code";

        /// <summary>Menu item: Return to previous menu.</summary>
        public const string ProbeMenuBack = "Back";

        // =========================================================================
        // Generic disabled reasons (shared across menus)
        // =========================================================================

        /// <summary>Generic reason: not connected.</summary>
        public const string DisabledConnect = "connect first";

        /// <summary>Error message: not connected (for error dialogs).</summary>
        public const string ErrorNotConnected = "Not connected!";

        /// <summary>Generic reason: no G-code file loaded.</summary>
        public const string DisabledNoFile = "load G-Code first";

        /// <summary>Generic reason: must disconnect first.</summary>
        public const string DisabledDisconnect = "disconnect first";

        /// <summary>Generic reason: work zero not set.</summary>
        public const string DisabledNoZero = "set work zero first";

        /// <summary>Generic reason: probe data not applied.</summary>
        public const string DisabledProbeSetupChanged = "height map is for a different file or origin";
        public const string DisabledProbeNotApplied = "apply probe data first";

        /// <summary>Generic reason: probe data incomplete.</summary>
        public const string DisabledProbeIncomplete = "probe incomplete ({0})";

        /// <summary>Generic reason: alarm state must be cleared.</summary>
        public const string DisabledAlarm = "clear alarm first";

        /// <summary>Menu reason while a run owns the machine and the file it is streaming.</summary>
        public const string DisabledRunInProgress = "a job is running";

        /// <summary>Menu reason while the machine is asleep ($SLP).</summary>
        public const string DisabledAsleep = "reset the machine first";

        /// <summary>Generic error: unknown validation error.</summary>
        public const string DisabledUnknown = "unknown error";

        /// <summary>The autosave could not be deleted, so the map is still on disk.</summary>
        public const string ErrorAutosaveNotDeleted = "Could not delete the saved height map.";

        /// <summary>Confirmed after a height map is applied to the loaded file.</summary>
        public const string HeightMapApplied = "Height map applied.";

        /// <summary>
        /// The map is applied to the loaded G-code and the original file it was applied to is
        /// gone, so the corrections cannot be taken back out.
        /// </summary>
        public const string ErrorMapStuckInGCode =
            "The height map is in the loaded file and the original is gone. Load a file.";

        /// <summary>No finished height map is in hand to apply.</summary>
        public const string ErrorNoCompleteMapToApply = "No finished height map to apply.";

        /// <summary>
        /// A run tracks its place in the file by line number, so replacing the file reopens
        /// the program at the start.
        /// </summary>
        public const string ErrorFileChangeDuringRun = "The job is using this file. Stop it first.";

        /// <summary>
        /// Re-zeroing X or Y moves the origin, so the rest of the job cuts in the wrong
        /// place. Z is the one axis a paused run can take, because a tool change asks for it.
        /// </summary>
        public const string ErrorZeroXYDuringRun = "Stop the job before re-zeroing X or Y.";

        /// <summary>Shown while connecting, when the machine is neither at the door nor alarmed but still will not clear.</summary>
        public const string ErrorMachineWillNotClear = "Machine not ready. Reset it, then try again.";

        /// <summary>Shown while connecting, when the alarm is still set after several tries.</summary>
        public const string ErrorAlarmWillNotClear = "Still in alarm. Clear it, then unlock.";

        // =========================================================================
        // Probe menu: Status messages
        // =========================================================================

        /// <summary>Warning: probe data not applied to G-code.</summary>
        public const string ProbeStatusNotApplied = "* Probe data not yet applied to G-Code";

        /// <summary>Status: probe data has been applied.</summary>
        public const string ProbeStatusApplied = "Probe data applied to G-Code";

        /// <summary>Status: incomplete probe data found from autosave.</summary>
        public const string ProbeStatusIncomplete = "Incomplete probe data found (autosaved)";

        /// <summary>Status: no probe data available.</summary>
        public const string ProbeStatusNoData = "No probe data";

        /// <summary>Status: no G-code file loaded (required for probing).</summary>
        public const string ProbeStatusNoFile = "No G-Code file loaded (required for probing)";

        /// <summary>Status: work zero not set (required for probing).</summary>
        public const string ProbeStatusNoZero = "Work zero not set (required for probing)";

        /// <summary>Status: probe data has been cleared.</summary>
        public const string ProbeStatusCleared = "Probe data cleared";

        /// <summary>Prompt: clear probe data after milling.</summary>
        public const string ProbePromptClear = "Clear probe data?";

        /// <summary>Status: probe data loaded from file.</summary>
        public const string ProbeStatusLoaded = "Probe data loaded";

        /// <summary>Status: probe data applied with exclamation.</summary>
        public const string ProbeStatusAppliedSuccess = "Probe data applied to G-Code!";

        /// <summary>Status: probe data is complete (no more points to probe).</summary>
        public const string ProbeStatusComplete = "Probe data is already complete.";

        /// <summary>
        /// Shown when a macro asks for a probe while one is already running. They share one
        /// controller, so the second would overwrite the first's settings.
        /// </summary>
        public const string ProbeErrorAlreadyRunning =
            "Probing is already running. Wait for it to finish, then try again.";

        /// <summary>Status: probing has started.</summary>
        public const string ProbeStatusStarted = "Probing started. Space=Pause, Escape=Stop";

        /// <summary>Status: probing paused.</summary>
        public const string ProbeStatusPaused = "PAUSED - Space=Resume, Escape=Stop";

        /// <summary>Status: probing resumed.</summary>
        public const string ProbeStatusResumed = "Resumed";

        /// <summary>Status: stopping in progress.</summary>
        public const string ProbeStatusStopping = "Stopping...";

        /// <summary>Status: probing stopped by user.</summary>
        public const string ProbeStatusStopped = "Probing stopped by user";

        /// <summary>Status: probing complete.</summary>
        public const string ProbeStatusCompleteSuccess = "Probing complete!";

        /// <summary>Status: probe data not saved (user cancelled).</summary>
        public const string ProbeStatusNotSaved = "Probe data not saved";

        /// <summary>Status: completed probe data has not been saved.</summary>
        public const string ProbeStatusUnsaved = "* Probe data complete but not saved";

        // =========================================================================
        // Probe menu: Error messages
        // =========================================================================


        /// <summary>Error: probe data is not complete.</summary>
        public const string ProbeErrorIncomplete = "Probe data not complete";


        /// <summary>Shown when the autosaved map was measured for another job.</summary>
        public const string ProbeAutosaveNotApplicable =
            "The saved height map was measured for a different file or work origin.";

        /// <summary>Error: no autosaved probe data available.</summary>
        public const string ProbeErrorNoAutosave =
            "There is no saved height map to recover. Probe the board first.";

        /// <summary>How a height map is described in the warning before an X or Y zero.</summary>
        public const string PartlyMeasuredMap = "a partly measured";

        /// <inheritdoc cref="PartlyMeasuredMap"/>
        public const string CompleteMap = "a complete";

        /// <inheritdoc cref="PartlyMeasuredMap"/>
        public const string UnmeasuredMap = "an unmeasured";

        /// <summary>
        /// What zeroing X or Y costs. {0} describes the map, {1} names the axes. Published
        /// through /api/constants, so the browser shows the same words.
        /// </summary>
        public const string ZeroDiscardsMap =
            "You have {0} height map for this board, measured from the current X/Y origin. "
            + "Zeroing {1} deletes it and the saved copy, and you will have to probe again. "
            + "Zeroing only Z keeps it. Continue?";

        /// <summary>Shown when settings could not be written to disk.</summary>
        public const string SettingsNotSaved =
            "Could not save your settings. They will be back to their old values next time "
            + "coppercli starts.";

        /// <summary>Shown when the file a previous session had open will not load.</summary>
        public const string ErrorFileNotLoaded = "The file from your last session would not load.";

        /// <summary>Success: recovered probe data from autosave.</summary>
        public const string ProbeStatusRecovered = "Recovered {0}/{1} points from autosave";

        /// <summary>Error: work zero not set with instructions.</summary>
        public const string ProbeErrorNoZero = "Work zero not set. Use Move menu to zero all axes (0) first.";

        /// <summary>Error: no incomplete probe data to resume.</summary>
        public const string ProbeErrorNoIncomplete = "No incomplete probe data found.";


        /// <summary>Error: no complete probe data to save.</summary>
        public const string ProbeErrorNoComplete = "No complete probe data to save.";

        // =========================================================================
        // Probe menu: Prompts
        // =========================================================================

        /// <summary>Prompt: enter probe margin.</summary>
        public const string ProbePromptMargin = "Probe margin (mm)";

        /// <summary>Prompt: enter grid size.</summary>
        public const string ProbePromptGridSize = "Grid size (mm)";

        /// <summary>Prompt: trace outline first?</summary>
        public const string ProbePromptTraceOutline = "Trace outline first?";

        /// <summary>Prompt: apply probe data to G-code?</summary>
        public const string ProbePromptApply = "Apply probe data to G-Code?";

        /// <summary>Prompt: proceed to milling?</summary>
        public const string ProbePromptMill = "Proceed to Milling?";

        /// <summary>Prompt: save probe data.</summary>

        // =========================================================================
        // Probe menu: Display labels
        // =========================================================================

        /// <summary>Display header prefix for probing progress.</summary>
        public const string ProbeDisplayHeader = "Probing:";

        /// <summary>Display instruction to stop probing.</summary>
        public const string ProbeDisplayEscapeStop = "Press Escape to stop";

        /// <summary>Display label for Z range when no data.</summary>
        public const string ProbeDisplayZNoData = "Z: --";

        /// <summary>Display label for points count.</summary>
        public const string ProbeDisplayPoints = "Points:";

        /// <summary>Display label for Z range.</summary>
        public const string ProbeDisplayZRange = "Z range:";

        /// <summary>Display label for variance.</summary>
        public const string ProbeDisplayVariance = "Variance:";

        /// <summary>Display suffix for millimeters.</summary>
        public const string ProbeDisplayMm = "mm";

        // =========================================================================
        // Probe menu: Format strings (use with string.Format or interpolation)
        // =========================================================================

        /// <summary>Format: resuming probe progress ({0}/{1} points complete).</summary>
        public const string ProbeFormatResume = "Resuming probe: {0}/{1} points complete";

        /// <summary>Format: error loading probe data ({0} = error message).</summary>
        public const string ProbeFormatLoadError = "Error loading probe data: {0}";

        /// <summary>Format: error creating probe grid ({0} = error message).</summary>
        /// <summary>
        /// Shown in place of a caught exception. Its text names files, offsets and types the
        /// operator cannot act on; it goes to the log instead. {0} is one of the Failed*
        /// constants below.
        /// </summary>
        public const string ErrorSomethingFailed =
            "{0} failed. Try again; if it keeps happening, restart coppercli with --debug and "
            + "keep the coppercli.log file it writes.";

        /// <summary>What was being done when it failed, as the operator asked for it.</summary>
        public const string FailedConnecting = "Connecting";

        /// <inheritdoc cref="FailedConnecting"/>
        public const string FailedLoadingTheFile = "Loading the file";

        /// <inheritdoc cref="FailedConnecting"/>
        public const string FailedLoadingTheHeightMap = "Loading the height map";

        /// <inheritdoc cref="FailedConnecting"/>
        public const string FailedProbing = "Probing";

        /// <inheritdoc cref="FailedConnecting"/>
        public const string FailedSettingUpTheGrid = "Setting up the grid";

        /// <inheritdoc cref="FailedConnecting"/>
        public const string FailedReadingTheMacro = "Reading the macro";

        /// <inheritdoc cref="FailedConnecting"/>
        public const string FailedRunningTheMacro = "Running the macro";

        /// <inheritdoc cref="FailedConnecting"/>
        public const string FailedStartingTheProxy = "Starting the proxy";

        /// <inheritdoc cref="FailedConnecting"/>
        public const string FailedStartingTheWebServer = "Starting the web server";

        /// <inheritdoc cref="FailedConnecting"/>
        public const string FailedListeningOnTheNetwork = "Listening on the network";

        /// <summary>Format: probing error ({0} = error message).</summary>

        /// <summary>Format: probe grid dimensions ({0}x{1} = {2} points).</summary>
        public const string ProbeFormatGrid = "Probe grid: {0}x{1} = {2} points";

        /// <summary>Format: probe bounds (X({0:F2} to {1:F2}) Y({2:F2} to {3:F2})).</summary>
        public const string ProbeFormatBounds = "Bounds: X({0:F2} to {1:F2}) Y({2:F2} to {3:F2})";

        /// <summary>Format: probe data saved to file ({0} = path).</summary>
        public const string ProbeFormatSaved = "Probe data saved to {0}";

        /// <summary>Format: error saving probe data ({0} = error message).</summary>
        public const string ProbeFormatSaveError = "Error saving: {0}";

        /// <summary>Format: overwrite confirmation ({0} = filename).</summary>
        public const string ProbeFormatOverwrite = "Overwrite {0}?";
    }
}
