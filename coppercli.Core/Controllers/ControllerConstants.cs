namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Constants for controller layer. No magic strings or numbers.
    ///
    /// SAFETY NOTE: CNC operations involve two coordinate systems:
    /// - Machine coordinates (G53): Absolute positions relative to home. Z=0 at top, negative down.
    /// - Work coordinates (G54 default): Relative to workpiece origin. Z=0 typically at PCB surface.
    ///
    /// Retracts and tool changes use MACHINE coordinates (G53), so the destination does not move
    /// when the work offset changes. Always set the coordinate mode explicitly before such a move.
    /// </summary>
    public static class ControllerConstants
    {
        // =========================================================================
        // Error messages
        // =========================================================================

        public const string ErrorInvalidTransition = "Invalid state transition: {0} → {1}";
        public const string ErrorCannotStart = "Cannot start: controller is {0}";
        public const string ErrorCannotPause = "Cannot pause: controller is {0}";
        public const string ErrorCannotResume = "Cannot resume: controller is {0}";
        public const string ErrorCannotReset = "Cannot reset: controller is {0}";
        public const string ErrorHomingFailed = "Homing did not complete, so milling cannot start.";

        /// <summary>Shown when the machine itself reports that homing is switched off.</summary>
        public const string ErrorHomingDisabledOnMachine =
            "This machine reports that homing is disabled in its own settings ($22), so it cannot establish " +
            "the reference position that every safety retract depends on. Enable homing on the controller " +
            "(and check the limit switches are wired) before milling.";

        /// <summary>Homing failed and the machine explained why.</summary>
        public const string ErrorHomingFailedBecause = "Homing did not complete, so milling cannot start. {0}";
        public const string ErrorSafetyRetractFailed = "Could not confirm the tool lifted to a safe height. Stopped before moving.";

        /// <summary>Shown when a point could not be measured and the run carried on.</summary>
        public const string ErrorProbePointSkipped =
            "No contact at point {0} of {1}. It was left unmeasured, so the probe data is incomplete.";

        /// <summary>Shown when a stopped run cannot confirm the tool reached safe height.</summary>
        public const string ErrorStopRetractFailed =
            "Stopped, but could not confirm the tool lifted clear. Check whether the tool is "
            + "still down before you move the machine.";
        public const string ErrorWorkOffsetUnknown = "The machine did not report its work offsets. Stopped rather than guess the Z origin.";
        /// <summary>Title of the prompt that holds a job until the door is closed.</summary>
        public const string DoorPromptTitle = "Enclosure Door";

        /// <summary>Shown while GRBL still reports the door open.</summary>
        public const string DoorOpenPrompt =
            "The enclosure door is open. If a magnet is on the switch, take it off. Close "
            + "the door, then press Continue.";

        /// <summary>Shown once it is shut but the machine is still holding.</summary>
        public const string DoorHoldingPrompt =
            "The door is closed and the machine is holding. Press Continue to release the hold.";

        /// <summary>
        /// The door was opened and closed again while the machine was homing. GRBL holds
        /// until something resumes it, and only the operator may decide that.
        /// </summary>
        public const string ErrorDoorClosedDuringHoming =
            "The door was opened while the machine was homing, so homing stopped. The door is "
            + "closed now - start the job again.";

        public const string ErrorMachineDoorOpen = "The enclosure door is open. Close it, then start the job again.";
        public const string ErrorMachineNotSettled = "The machine did not stop moving. Wait for it to finish, then start the job again.";
        public const string ErrorMillingDidNotStart =
            "The job did not start streaming to the machine. This usually means the machine was left in probe mode - reconnect or reset, then try again.";
        public const string ErrorMillingAlarm = "The machine raised an alarm during the job. Milling stopped.";

        /// <summary>
        /// What the operator is told when a run ends on an error the workflow does not
        /// handle itself - a disk error, a port another program took.
        /// </summary>
        public const string ErrorRunFailed =
            "The run stopped on an unexpected error. Check the machine before doing anything else.";
        public const string LogMillingAlarm = "Milling aborted: machine in alarm state ({0})";
        public const string ErrorProbeNoContact = "Probe failed: max depth reached without contact";
        public const string ErrorProbeHeightUnexpected =
            "Probe height {0:F3}mm differs from the surrounding measurements by {1:F3}mm - "
            + "check the board for debris before continuing.";
        public const string ErrorProbeTimeout = "Probe timed out";
        public const string ErrorToolSetterNotConfigured = "Tool setter position not configured";
        public const string ErrorTraceHeightUnsafe = "Trace height must be positive (current: {0:F3}mm)";

        // =========================================================================
        // Log messages
        // =========================================================================

        public const string LogStateTransition = "{0}: {1} → {2}";
        public const string LogPhaseChange = "{0} phase: {1}";
        public const string LogMillingStart = "Milling started, depth adjustment: {0:F3}mm";
        public const string LogSettlingPhase = "Settling phase: waiting {0} seconds";
        public const string LogSettlingComplete = "Settling complete";
        public const string LogStatusChanged = "Status changed: {0} → {1}, resetting settle count";
        public const string LogHomingStart = "Homing started";
        public const string LogHomingComplete = "Homing complete";
        public const string LogSafetyRetract = "Safety retract to Z={0} (machine coords)";
        public const string LogStateInit = "State initialization: G90 G17";
        public const string LogNoDepthAdjustment = "No depth adjustment (0mm)";
        public const string LogDepthAdjustmentRestored = "Depth adjustment restored: Z offset back to {0:F3}";
        public const string LogDepthAdjustment = "Depth adjustment: Z offset {0:F3} → {1:F3} (adj: {2:F3})";
        public const string LogFileStarted = "File started: Mode={0}, Position={1}";
        public const string LogMillingComplete = "Milling complete (stable idle)";
        public const string LogM6Detected = "M6 detected at line {0}, tool {1}";
        public const string LogSkippingM0 = "Skipping M0 at line {0} (redundant after M6)";
        public const string LogOperatorPauseContinued = "Operator continued past pause at line {0}";
        public const string LogProgramEndDetected = "Program end (M2/M30) detected at line {0}, ending stream";

        // =========================================================================
        // Phase names (for progress display)
        // =========================================================================

        public const string PhaseSettling = "Settling";
        public const string PhaseHoming = "Homing";
        public const string PhaseRetracting = "Retracting";
        public const string PhaseInitializing = "Initializing";
        public const string PhaseMilling = "Milling";
        public const string PhaseCompleting = "Completing";
        public const string PhaseWaitingForOperator = "Waiting for operator";

        /// <summary>How far past an M6 to look for the redundant M0 that follows it.</summary>
        public const int ToolChangeM0SearchLines = 8;

        // =========================================================================
        // Progress messages
        // =========================================================================

        public const string MessageSettlingCountdown = "Settling... {0}s";
        public const string MessageWaitingForIdle = "Waiting for idle...";
        public const string MessageHoming = "Homing machine...";
        public const string MessageRetracting = "Retracting Z to safe height...";
        public const string MessageInitializing = "Initializing machine state...";
        public const string MessageMillingProgress = "Line {0} of {1}";
        public const string MessageComplete = "Milling complete";

        // =========================================================================
        // User input options
        // =========================================================================

        public const string OptionContinue = "Continue";
        public const string OptionAbort = "Abort";


        // =========================================================================
        // Tool change log messages
        // =========================================================================

        public const string LogToolChangeStart = "Tool change started: T{0}";
        public const string LogToolChangeComplete = "Tool change complete";
        public const string LogToolChangeAborted = "Tool change aborted by user";
        public const string LogToolChangeProbeFailed = "Tool change probe failed";
        public const string LogToolChangePhase = "Tool change phase: {0}";
        public const string LogToolChangeOffset = "Tool offset: ref={0:F3}, new={1:F3}, offset={2:F3}";

        // =========================================================================
        // Tool change progress messages
        // =========================================================================

        public const string MessageToolChangeRaisingZ = "Raising Z to clearance...";
        public const string MessageToolChangeMovingToSetter = "Moving to tool setter...";
        public const string MessageToolChangeMeasuringRef = "Measuring reference tool...";
        public const string MessageToolChangeMovingToWork = "Moving to work area...";
        public const string MessageToolChangeWaitingForToolChange = "Change tool and press Continue";
        public const string MessageToolChangeWaitingForZeroZ = "Set Z0 and press Continue";
        public const string MessageToolChangeMeasuringNew = "Measuring new tool...";
        public const string MessageToolChangeProbingPCB = "Probing PCB surface...";
        public const string MessageToolChangeApplyingOffset = "Applying Z offset...";
        public const string MessageToolChangeReturning = "Returning to work position...";
        public const string MessageToolChangeComplete = "Tool change complete";

        // =========================================================================
        // Tool change user prompts
        // =========================================================================

        public const string ToolChangePromptTitle = "Tool Change";
        public const string ToolChangePrompt = "Change to tool T{0} and press Continue";
        public const string ToolChangePromptZeroZ = "Jog to PCB surface, set Z0, then press Continue";
        public const string ToolChangeZeroZTitle = "Set Z Zero";

        // =========================================================================
        // Operator pause (M0/M1) prompt
        // =========================================================================

        /// <summary>Title for the M0/M1 pause dialog. Reuses the tool-change dialog
        /// plumbing (see MillingController.HandleOperatorPauseAsync), so it needs its
        /// own title rather than inheriting "Tool Change".</summary>
        public const string OperatorPauseTitle = "Program Paused";

        /// <summary>
        /// Shown when the program pauses and carries no note saying why. Deliberately
        /// carries no line number: the streamed program is regenerated from the parsed
        /// toolpath, so its line numbering does not match the file the operator has open.
        /// The progress line reports how far in they are.
        /// </summary>
        public const string OperatorPausePrompt =
            "The program paused. The tool is still down and the spindle is still running. "
            + "Continue milling, or stop the job?";

        /// <summary>As above, quoting the note the program left ({0}).</summary>
        public const string OperatorPausePromptWithNote =
            "The program paused: {0}. The tool is still down and the spindle is still "
            + "running. Continue milling, or stop the job?";

        /// <summary>How far back to look for a comment explaining a pause.</summary>
        public const int PauseNoteSearchLines = 4;

        /// <summary>The percentage a finished step reports.</summary>
        public const int ProgressPercentComplete = 100;

        /// <summary>How long to give GRBL to leave the door hold after a resume.</summary>
        public const int DoorResumeTimeoutMs = 5000;

        /// <summary>
        /// How far a probed height may sit from its measured neighbors before the run
        /// pauses for the operator (mm).
        ///
        /// Adjacent nodes differ by the board's warp over one grid step plus probe
        /// repeatability, together well under a tenth of a millimeter. A tip that stopped
        /// somewhere other than the board lands whole tenths away.
        /// </summary>
        public const double ProbeHeightDeviationToleranceMm = 0.5;
    }
}
