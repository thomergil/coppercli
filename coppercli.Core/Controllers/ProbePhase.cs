namespace coppercli.Core.Controllers
{
    /// <summary>
    /// The step of work a grid probing run is on.
    ///
    /// Work steps only. Whether the run finished, was cancelled or failed belongs to
    /// <see cref="ControllerState"/>; naming those here stored one fact twice.
    /// </summary>
    public enum ProbePhase
    {
        /// <summary>Not probing.</summary>
        NotStarted,

        /// <summary>Tracing probe outline (optional).</summary>
        TracingOutline,

        /// <summary>Safety retracting Z to machine coords (truly safe height).</summary>
        SafetyRetracting,

        /// <summary>Moving to start position (first probe point).</summary>
        MovingToStart,

        /// <summary>Descending to safe height (work coords).</summary>
        Descending,

        /// <summary>Moving to next probe point.</summary>
        MovingToPoint,

        /// <summary>Probing current point.</summary>
        Probing,

        /// <summary>Recording probe result.</summary>
        RecordingResult,

        /// <summary>Raising Z after all points complete.</summary>
        FinalRetract
    }
}
