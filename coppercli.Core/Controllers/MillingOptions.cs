#nullable enable
namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Configuration options for a milling operation.
    /// Snapshot at StartAsync - changes during operation are ignored.
    /// </summary>
    public class MillingOptions
    {
        /// <summary>Path to the G-code file to mill.</summary>
        public string? FilePath { get; set; }

        /// <summary>
        /// Depth adjustment in mm (negative = deeper).
        /// Applied as offset to work coordinate Z origin.
        /// </summary>
        public float DepthAdjustment { get; set; }

        /// <summary>
        /// Whether to home the machine if not already homed.
        /// Default: true.
        /// </summary>
        public bool RequireHoming { get; set; } = true;

        /// <summary>
        /// Whether to skip user confirmations (for Web UI).
        /// Default: false (TUI shows confirmations).
        /// </summary>
        public bool SkipConfirmation { get; set; }
    }
}
