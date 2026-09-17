#nullable enable
using coppercli.Core.Util;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Set this before every call to `HandleToolChangeAsync`; it is read once, at the start.
    /// </summary>
    public class ToolChangeOptions
    {
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
        /// Millimeters, positive. Used without a tool setter, when probing the PCB surface.
        /// </summary>
        public double ProbeMaxDepth { get; set; } = 5.0;

        /// <summary>Millimeters per minute. Used without a tool setter.</summary>
        public double ProbeFeed { get; set; } = 20.0;

        /// <summary>
        /// Millimeters in work coordinates. Used without a tool setter, after Z is zeroed.
        /// </summary>
        public double RetractHeight { get; set; } = 6.0;

        /// <summary>
        /// Where the tool is parked for the swap, in work coordinates. Null falls back to the
        /// return position, which is wherever the M6 was reached.
        /// </summary>
        public Vector3? WorkAreaCenter { get; set; }
    }
}
