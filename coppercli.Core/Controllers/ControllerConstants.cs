namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Constants for controller layer. No magic strings or numbers.
    ///
    /// SAFETY NOTE: CNC operations involve two coordinate systems:
    /// - Machine coordinates (G53): Absolute positions relative to home. Z=0 at top, negative down.
    /// - Work coordinates (G54 default): Relative to workpiece origin. Z=0 typically at PCB surface.
    ///
    /// Safety-critical operations (retracts, tool changes) use MACHINE coordinates (G53) to ensure
    /// predictable behavior regardless of work offset. Defense-in-depth: always verify coordinate
    /// mode before safety-critical moves.
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
        public const string ErrorHomingFailed = "Homing failed. Cannot mill without homing.";
        public const string ErrorProbeNoContact = "Probe failed: max depth reached without contact";
        public const string ErrorProbeTimeout = "Probe timed out";
        public const string ErrorToolSetterNotConfigured = "Tool setter position not configured";

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
        public const string LogDepthAdjustment = "Depth adjustment: Z offset {0:F3} → {1:F3} (adj: {2:F3})";
        public const string LogFileStarted = "File started: Mode={0}, Position={1}";
        public const string LogMillingComplete = "Milling complete (stable idle)";
        public const string LogM6Detected = "M6 detected at line {0}, tool {1}";
        public const string LogSkippingM0 = "Skipping M0 at line {0} (redundant after M6)";

        // =========================================================================
        // Phase names (for progress display)
        // =========================================================================

        public const string PhaseSettling = "Settling";
        public const string PhaseHoming = "Homing";
        public const string PhaseRetracting = "Retracting";
        public const string PhaseInitializing = "Initializing";
        public const string PhaseMilling = "Milling";
        public const string PhaseCompleting = "Completing";

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
        // Tool change phase names
        // =========================================================================

        public const string PhaseToolChangeRaisingZ = "RaisingZ";
        public const string PhaseToolChangeMovingToSetter = "MovingToToolSetter";
        public const string PhaseToolChangeMeasuringRef = "MeasuringReference";
        public const string PhaseToolChangeMovingToWork = "MovingToWorkArea";
        public const string PhaseToolChangeWaitingForUser = "WaitingForUserChange";
        public const string PhaseToolChangeMeasuringNew = "MeasuringNewTool";
        public const string PhaseToolChangeProbingPCB = "ProbingPCBSurface";
        public const string PhaseToolChangeApplyingOffset = "ApplyingOffset";
        public const string PhaseToolChangeReturning = "Returning";
        public const string PhaseToolChangeComplete = "Complete";

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
        public const string ToolChangePromptWithSetter = "Change to tool T{0} and press Continue";
        public const string ToolChangePromptWithoutSetter = "Change to tool T{0} and press Continue";
        public const string ToolChangePromptZeroZ = "Jog to PCB surface, set Z0, then press Continue";
        public const string ToolChangeZeroZTitle = "Set Z Zero";
    }
}
