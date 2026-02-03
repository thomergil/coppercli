namespace coppercli.Core.Controllers
{
    /// <summary>
    /// States for workflow controllers (milling, probing, tool change).
    /// Controllers are explicit finite state machines with validated transitions.
    /// </summary>
    public enum ControllerState
    {
        /// <summary>Ready to start a new operation.</summary>
        Idle,

        /// <summary>Setup phase (settling, homing, safety checks).</summary>
        Initializing,

        /// <summary>Main operation in progress.</summary>
        Running,

        /// <summary>User paused the operation.</summary>
        Paused,

        /// <summary>Waiting for user response to a prompt.</summary>
        WaitingForUserInput,

        /// <summary>Finishing up (waiting for idle, cleanup).</summary>
        Completing,

        /// <summary>Operation completed successfully.</summary>
        Completed,

        /// <summary>Operation failed due to error.</summary>
        Failed,

        /// <summary>Operation cancelled by user.</summary>
        Cancelled
    }
}
