namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Name each step of grid probing. <see cref="ControllerState"/> records whether
    /// the run finished, was cancelled, or failed.
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
