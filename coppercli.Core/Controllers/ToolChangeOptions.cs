#nullable enable
using coppercli.Core.Util;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Configuration options for a tool change operation.
    /// Passed to HandleToolChangeAsync - must be set before each tool change.
    /// </summary>
    public class ToolChangeOptions
    {
        /// <summary>Builds tool-change options from settings and the loaded file's bounds.</summary>
        public static ToolChangeOptions FromSettings(Settings.MachineSettings settings, GCode.GCodeFile? file)
        {
            return new ToolChangeOptions
            {
                ProbeMaxDepth = settings.ProbeMaxDepth,
                ProbeFeed = settings.ProbeFeed,
                RetractHeight = Constants.RetractZMm,
                WorkAreaCenter = file != null && file.ContainsMotion ? file.Center : null,
            };
        }


        /// <summary>
        /// Maximum probe depth for PCB surface probing (mm, positive value).
        /// Used in non-tool-setter mode when probing the PCB surface.
        /// </summary>
        public double ProbeMaxDepth { get; set; } = 5.0;

        /// <summary>
        /// Probe feed rate for PCB surface probing (mm/min).
        /// Used in non-tool-setter mode.
        /// </summary>
        public double ProbeFeed { get; set; } = 20.0;

        /// <summary>
        /// Height to retract after probing (mm, work coordinates).
        /// Used in non-tool-setter mode after zeroing Z.
        /// </summary>
        public double RetractHeight { get; set; } = 6.0;

        /// <summary>
        /// Center of work area for tool swap positioning (work coordinates).
        /// If null, uses the return position (where M6 was encountered).
        /// Set this to file bounds center for accessible tool swap location.
        /// </summary>
        public Vector3? WorkAreaCenter { get; set; }
    }
}
