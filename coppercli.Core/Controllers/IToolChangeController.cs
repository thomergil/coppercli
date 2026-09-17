#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// The tool change workflow, entered by `MillingController` when it reaches an M6.
    /// </summary>
    public interface IToolChangeController : IController
    {
        ToolChangePhase Phase { get; }

        bool HasToolSetter { get; }

        ToolChangeInfo? CurrentToolChange { get; }

        ToolChangeOptions Options { get; set; }

        /// <summary>Returns false when the change was aborted.</summary>
        Task<bool> HandleToolChangeAsync(ToolChangeInfo info, CancellationToken ct = default);
    }
}
