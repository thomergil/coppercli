namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Operator messages, timeouts, and limits for controller runs. Both UIs use these messages.
    ///
    /// Two coordinate systems are in play:
    /// - Machine coordinates (G53): absolute, relative to home. Z=0 at the top, negative down.
    /// - Work coordinates (G54 by default): relative to the workpiece origin, Z=0 usually at
    ///   the PCB surface.
    ///
    /// Retracts and tool changes use G53, so the destination does not move when the work offset
    /// changes. Set the coordinate mode explicitly before such a move.
    /// </summary>
    public static class ControllerConstants
    {
        /// <summary>
        /// Show a workflow's deliberate exception message, or a generic message for
        /// other exceptions.
        ///
        /// ObjectDisposedException derives from InvalidOperationException and its text names
        /// an internal object, so it is excluded.
        /// </summary>
        public static string ShowableMessage(System.Exception ex) =>
            ex is System.InvalidOperationException or System.TimeoutException
                && ex is not (InvalidControllerStateException or System.ObjectDisposedException)
                ? ex.Message
                : ErrorRunFailed;

        public const string ErrorInvalidTransition = "Invalid state transition: {0} → {1}";
        public const string ErrorCannotStart = "Cannot start: controller is {0}";
        public const string ErrorCannotPause = "Cannot pause: controller is {0}";
        public const string ErrorCannotResume = "Cannot resume: controller is {0}";
        public const string ErrorCannotReset = "Cannot reset: controller is {0}";
        public const string ErrorHomingFailed = "Homing did not complete.";

        public const string ErrorHomingDisabledOnMachine =
            "Homing is disabled on the machine ($22). Enable it, then mill.";

        public const string ErrorHomingFailedBecause = "Homing did not complete. {0}";
        public const string ErrorSafetyRetractFailed = "Could not confirm the tool lifted. Stopped.";

        public const string ErrorProbePointSkipped = "No contact at point {0} of {1}. Left unmeasured.";

        public const string ErrorStopRetractFailed =
            "Stopped. Could not confirm the tool lifted - check it.";
        public const string ErrorWorkOffsetUnknown = "No work offsets from the machine. Stopped.";
        public const string ErrorToolOffsetNotTaken =
            "The machine did not take the new tool's Z origin. Stopped.";

        /// <summary>
        /// Remove the depth adjustment from the work origin when the run ends. Otherwise
        /// later jobs cut at the adjusted depth.
        /// </summary>
        public const string ErrorDepthAdjustmentNotRestored =
            "The {0:F2}mm depth adjustment is still in the work origin - the machine would "
            + "not take it back out. Set Z zero again before the next job.";

        /// <summary>
        /// The work origin was not written. Shown instead of a confirmation, because
        /// recording an origin the machine does not have puts the next cut in the wrong
        /// place and deletes the height map on the way.
        /// </summary>
        public const string ErrorWorkZeroNotWritten =
            "The machine did not take the work origin. Check it is connected and not alarmed, then try again.";
        /// <summary>
        /// How many times the operator is asked to clear the machine before giving up. Read
        /// by every screen that clears a door: a run, the jog screen and the connect flow.
        /// </summary>
        public const int MachineClearAttempts = 5;

        /// <summary>
        /// The operator answered the enclosure prompt this many times and the hold is still
        /// there, which indicates a fault in the switch or its wiring.
        /// </summary>
        public const string ErrorDoorWillNotRelease = "Door will not clear. Check the switch.";

        public const string DoorOpenPrompt = "Close the door.";

        /// <summary>Shown once the door reads closed but the machine is still holding.</summary>
        public const string DoorHoldingPrompt = "Door closed. Continue?";

        /// <summary>
        /// Shown while GRBL restores from the park. It mentions neither the tool nor the
        /// spindle, because the same message is shown for a probe, where no spindle runs.
        /// </summary>
        public const string DoorResumingMessage = "Resuming...";


        /// <summary>
        /// The door was opened and closed while the machine was homing. GRBL holds until
        /// it is resumed, and only the operator may do that.
        /// </summary>
        public const string ErrorDoorClosedDuringHoming = "Door opened during homing. Start again.";

        public const string ErrorMachineDoorOpen = "Door open. Close it, then start again.";
        /// <summary>
        /// A controller raised a prompt with no subscriber on UserInputRequired. The run
        /// would otherwise wait for an answer that cannot arrive, and only a restart clears it.
        /// </summary>
        public const string ErrorNoPromptHandler =
            "The job could not ask for an answer. Restart coppercli.";

        public const string ErrorMachineNotResponding =
            "Machine not accepting moves. Check door, alarm and sleep.";

        public const string ErrorDoorBlocksResume = "Holding at the door. Close it.";

        /// <summary>
        /// Do not send CycleStart from the door overlay while a run waits for its
        /// enclosure prompt; its Continue button handles the answer.
        /// </summary>
        public const string ErrorDoorAnswerThePrompt = "Answer the job's door prompt.";
        public const string ErrorMachineNotSettled = "Machine still moving. Wait, then start again.";
        public const string ErrorMillingDidNotStart =
            "The job did not start. Reconnect or reset, then try again.";
        public const string ErrorMillingAlarm = "Alarm during the job. Milling stopped.";

        /// <summary>An alarm found while settling, before any cutting.</summary>
        public const string ErrorAlarmBeforeStart = "In alarm. Clear it, then unlock.";

        /// <summary>
        /// Shown when a run ends on an error the workflow does not handle, such as a disk
        /// error or a port another program holds.
        /// </summary>
        public const string ErrorRunFailed = "The run stopped on an error. Check the machine.";
        public const string LogMillingAlarm = "Milling aborted: machine in alarm state ({0})";
        public const string ErrorProbeNoContact = "Probe failed: max depth reached without contact";
        public const string ErrorProbeCycleNotOpen =
            "Could not start the probe: the machine is busy or not connected.";
        public const string ErrorProbeHeightUnexpected =
            "Probe height {0:F3}mm is {1:F3}mm off the points around it. Check for debris.";
        public const string ErrorProbeTimeout = "Probe timed out";
        public const string ErrorToolSetterNotConfigured = "Tool setter position not configured";
        public const string ErrorTraceHeightUnsafe = "Trace height must be positive (current: {0:F3}mm)";

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

        // Values for ProgressInfo.Phase. Screens compare against them, so the text is
        // part of the interface, not decoration.

        public const string PhaseSettling = "Settling";
        public const string PhaseHoming = "Homing";
        public const string PhaseRetracting = "Retracting";
        public const string PhaseInitializing = "Initializing";
        public const string PhaseMilling = "Milling";
        public const string PhaseCompleting = "Completing";
        public const string PhaseWaitingForOperator = "Waiting for operator";

        /// <summary>
        /// Clear the <see cref="PhaseWaitingForOperator"/> message before the run selects
        /// its next phase. The mill screen displays phase messages from other operations.
        /// </summary>
        public const string PhaseDoorCleared = "Door clear";

        /// <summary>How far past an M6 to look for the redundant M0 that follows it.</summary>
        public const int ToolChangeM0SearchLines = 8;

        public const string MessageSettlingCountdown = "Settling... {0}s";
        public const string MessageWaitingForIdle = "Waiting for idle...";
        public const string MessageHoming = "Homing machine...";
        public const string MessageRetracting = "Retracting Z to safe height...";
        public const string MessageInitializing = "Initializing machine state...";
        public const string MessageMillingProgress = "Line {0} of {1}";
        public const string MessageComplete = "Milling complete";

        public const string OptionContinue = "Continue";
        public const string OptionAbort = "Abort";


        public const string LogToolChangeStart = "Tool change started: T{0}";
        public const string LogToolChangeComplete = "Tool change complete";
        public const string LogToolChangeAborted = "Tool change aborted by user";
        public const string LogToolChangeProbeFailed = "Tool change probe failed";
        public const string LogToolChangePhase = "Tool change phase: {0}";
        public const string LogToolChangeOffset = "Tool offset: ref={0:F3}, new={1:F3}, offset={2:F3}";

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

        public const string ToolChangePromptTitle = "Tool Change";
        public const string ToolChangePrompt = "Change to tool T{0} and press Continue";

        /// <summary>The same prompt where the tool has a name in the file.</summary>
        public const string ToolChangePromptNamed = "Change to tool T{0} ({1}) and press Continue";
        public const string ToolChangePromptZeroZ = "Jog to PCB surface, set Z0, then press Continue";
        public const string ToolChangeZeroZTitle = "Set Z Zero";

        /// <summary>Title for the M0/M1 pause dialog. It reuses the tool-change dialog
        /// (see MillingController.HandleOperatorPauseAsync), so it needs a title of its own
        /// rather than "Tool Change".</summary>
        public const string OperatorPauseTitle = "Program Paused";

        /// <summary>
        /// Shown when the program pauses without a note explaining why. It gives no line
        /// number, because the streamed program is regenerated from the parsed toolpath and
        /// its numbering does not match the file the operator has open.
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

        public const int ProgressPercentComplete = 100;

        /// <summary>How long to give GRBL to leave the door hold after a resume.</summary>
        public const int DoorResumeTimeoutMs = 5000;

        /// <summary>
        /// How many status reports GRBL's reading of the door switch may lag behind the
        /// operator's answer: the substate arrives on the status poll, so the report received
        /// when they answer predates them closing the door. This is not a wait for the door
        /// itself - see <see cref="MachineWait.ReleaseDoorHoldAsync"/>.
        /// </summary>
        public const int DoorReadingCatchUpReports = 3;

        /// <summary>
        /// How far a probed height may sit from its measured neighbors before the run
        /// pauses for the operator (mm). Adjacent nodes differ by the board's warp over one
        /// grid step plus probe repeatability, which together stay well under 0.1mm, while a
        /// tip that stopped on something other than the board reads whole tenths away.
        /// </summary>
        public const double ProbeHeightDeviationToleranceMm = 0.5;
    }
}
