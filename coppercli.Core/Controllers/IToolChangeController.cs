#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Controller for tool change workflow.
    /// Called by MillingController when M6 is detected.
    /// Handles both tool setter and non-tool setter workflows.
    /// </summary>
    public interface IToolChangeController : IController
    {
        /// <summary>Current phase within tool change workflow.</summary>
        ToolChangePhase Phase { get; }

        /// <summary>Whether a tool setter is configured.</summary>
        bool HasToolSetter { get; }

        /// <summary>Information about the current tool change request.</summary>
        ToolChangeInfo? CurrentToolChange { get; }

        /// <summary>Configuration options for this tool change.</summary>
        ToolChangeOptions Options { get; set; }

        /// <summary>
        /// Handle a tool change request from milling controller.
        /// Returns true if tool change completed successfully, false if aborted.
        /// </summary>
        Task<bool> HandleToolChangeAsync(ToolChangeInfo info, CancellationToken ct = default);
    }
}
