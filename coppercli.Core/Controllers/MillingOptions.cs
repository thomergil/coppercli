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
        /// <param name="enclosureConfirmed">
        /// Whether the operator has just answered for the enclosure - see
        /// <see cref="EnclosureConfirmed"/>. Required rather than defaulted, so a caller that
        /// starts a job without asking cannot inherit an answer the operator never gave.
        /// </param>
        public static MillingOptions Create(string? filePath, double depthAdjustment,
            bool machineIsHomed, bool enclosureConfirmed)
        {
            return new MillingOptions
            {
                FilePath = filePath,
                DepthAdjustment = depthAdjustment,
                RequireHoming = !machineIsHomed,
                EnclosureConfirmed = enclosureConfirmed,
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

        /// <summary>
        /// The operator has just said the enclosure is clear of probing equipment - the
        /// question both front ends ask to start a job. That answer releases a door hold an
        /// earlier run left closed and parked, instead of asking them about the enclosure a
        /// second time for a job that has not moved anything yet. It covers that one
        /// release: a door opened later in the run is put to them as usual.
        ///
        /// False releases nothing, which is what a caller that did not ask must get, so the
        /// hold reaches the operator as a prompt.
        /// </summary>
        public bool EnclosureConfirmed { get; set; }
    }
}
