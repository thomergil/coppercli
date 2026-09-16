#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using coppercli.Core.GCode;
using coppercli.Core.Util;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Interface for grid probing controller.
    /// Enables unit testing with mocks.
    /// </summary>
    public interface IProbeController : IController
    {
        /// <summary>Current phase within the probing workflow.</summary>
        ProbePhase Phase { get; }

        /// <summary>True while this run is tracing the grid outline rather than measuring.</summary>
        bool IsTracingOutline { get; }

        /// <summary>True while this run is measuring the grid, which is the only mode with
        /// progress to show.</summary>
        bool IsMeasuringGrid { get; }

        /// <summary>The probe grid being populated.</summary>
        ProbeGrid? Grid { get; }

        /// <summary>Number of points probed.</summary>
        int PointsCompleted { get; }

        /// <summary>Total number of points to probe.</summary>
        int TotalPoints { get; }

        /// <summary>Current point being probed (0-indexed).</summary>
        int CurrentPointIndex { get; }

        /// <summary>Probing options.</summary>
        ProbeOptions Options { get; set; }

        /// <summary>
        /// Fired when phase changes within the probing workflow.
        /// </summary>
        event Action<ProbePhase>? PhaseChanged;

        /// <summary>
        /// Fired when a probe point is completed.
        /// Reports point index (0-based), coordinates (X, Y), and measured Z height.
        /// </summary>
        event Action<int, Vector2, double>? PointCompleted;

        /// <summary>
        /// Load an existing probe grid (for continuing interrupted sessions).
        /// </summary>
        void LoadGrid(ProbeGrid grid);

        /// <summary>
        /// Get the current probe grid for persistence.
        /// </summary>
        ProbeGrid? GetGrid();

        /// <summary>
        /// Trace the outline of the loaded grid.
        /// Uses Options.TraceHeight and Options.TraceFeed for movement parameters.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        Task TraceOutlineAsync(CancellationToken ct);

        /// <summary>
        /// Perform a single Z probe at the current XY position.
        /// Uses Options.MaxDepth and Options.ProbeFeed for probe parameters.
        /// Does not modify controller state (this is a standalone operation).
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Tuple of (success, Z position in work coordinates).</returns>
        Task<(bool Success, double ZPosition)> ProbeZSingleAsync(CancellationToken ct);
    }

    /// <summary>
    /// Options for probe controller.
    /// </summary>
    public class ProbeOptions
    {
        /// <summary>
        /// Builds the full set of probe options from settings. Every call site uses this,
        /// so a single-point probe, a grid probe and an outline trace all carry the same
        /// values and cannot drift - the controller reads only the fields it needs.
        /// </summary>
        public static ProbeOptions FromSettings(Settings.MachineSettings settings,
            bool traceOutline = false)
        {
            return new ProbeOptions
            {
                SafeHeight = settings.ProbeSafeHeight,
                MaxDepth = settings.ProbeMaxDepth,
                ProbeFeed = settings.ProbeFeed,
                MinimumHeight = settings.ProbeMinimumHeight,
                AbortOnFail = settings.AbortOnProbeFail,
                XAxisWeight = settings.ProbeXAxisWeight,
                TraceHeight = settings.OutlineTraceHeight,
                TraceFeed = settings.OutlineTraceFeed,
                TraceOutline = traceOutline,
            };
        }


        /// <summary>Safe height for Z travel between points (mm in work coords).</summary>
        public double SafeHeight { get; set; } = Constants.RetractZMm;

        /// <summary>Maximum probe depth below current Z (mm, negative).</summary>
        public double MaxDepth { get; set; } = 10.0;

        /// <summary>Probe feed rate (mm/min).</summary>
        public double ProbeFeed { get; set; } = 50.0;

        /// <summary>Minimum height to retract after probe (mm).</summary>
        public double MinimumHeight { get; set; } = 1.0;

        /// <summary>If true, abort probing on first failed probe. Otherwise skip and continue.</summary>
        public bool AbortOnFail { get; set; } = true;

        /// <summary>Weight multiplier for X distance when sorting probe order (serpentine optimization).</summary>
        public double XAxisWeight { get; set; } = 1.0;

        /// <summary>Height for outline trace (mm, work coords). Only used if tracing enabled.</summary>
        public double TraceHeight { get; set; } = Constants.RetractZMm;

        /// <summary>Feed rate for outline trace (mm/min).</summary>
        public double TraceFeed { get; set; } = 500.0;

        /// <summary>If true, trace the outline before probing.</summary>
        public bool TraceOutline { get; set; }

        /// <summary>
        /// How far a probed height may sit from its already-measured neighbors before
        /// the run pauses for the operator (mm). Set to 0 to accept every height.
        /// </summary>
        public double HeightDeviationTolerance { get; set; } =
            ControllerConstants.ProbeHeightDeviationToleranceMm;
    }
}
