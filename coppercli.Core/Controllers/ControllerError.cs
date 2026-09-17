#nullable enable
using System;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// What a run reports when something goes wrong. `Message` is worded for the operator and
    /// reaches the screen through `MenuHelpers.ShowRunError`; `Exception` goes to the log only.
    /// </summary>
    public record ControllerError(
        string Message,

        Exception? Exception = null,

        /// <summary>False when the run carries on after reporting this.</summary>
        bool IsFatal = true
    );
}
