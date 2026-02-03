namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Progress information emitted by controllers during operations.
    /// Immutable record for thread-safe event handling.
    /// </summary>
    public record ProgressInfo(
        /// <summary>Current phase name (e.g., "Settling", "Homing", "Milling").</summary>
        string Phase,

        /// <summary>Overall progress percentage (0-100).</summary>
        float Percentage,

        /// <summary>Human-readable description of current action.</summary>
        string Message,

        /// <summary>Current step number (e.g., line 50, probe point 5). Null if not applicable.</summary>
        int? CurrentStep = null,

        /// <summary>Total steps (e.g., 200 lines, 25 points). Null if not applicable.</summary>
        int? TotalSteps = null
    );
}
