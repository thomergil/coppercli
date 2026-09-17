namespace coppercli.Core.Controllers
{
    /// <summary>
    /// The step of work a grid probing run is on. Whether the run finished, was cancelled or
    /// failed belongs to <see cref="ControllerState"/>; naming those here stored one fact twice.
    /// </summary>
    public enum ProbePhase
    {
        NotStarted,

        /// <summary>Optional; skipped unless the operator asked for the outline.</summary>
        TracingOutline,

        /// <summary>Raising Z in machine coordinates, which is safe whatever the work offset is.</summary>
        SafetyRetracting,

        /// <summary>Moving to the first probe point.</summary>
        MovingToStart,

        /// <summary>Dropping to the safe height in work coordinates.</summary>
        Descending,

        MovingToPoint,

        Probing,

        RecordingResult,

        /// <summary>Raising Z once every point is probed.</summary>
        FinalRetract
    }
}
