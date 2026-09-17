#nullable enable
namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Read once, at StartAsync. A change made while the run is going has no effect.
    /// </summary>
    public class MillingOptions
    {
        /// <summary>The RequireHoming = not-yet-homed rule is written here once, so both front
        /// ends enforce it identically.</summary>
        public static MillingOptions Create(string? filePath, double depthAdjustment,
            bool machineIsHomed)
        {
            return new MillingOptions
            {
                FilePath = filePath,
                DepthAdjustment = depthAdjustment,
                RequireHoming = !machineIsHomed,
            };
        }


        public string? FilePath { get; set; }

        /// <summary>
        /// How long the settling phase waits for the machine to stop and stay stopped.
        /// Only tests set this, to avoid the full timeout on a machine that will not
        /// settle.
        /// </summary>
        internal int SettleTimeoutMs { get; set; } = Util.Constants.SettleTimeoutMs;

        /// <summary>
        /// Millimeters, negative for deeper. Applied as an offset to the work coordinate Z
        /// origin.
        /// </summary>
        public double DepthAdjustment { get; set; }

        public bool RequireHoming { get; set; } = true;
    }
}
