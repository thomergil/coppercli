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

        /// <summary>Closed and parked, waiting for a cycle start.</summary>
        WaitingForResume,

        /// <summary>Restoring from the park: the tool is moving and the spindle is starting.</summary>
        Resuming
    }
}
