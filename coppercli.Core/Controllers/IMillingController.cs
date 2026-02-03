#nullable enable
using System;
using System.Collections.Generic;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Milling-specific controller interface.
    /// Extends IController with milling-specific state and events.
    /// </summary>
    public interface IMillingController : IController
    {
        /// <summary>Current phase within the milling workflow.</summary>
        MillingPhase Phase { get; }

        /// <summary>Lines of G-code completed.</summary>
        int LinesCompleted { get; }

        /// <summary>Total lines in file.</summary>
        int TotalLines { get; }

        /// <summary>
        /// Fired when M6 tool change is detected.
        /// Milling pauses until tool change is handled and Resume() is called.
        /// </summary>
        event Action<ToolChangeInfo>? ToolChangeDetected;

        /// <summary>Configuration for this milling operation.</summary>
        MillingOptions Options { get; set; }

        /// <summary>
        /// Work coordinates where cutting occurred (Z below threshold).
        /// Client maps these to grid cells based on display size.
        /// </summary>
        IReadOnlyList<(double X, double Y)> CuttingPath { get; }
    }
}
