namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Which of GRBL's door states the machine is in. Each needs different text and a
    /// different action, so callers must not collapse them into two.
    /// </summary>
    public enum DoorState
    {
        None,

        /// <summary>The enclosure may be open. Only the operator can close it.</summary>
        Open,

        /// <summary>
        /// GRBL is still moving to the park position. It reports this until the move ends,
        /// whether or not the door has been closed since, so it says nothing about the door.
        /// </summary>
        Retracting,

        /// <summary>Closed and parked, waiting for a cycle start.</summary>
        WaitingForResume,

        /// <summary>Restoring from the park: the tool is moving and the spindle is starting.</summary>
        Resuming
    }
}
