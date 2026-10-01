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
        /// The run's progress and time left, from where the tool is on the G-code; null before
        /// the run has worked it out, or when it could not. After the run ends it stays as it
        /// last was, all done for a completed run, until the controller is released.
        /// </summary>
        JobEstimate? Estimate { get; }

        /// <summary>
        /// Raised on an M6 in the file, once the run has asked about the sections and depth where
        /// they apply and the operator has not stopped it. Milling stays paused until the change
        /// is handled and Resume() is called.
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
