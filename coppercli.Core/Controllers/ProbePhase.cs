namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Phases within a grid probing workflow.
    /// </summary>
    public enum ProbePhase
    {
        /// <summary>Not probing.</summary>
        NotStarted,

        /// <summary>Creating probe grid from file bounds.</summary>
        CreatingGrid,

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
        FinalRetract,

        /// <summary>All points probed successfully.</summary>
        Complete,

        /// <summary>Probing was cancelled by user.</summary>
        Cancelled,

        /// <summary>Probing failed due to error.</summary>
        Failed
    }
}
