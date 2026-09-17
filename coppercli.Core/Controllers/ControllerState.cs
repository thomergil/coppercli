namespace coppercli.Core.Controllers
{
    /// <summary>
    /// The states every workflow controller runs through. `ControllerBase` holds the table of
    /// allowed transitions and rejects the rest.
    /// </summary>
    public enum ControllerState
    {
        Idle,

        /// <summary>Settling, homing and safety checks, before any work starts.</summary>
        Initializing,

        Running,

        Paused,

        WaitingForUserInput,

        /// <summary>Work is done; waiting for idle and retracting.</summary>
        Completing,

        Completed,

        Failed,

        Cancelled
    }
}
