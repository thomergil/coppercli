namespace coppercli
{
    /// <summary>
    /// Constants shared with coppercli.Core belong in coppercli.Core/Util/Constants.cs, and
    /// GRBL protocol constants in coppercli.Core/Util/GrblProtocol.cs.
    /// </summary>
    internal static class CliConstants
    {

        public const string AppTitle = "coppercli";

        /// <summary>The 'v' prefix is for display; installer/coppercli.iss carries the same version without it.</summary>
        public const string AppVersion = "v0.5.2";

        public const int ConnectionTimeoutMs = 5000;

        public const int GrblResponseTimeoutMs = 3000;

        public const int AutoDetectTimeoutMs = 2000;

        public const int TakeoverDelayMs = 500;

        public const int TakeoverApiTimeoutMs = 5000;

        public const string ServerHasTheMachine = "The coppercli server has the machine.";
        public const string TakeOverFromServerQuestion = "Take the machine over from the server?";
        public const string ServerNotReached = "Could not reach the coppercli server to take the machine over.";
        public const string WebServerStoppedItself = "The web server stopped unexpectedly, so server mode has ended.";

        /// <summary>Shorter than the normal status poll, so jogging keeps up with the keyboard.</summary>
        public const int JogPollIntervalMs = 50;

        public const int ConfirmationDisplayMs = 1000;

        /// <summary>How long "Settings saved" stays up before the settings screen redraws.</summary>
        public const int SettingsSavedDisplayMs = 500;

        /// <summary>
        /// Ordered by likelihood. 115200 is the GRBL v0.9+ default, 250000 appears in high-speed
        /// configurations, and 9600 in older GRBL and some Bluetooth modules.
        /// </summary>
        public static readonly int[] CommonBaudRates = { 115200, 250000, 9600, 57600, 38400, 19200 };
        public const string BaudMenuTitle = "Select baud rate:";

        /// <summary>
        /// The last entry of the baud menu. Escape returns the last index, so without this entry a
        /// cancel would pick whichever rate sits at the end of the list.
        /// </summary>
        public const string BaudMenuKeepCurrent = "Keep the current rate";

        /// <summary>Globs matched when auto-detecting a port on macOS and Linux.</summary>
        public static readonly string[] UnixSerialPortPatterns =
            { "ttyUSB*", "ttyACM*", "tty.usbserial*", "cu.usbmodem*", "tty.usbmodem*" };

        // Settings, session and probe autosave are written to the application directory.
        public const string SettingsFileName = "settings.json";

        public const string SessionFileName = "session.json";

        public const string ProbeAutoSaveFileName = "probe_autosave" + ProbeGridExtension;

        public static readonly string[] GCodeExtensions = { ".nc", ".gcode", ".ngc", ".gc", ".tap", ".cnc" };

        public const string ProbeGridExtension = ".pgrid";

        public static readonly string[] ProbeGridExtensions = { ProbeGridExtension };

        public const string MacroExtension = ".cmacro";

        public const char MacroCommentChar = '#';

        public const string ProbeDateFormat = "yyyy-MM-dd-HH-mm";

        /// <summary>
        /// Work coordinates, not machine. Probe moves start from this height above the workpiece.
        /// </summary>
        public const double ReferenceZHeightMm = 1.0;

        /// <summary>Shortcut keys run 1-9, then 0, then A-Z.</summary>
        public const int MaxMenuShortcuts = 36;

        public const int MenuShortcutAlphaStart = 10;

        public const int MenuShortcutZeroIndex = 9;

        /// <summary>
        /// Feed is mm/min and BaseDistance is mm. A digit key multiplies BaseDistance, up to
        /// MaxMultiplier.
        /// </summary>
        public record JogMode(string Name, double Feed, double BaseDistance, int MaxMultiplier)
        {
            public string FormatDistance(int multiplier) =>
                BaseDistance >= 1 ? $"{BaseDistance * multiplier:F0}mm"
                                  : $"{BaseDistance * multiplier:G}mm";
        }

        /// <summary>
        /// A digit key pressed before the jog key sets the multiplier, default 1. Fast takes fewer
        /// multipliers than the other modes so one keypress cannot ask for a long move.
        /// </summary>
        public static readonly JogMode[] JogModes =
        {
            new("Fast",   5000, 10.0,  5),
            new("Normal",  500,  1.0,  9),
            new("Slow",     50,  0.1,  9),
            new("Creep",     5,  0.01, 9),
        };

        public const int DefaultJogModeIndex = 1;

        /// <summary>Millimeters.</summary>
        public const double DefaultProbeMargin = 0.5;

        /// <summary>Millimeters.</summary>
        public const double DefaultProbeGridSize = 5.0;

        /// <summary>Millimeters per step: one ↑/↓ key in the terminal, one -/+ button in the browser.</summary>
        public const double DepthAdjustmentIncrement = 0.02;

        /// <summary>
        /// Decimal places a depth adjustment is rounded to, so steps that add back to zero
        /// leave exactly zero rather than a floating-point remainder.
        /// </summary>
        public const int DepthAdjustmentDecimals = 3;

        /// <summary>Millimeters, in either direction.</summary>
        public const double DepthAdjustmentMax = 1.0;

        /// <summary>
        /// A terminal cell is about twice as tall as it is wide, so two characters draw a square
        /// cell.
        /// </summary>
        public const int MillGridCharsPerCell = 2;

        /// <summary>Cells.</summary>
        public const int MillGridMinWidth = 10;

        /// <summary>Cells.</summary>
        public const int MillGridMinHeight = 3;

        /// <summary>Used when the browser does not send a grid size of its own.</summary>
        public const int WebMillGridDefaultWidth = 50;

        /// <inheritdoc cref="WebMillGridDefaultWidth"/>
        public const int WebMillGridDefaultHeight = 20;

        /// <summary>Used when Console.WindowWidth fails.</summary>
        public const int FallbackTerminalWidth = 80;

        /// <summary>Used when Console.WindowHeight fails.</summary>
        public const int FallbackTerminalHeight = 24;

        /// <summary>Lines taken by the header and borders.</summary>
        public const int MillTermHeightPadding = 12;

        /// <summary>Characters.</summary>
        public const int MillBorderPadding = 2;

        /// <summary>Characters.</summary>
        public const int MillProgressBarWidth = 30;

        /// <summary>Characters taken by the indent and the percentage beside the bar.</summary>
        public const int MillProgressLinePadding = 15;

        /// <summary>Characters, left and right margin together.</summary>
        public const int MillGridHorizontalPadding = 4;

        /// <summary>Shown until enough of the job has run to estimate the time left.</summary>
        public const string EtaUnknown = "--:--:--";

        public const string MillCurrentPosMarker = "● ";

        public const string MillVisitedMarker = "░░";

        public const string MillEmptyMarker = "··";

        public const string OverlayHoldMessage = "HOLD - Press R to resume";

        /// <summary>Asked before a run starts.</summary>
        public const string ProbeRemovedQuestion = "Probing equipment removed?";

        /// <summary>{0} is ProbeRemovedQuestion, {1} the depth adjustment, {2} the sections text, {3} SafetyPhasesFormat or nothing.</summary>
        public const string SafetyMessageFormat = "{0}  Depth: {1}mm  Sections: {2}{3}";

        /// <summary>For a job with more than one phase; {0} is the phases text.</summary>
        public const string SafetyPhasesFormat = "  Phases: {0}";

        /// <summary>{0} is SafetyPhasesKeyHint or nothing.</summary>
        public const string SafetyDepthSubMessage = "↑/↓=Depth  S=Sections  {0}Y=Start  Esc=Cancel";

        /// <summary>For a job with more than one phase.</summary>
        public const string SafetyPhasesKeyHint = "P=Phases  ";

        /// <summary>What a run mills when the choice is every phase.</summary>
        public const string PhasesAll = "all";

        /// <summary>{0} is the chosen phase numbers, joined by PhaseNumberSeparator.</summary>
        public const string PhasesChosenFormat = "{0} only";

        public const string PhaseNumberSeparator = ", ";

        public const string PhasesMenuTitle = "Phases to mill. Each tool's work is one phase. Press Enter to choose or clear one:";

        public const string PhasesDone = "Done";


        /// <summary>{0} is the phase's number, {1} how long its feed moves take.</summary>
        public const string PhaseLabelFormat = "Phase {0} · {1}";

        public const string PhaseChosenMarker = "[x] ";
        public const string PhaseNotChosenMarker = "[ ] ";

        /// <summary>What a run mills when the choice is the whole board (BoardSections.IsWholeBoard).</summary>
        public const string SectionsWholeBoard = "whole board";

        /// <summary>{0} is how many sections are chosen, {1} how many the board is divided into.</summary>
        public const string SectionsChosenFormat = "{0} of {1}";

        public const string SectionsTitle = "Sections";

        /// <summary>{0} columns, {1} rows, {2} the sections text.</summary>
        public const string SectionsDivisionFormat = "Columns: {0}  Rows: {1}    Mill: {2}";

        public const string SectionsDivideHint = "↑/↓=Rows  ←/→=Columns  Enter=Choose sections  Esc=Cancel";

        public const string SectionsChooseHint = "Arrows=Move  Space=Choose or clear  Enter=Done  Esc=Back";

        /// <summary>
        /// Lines the sections picker writes above and below the board, borders aside, and one
        /// spare so the last line's newline does not scroll the screen.
        /// </summary>
        public const int SectionPickerTextLines = 7;

        public const string WindowTooSmallForBoard = "(Window too small to draw the board)";

        /// <summary>{0} and {1} are the board's lowest and highest X, {2} and {3} its lowest and highest Y, in mm.</summary>
        public const string BoardBoundsFormat = "X: {0:F1} to {1:F1}  Y: {2:F1} to {3:F1}";

        public const string OverlayAlarmMessage = "ALARM - Press X to stop";

        public const string NoMachineProfileWarning = "No machine profile selected";

        public const string NoHeightMapWarning = "No height map - milling without height correction";

        public const string NotHomedWarning = "Machine not homed - will home before milling";

        public const string StoppedWhileCuttingWarning =
            "Last job stopped while cutting - a crash or stall may have lost the position";

        /// <summary>Asked after StoppedWhileCuttingWarning.</summary>
        public const string HomeFirstQuestion = "Home first?";

        /// <summary>
        /// The answer the home-first question starts at: yes, because a crash or stall can lose
        /// the position. Published to the browser, which ticks its box to match.
        /// </summary>
        public const bool HomeFirstByDefault = true;

        /// <summary>Follows a warning the operator may go past.</summary>
        public const string ContinueQuestion = "Continue?";

        public const string ContinueOrCancelKeyHint = "Y=Continue  Esc=Cancel";

        /// <summary>Shown under the Z-zero prompt.</summary>
        public const string JogContinueOrCancelKeyHint = "J=Jog  " + ContinueOrCancelKeyHint;

        public const string SleepPreventionWarning = "Sleep prevention unavailable";

        /// <summary>Heads the file's warnings before a probe or a mill moves the machine.</summary>
        public const string FileWarningsHeading = "WARNING: Check this file before the machine moves:";

        public const string FileWarningsPrompt = "Continue despite warnings?";

        /// <summary>Shown while GRBL reports the enclosure open.</summary>
        public const string DoorOpenMessage = "DOOR OPEN";

        /// <summary>Shown once the door is closed and the machine is waiting to be resumed.</summary>
        public const string DoorClosedMessage = "DOOR CLOSED - machine holding";

        /// <summary>
        /// While GRBL moves to the park position. The door may already be closed again, so this
        /// does not say it is open.
        /// </summary>
        public const string DoorRetractingStatus = "DOOR - machine retracting";

        /// <summary>Mill screen status while GRBL moves the tool back after a resume.</summary>
        public const string DoorResumingStatus = "DOOR CLOSED - machine resuming";

        public const string MillAlarmStatus = "ALARM";

        /// <summary>Shown while the machine is asleep ($SLP).</summary>
        public const string MillSleepStatus = "ASLEEP - reset to wake";

        /// <summary>The run is paused, not the machine.</summary>
        public const string MillPausedStatus = "PAUSED";

        /// <summary>Confirmed on the jog screen after zeroing.</summary>
        public const string ZeroedZ = "Z zeroed";

        /// <inheritdoc cref="ZeroedZ"/>
        public const string ZeroedAllAxes = "All axes zeroed";

        /// <summary>{0} is the zero confirmation; the rest states what became of the height map.</summary>
        public const string ZeroedMapStillApplied = "{0} - height map still applied";

        /// <inheritdoc cref="ZeroedMapStillApplied"/>
        public const string ZeroedMapDiscarded = "{0} - height map discarded";

        /// <inheritdoc cref="ZeroedMapStillApplied"/>
        public const string ZeroedFileLeftAlone = "{0} - height map kept for this run";

        /// <summary>Confirmed on the jog screen after an unlock ($X).</summary>
        public const string UnlockedMessage = "Unlocked";

        /// <summary>Confirmed on the jog screen after a soft reset.</summary>
        public const string ResetMessage = "Reset";

        /// <summary>Shown while a tool change that is already moving is stopped.</summary>
        public const string ToolChangeAbortingMessage = "Stopping the tool change...";

        public const string StopKeyHint = "Esc=Stop";

        /// <summary>
        /// Appended to the tool-change status while the machine moves on its own, because Escape
        /// works throughout.
        /// </summary>
        public const string ToolChangeAbortHint = "{0}  (" + StopKeyHint + ")";

        /// <summary>
        /// Shown when a job does not finish stopping in the time allowed. Used by both the
        /// terminal and the browser.
        /// </summary>
        public const string StopTimedOutWarning =
            "Stop did not finish. The machine may still be moving - check it.";

        public const string SleepPreventionSubMessage = "System may sleep during job. Y=Continue  Esc=Cancel";

        /// <summary>Characters.</summary>
        public const int ProbeGridConsolePadding = 4;

        /// <summary>Cells.</summary>
        public const int ProbeGridMaxDisplayWidth = 50;

        /// <summary>Cells.</summary>
        public const int ProbeGridMaxDisplayHeight = 20;

        /// <summary>Lines.</summary>
        public const int ProbeGridHeaderPadding = 8;

        // More proxy constants are in coppercli.Core/Util/Constants.cs.

        public const int ProxyDefaultPort = 34000;

        public const int WebDefaultPort = 34001;

        /// <summary>Bits; 24 is a class C network.</summary>
        public const int NetworkScanDefaultMask = 24;

        /// <summary>Bits; 16 is a class B network.</summary>
        public const int NetworkScanMinMask = 16;

        /// <summary>Waited after a stop before Z is raised to safe height.</summary>
        public const int MillStopDelayMs = 500;

        public const int ProxyStatusUpdateIntervalMs = 500;

        public const string StatusNone = "None";

        /// <summary>Per connection attempt, not for the whole scan.</summary>
        public const int NetworkScanTimeoutMs = 200;

        public const int NetworkScanParallelism = 100;

        /// <summary>How long the parsed command count stays on screen before the macro runs.</summary>
        public const int MacroParseDisplayMs = 500;

        public const string PromptEnter = "Press Enter to continue";

        public const string FileBrowserSelectTitle = "Select File";

        public const string FileBrowserSaveTitle = "Save File";

        public const string FileBrowserFilenameLabel = "Filename: ";

        public const string FileBrowserDirectoryLabel = "Directory";

        public const string FileBrowserErrorDirNotFound = "Directory not found: {0}";

        public const string FileBrowserMenuSave = "Save";

        public const string FileBrowserMenuChangeName = "Change filename";

        public const string FileBrowserMenuChangeDir = "Change directory";

        public const string MenuCancel = "Cancel";

        public const string FileBrowserSelectDir = "[Select this directory]";

        public const string FileBrowserHelpSelect = "↑↓ navigate, 1-9 select, Enter select, / filter, Esc cancel";

        public const string FileBrowserHelpFilter = "↑↓ navigate, 1-9 select, Enter select, type to filter, Esc clear";

        public const string FileBrowserHelpSave = "↑↓ navigate, 1-9 select, n edit name, Enter save, Esc cancel";

        public const string FileBrowserHelpEditName = "Type filename, Enter save, Esc cancel";

        /// <summary>Characters.</summary>
        public const int FileBrowserNameColumnWidth = 30;

        public const int MaxFileLoadWarningsShown = 5;

        // Spectre.Console markup names.

        public const string ColorError = "red";

        public const string ColorSuccess = "green";

        public const string ColorWarning = "yellow";

        public const string ColorPrompt = "blue";

        public const string ColorInfo = "cyan";

        public const string ColorDim = "dim";

        public const string ColorBold = "bold";

        public const string ToolChangeLabel = "TOOL CHANGE";

        /// <summary>macOS.</summary>
        public const string CaffeinateCommand = "caffeinate";

        /// <summary>Prevents idle sleep; display sleep is left alone.</summary>
        public const string CaffeinateArgs = "-i";

        /// <summary>Linux.</summary>
        public const string SystemdInhibitCommand = "systemd-inhibit";

        public const string SystemdInhibitArgs = "--what=idle:sleep --why=\"CNC operation\" sleep infinity";

        /// <summary>Unix; tests whether a command exists.</summary>
        public const string WhichCommand = "which";

        public const int ProgramCheckTimeoutMs = 1000;

        public const string ProbeMenuHeader = "Probe options:";

        public const string ProbeMenuContinue = "Continue Probing";

        public const string ProbeMenuDiscard = "Discard Probe Data";

        public const string ProbeMenuDiscardAndStart = "Discard and Start Probing";

        public const string ProbeMenuStart = "Start Probing";

        public const string ProbeMenuLoad = "Load from File";

        public const string ProbeMenuRecover = "Recover from Autosave";

        public const string ProbeMenuSave = "Save to File";

        public const string ProbeMenuSaveUnsaved = "Save Probe Data (unsaved)";

        public const string ProbeMenuApply = "Apply to G-Code";

        public const string ProbeMenuView = "View Height Map";

        public const string ProbeMenuBack = "Back";

        // Shared by every menu that disables an item.

        public const string DisabledConnect = "connect first";

        public const string ErrorNotConnected = "Not connected!";

        public const string DisabledNoFile = "load G-Code first";

        public const string DisabledDisconnect = "disconnect first";

        public const string DisabledNoZero = "set work zero first";

        public const string DisabledProbeSetupChanged = "height map is for a different file or origin";
        public const string DisabledProbeNotApplied = "apply probe data first";

        public const string DisabledProbeIncomplete = "probe incomplete ({0})";

        public const string DisabledAlarm = "clear alarm first";

        public const string DisabledRunInProgress = "a job is running";

        /// <summary>The machine is asleep after $SLP.</summary>
        public const string DisabledAsleep = "reset the machine first";

        public const string DisabledUnknown = "unknown error";

        public const string ErrorAutosaveNotDeleted = "Could not delete the saved height map.";

        public const string HeightMapApplied = "Height map applied.";

        public const string ExistingHeightMapQuestion = "Apply the existing height map to this file?";

        // Session questions, shown by both front ends; SessionRestore decides which are asked.
        public const string ReloadFileQuestion = "Reload the file you had open?";

        public const string StoredWorkZeroQuestion = "Is the work origin still where you left it?";

        public const string StoredWorkZeroDetail =
            "The machine has kept its work offset. Say no if the workpiece has moved or been replaced.";

        public const string UnfinishedHeightMapQuestion = "Keep the unfinished height map?";

        public const string UnsavedHeightMapQuestion = "Keep the height map you have not saved?";

        public const string SavedHeightMapQuestion = "Apply the height map you saved for this file?";

        /// <summary>{0} is the map file's name; {1} describes what the map holds.</summary>
        public const string SavedHeightMapDetail = "{0}: {1}";

        /// <summary>{0} is the number of points.</summary>
        public const string StoredMapPoints = "{0} points";

        /// <summary>{0} is the points measured; {1} the points in the grid.</summary>
        public const string StoredMapPointsMeasured = "{0} of {1} points measured";

        /// <summary>{0} is the size, from StoredMapPoints or StoredMapPointsMeasured; {1} the G-code file's name.</summary>
        public const string StoredMapMeasuredFor = "{0}, measured for {1}";

        /// <summary>{0} is the size, from StoredMapPoints or StoredMapPointsMeasured.</summary>
        public const string StoredMapFileNotRecorded =
            "{0}, from an earlier session (the file it was measured for was not recorded)";

        public const string StoredMapUndescribed = "stored height map";

        public const string ErrorQuestionAlreadyAnswered = "That question has changed or has already been answered.";

        public const string ErrorSavedHeightMapNotRead = "The saved height map could not be read.";

        public const string HeightMapDiscardedOnLoad = "Discarded the height map - {0}. Probe again before milling.";

        public const string ErrorNoCompleteMapToApply = "No finished height map to apply.";

        public const string ErrorNoFileLoaded = "No file loaded";

        public const string ErrorInvalidDepth = "The depth adjustment must be a number.";

        /// <summary>
        /// A run tracks its place in the file by line number, so replacing the file reopens
        /// the program at the start.
        /// </summary>
        public const string ErrorFileChangeDuringRun = "The job is using this file. Stop it first.";

        /// <summary>
        /// Re-zeroing X or Y moves the origin, so the rest of the job cuts in the wrong place. Z
        /// is the exception, because a tool change needs it.
        /// </summary>
        public const string ErrorZeroXYDuringRun = "Stop the job before re-zeroing X or Y.";

        /// <summary>Shown after "Home machine?" when the machine is asleep.</summary>
        public const string ErrorMachineAsleep = "Machine not ready. Reset it, then try again.";

        public const string PromptHomeMachine = "Home machine?";

        /// <summary>Asked after GRBL refused $H because the door is open.</summary>
        public const string PromptHomeDoorOpen = "The door is open. Close it, then home?";

        public const string ProbeStatusNotApplied = "* Probe data not yet applied to G-Code";

        public const string ProbeStatusApplied = "Probe data applied to G-Code";

        public const string ProbeStatusIncomplete = "Incomplete probe data found (autosaved)";

        public const string ProbeStatusNoData = "No probe data";

        public const string ProbeStatusNoFile = "No G-Code file loaded (required for probing)";

        public const string ProbeStatusNoZero = "Work zero not set (required for probing)";

        public const string ProbeStatusCleared = "Probe data cleared";

        public const string ProbePromptClear = "Clear probe data?";

        public const string ProbeStatusLoaded = "Probe data loaded";

        public const string ProbeStatusAppliedSuccess = "Probe data applied to G-Code!";

        public const string ProbeStatusComplete = "Probe data is already complete.";

        /// <summary>
        /// Shown when a macro asks for a probe while one is already running. They share one
        /// controller, so the second would overwrite the first's settings.
        /// </summary>
        public const string ProbeErrorAlreadyRunning =
            "Probing is already running. Wait for it to finish, then try again.";

        public const string ProbeStatusStarted = "Probing started. Space=Pause, Escape=Stop";

        public const string ProbeStatusPaused = "PAUSED - Space=Resume, Escape=Stop";

        public const string ProbeStatusResumed = "Resumed";

        public const string ProbeStatusStopping = "Stopping...";

        public const string ProbeStatusStopped = "Probing stopped by user";

        public const string ProbeStatusCompleteSuccess = "Probing complete!";

        public const string ProbeStatusNotSaved = "Probe data not saved";

        public const string ProbeStatusUnsaved = "* Probe data complete but not saved";

        public const string ProbeErrorIncomplete = "Probe data not complete";

        public const string ProbeAutosaveNotApplicable =
            "The saved height map was measured for a different file or work origin.";

        public const string ProbeErrorNoAutosave =
            "There is no saved height map to recover. Probe the board first.";

        /// <summary>Substituted into {0} of ZeroDiscardsMap.</summary>
        public const string PartlyMeasuredMap = "a partly measured";

        /// <inheritdoc cref="PartlyMeasuredMap"/>
        public const string CompleteMap = "a complete";

        /// <inheritdoc cref="PartlyMeasuredMap"/>
        public const string UnmeasuredMap = "an unmeasured";

        /// <summary>
        /// {0} describes the map and {1} names the axes. Published through /api/constants, so the
        /// browser shows the same words.
        /// </summary>
        public const string ZeroDiscardsMap =
            "You have {0} height map for this board, measured from the current X/Y origin. "
            + "Zeroing {1} deletes it and the saved copy, and you will have to probe again. "
            + "Zeroing only Z keeps it. Continue?";

        public const string SettingsNotSaved =
            "Could not save your settings. They will be back to their old values next time "
            + "coppercli starts.";

        public const string ErrorFileNotLoaded = "The file from your last session would not load.";

        public const string ProbeStatusRecovered = "Recovered {0}/{1} points from autosave";

        public const string ProbeErrorNoZero = "Work zero not set. Use Move menu to zero all axes (0) first.";

        public const string ProbeErrorNoIncomplete = "No incomplete probe data found.";

        public const string ProbeErrorNoComplete = "No complete probe data to save.";

        public const string ProbeErrorNoHeights = "No measured heights to show.";

        public const string ProbePromptMargin = "Probe margin (mm)";

        public const string ProbePromptGridSize = "Grid size (mm)";

        public const string ProbePromptTraceOutline = "Trace outline first?";

        public const string ProbePromptApply = "Apply probe data to G-Code?";

        public const string ProbePromptMill = "Proceed to Milling?";

        public const string ProbeDisplayHeader = "Probing:";

        /// <summary>Heads the height map drawn from the Probe menu, in place of ProbeDisplayHeader.</summary>
        public const string ProbeDisplayMapHeader = "Height map:";

        public const string ProbeDisplayEscapeStop = "Press Escape to stop";

        public const string ProbeDisplayZNoData = "Z: --";

        public const string ProbeDisplayPoints = "Points:";

        public const string ProbeDisplayZRange = "Z range:";

        public const string ProbeDisplayVariance = "Variance:";

        public const string ProbeDisplayMm = "mm";

        public const string ProbeFormatResume = "Resuming probe: {0}/{1} points complete";

        public const string ProbeFormatLoadError = "Error loading probe data: {0}";

        /// <summary>
        /// Shown in place of a caught exception, whose own text names files and types the operator
        /// cannot act on. {0} is one of the Failed* constants below.
        /// </summary>
        public const string ErrorSomethingFailed =
            "{0} failed. Try again; if it keeps happening, restart coppercli with --debug and "
            + "keep the coppercli.log file it writes.";

        /// <summary>
        /// Substituted into {0} of ErrorSomethingFailed, worded as the operator would describe the
        /// step.
        /// </summary>
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

        public const string ProbeFormatGrid = "Probe grid: {0}x{1} = {2} points";

        public const string ProbeFormatBounds = "Bounds: X({0:F2} to {1:F2}) Y({2:F2} to {3:F2})";

        public const string ProbeFormatSaved = "Probe data saved to {0}";

        public const string ProbeFormatSaveError = "Error saving: {0}";

        public const string ProbeFormatOverwrite = "Overwrite {0}?";
    }
}
