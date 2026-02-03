#nullable enable
using System;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Error information emitted by controllers.
    /// </summary>
    public record ControllerError(
        /// <summary>Human-readable error message.</summary>
        string Message,

        /// <summary>Original exception, if any.</summary>
        Exception? Exception = null,

        /// <summary>True if operation cannot continue, false if recoverable.</summary>
        bool IsFatal = true
    );
}
