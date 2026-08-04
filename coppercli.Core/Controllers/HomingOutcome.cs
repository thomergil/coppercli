#nullable enable
using coppercli.Core.Communication;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Whether homing completed, and if not, what the machine said about it.
    ///
    /// "Homing failed" on its own leaves the operator guessing. The common causes are
    /// distinguishable - GRBL names them - and the message should say which one it was.
    /// </summary>
    public readonly record struct HomingOutcome(bool Success, string? Reason)
    {
        public static readonly HomingOutcome Homed = new(true, null);

        /// <summary>Builds the failure, naming the cause when GRBL gave one.</summary>
        /// <summary>Homing stopped for a reason the operator can see and act on.</summary>
        public static HomingOutcome Interrupted(string reason) => new(false, reason);

        public static HomingOutcome Refused(GrblRejection? rejection)
        {
            if (rejection == null)
            {
                return new HomingOutcome(false, null);
            }

            string reason = rejection.Value.Code == GrblRejection.HomingNotEnabled
                ? ControllerConstants.ErrorHomingDisabledOnMachine
                : rejection.Value.Description;

            return new HomingOutcome(false, reason);
        }
    }
}
