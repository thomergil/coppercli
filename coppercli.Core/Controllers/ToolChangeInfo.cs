#nullable enable
using coppercli.Core.Util;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// An M6 that `MillingController` found in the file, raised through its
    /// `ToolChangeDetected` event.
    /// </summary>
    public record ToolChangeInfo(
        int ToolNumber,

        string? ToolName,

        /// <summary>Work coordinates, not machine coordinates.</summary>
        Vector3 ReturnPosition,

        int LineNumber
    );
}
