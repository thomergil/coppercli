#nullable enable
using coppercli.Core.Util;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Information about a tool change request from M6 detection.
    /// Emitted via MillingController.ToolChangeDetected event.
    /// </summary>
    public record ToolChangeInfo(
        /// <summary>Tool number from M6 Tn command.</summary>
        int ToolNumber,

        /// <summary>Tool name if known, null otherwise.</summary>
        string? ToolName,

        /// <summary>Position to return to after tool change (work coordinates).</summary>
        Vector3 ReturnPosition,

        /// <summary>Line number in file where M6 was detected.</summary>
        int LineNumber
    );
}
