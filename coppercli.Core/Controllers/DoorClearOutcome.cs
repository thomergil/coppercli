namespace coppercli.Core.Controllers
{
    /// <summary>What `MachineWait.ClearDoorHoldAsync` returned.</summary>
    public enum DoorClearOutcome
    {
        Cleared,

        /// <summary>The operator declined to release it, or backed out of waiting.</summary>
        Declined,

        /// <summary>Released as often as the retry limit allows and the hold is still there.</summary>
        WillNotRelease
    }
}
