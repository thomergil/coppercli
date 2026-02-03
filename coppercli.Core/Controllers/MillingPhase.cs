namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Phases within the milling workflow.
    /// Provides finer-grained tracking than ControllerState.
    /// </summary>
    public enum MillingPhase
    {
        /// <summary>Not yet started.</summary>
        NotStarted,

        /// <summary>Waiting for machine to stabilize in Idle state.</summary>
        Settling,

        /// <summary>Homing the machine (if not already homed).</summary>
        Homing,

        /// <summary>Retracting Z to safe height.</summary>
        Retracting,

        /// <summary>Initializing machine state (G90, G17, depth adjustment).</summary>
        Initializing,

        /// <summary>Actively milling (sending G-code file).</summary>
        Milling,

        /// <summary>User paused the operation.</summary>
        Paused,

        /// <summary>Handling M6 tool change.</summary>
        ToolChange,

        /// <summary>Waiting for completion and cleanup.</summary>
        Completing
    }
}
