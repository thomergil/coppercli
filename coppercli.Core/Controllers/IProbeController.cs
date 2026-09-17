#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using coppercli.Core.GCode;
using coppercli.Core.Util;

namespace coppercli.Core.Controllers
{
    public interface IProbeController : IController
    {
        ProbePhase Phase { get; }

        bool IsTracingOutline { get; }

        /// <summary>True while this run is measuring the grid, which is the only mode with
        /// progress to show.</summary>
        bool IsMeasuringGrid { get; }

        ProbeGrid? Grid { get; }

        int PointsCompleted { get; }

        int TotalPoints { get; }

        /// <summary>Zero-based.</summary>
        int CurrentPointIndex { get; }

        ProbeOptions Options { get; set; }

        event Action<ProbePhase>? PhaseChanged;

        /// <summary>
        /// Raised once per probed point, with the zero-based point index, its X and Y, and the
        /// measured Z height.
        /// </summary>
        event Action<int, Vector2, double>? PointCompleted;

        /// <summary>Continues an interrupted session from the points it already has.</summary>
        void LoadGrid(ProbeGrid grid);

        ProbeGrid? GetGrid();

        /// <summary>Moves at Options.TraceHeight and Options.TraceFeed.</summary>
        Task TraceOutlineAsync(CancellationToken ct);

        /// <summary>
        /// Probes Z once where the tool already is, using Options.MaxDepth and
        /// Options.ProbeFeed. It leaves the controller's state untouched, and ZPosition comes
        /// back in work coordinates.
        /// </summary>
        Task<(bool Success, double ZPosition)> ProbeZSingleAsync(CancellationToken ct);
    }

    public class ProbeOptions
    {
        /// <summary>
        /// Every call site builds its options here, so a single-point probe, a grid probe and
        /// an outline trace carry the same values and cannot drift. The controller reads only
        /// the fields it needs.
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


        /// <summary>Z travel height between points, in millimeters in work coordinates.</summary>
        public double SafeHeight { get; set; } = Constants.RetractZMm;

        /// <summary>How far below the current Z to search for the surface (mm, positive).</summary>
        public double MaxDepth { get; set; } = 10.0;

        /// <summary>Millimeters per minute.</summary>
        public double ProbeFeed { get; set; } = 50.0;

        /// <summary>How far to retract after a probe, in millimeters.</summary>
        public double MinimumHeight { get; set; } = 1.0;

        /// <summary>False skips a failed point and carries on.</summary>
        public bool AbortOnFail { get; set; } = true;

        /// <summary>Weights X distance when sorting the probe order into a serpentine.</summary>
        public double XAxisWeight { get; set; } = 1.0;

        /// <summary>Millimeters in work coordinates, read only when TraceOutline is set.</summary>
        public double TraceHeight { get; set; } = Constants.RetractZMm;

        /// <summary>Millimeters per minute.</summary>
        public double TraceFeed { get; set; } = 500.0;

        public bool TraceOutline { get; set; }

        /// <summary>
        /// How far a probed height may sit from its measured neighbors before the run
        /// pauses for the operator (mm). Set to 0 to accept every height.
        /// </summary>
        public double HeightDeviationTolerance { get; set; } =
            ControllerConstants.ProbeHeightDeviationToleranceMm;
    }
}
