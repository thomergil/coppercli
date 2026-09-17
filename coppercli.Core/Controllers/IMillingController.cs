#nullable enable
using System;
using System.Collections.Generic;

namespace coppercli.Core.Controllers
{
    public interface IMillingController : IController
    {
        MillingPhase Phase { get; }

        int LinesCompleted { get; }

        int TotalLines { get; }

        /// <summary>
        /// Raised on an M6 in the file. Milling stays paused until the change is handled and
        /// Resume() is called.
        /// </summary>
        event Action<ToolChangeInfo>? ToolChangeDetected;

        MillingOptions Options { get; set; }

        /// <summary>
        /// Work coordinates where Z went below the cutting threshold. The client maps these to
        /// grid cells for whatever display size it has.
        /// </summary>
        IReadOnlyList<(double X, double Y)> CuttingPath { get; }
    }
}
