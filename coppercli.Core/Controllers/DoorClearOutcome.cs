namespace coppercli.Core.Controllers
{
    /// <summary>How clearing a door hold ended.</summary>
    public enum DoorClearOutcome
    {
        /// <summary>The machine is out of Door.</summary>
        Cleared,

        /// <summary>The operator declined to release it, or backed out of waiting.</summary>
        Declined,

        /// <summary>Released as often as allowed and the hold is still there.</summary>
        WillNotRelease
    }
}
