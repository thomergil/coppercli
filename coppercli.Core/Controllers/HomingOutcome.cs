#nullable enable
using coppercli.Core.Communication;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Whether homing completed and, when it did not, the reason GRBL gave. "Homing failed" on
    /// its own leaves the operator guessing, so `Reason` carries the cause to the screen.
    /// </summary>
    public readonly record struct HomingOutcome(bool Success, string? Reason)
    {
        public static readonly HomingOutcome Homed = new(true, null);

        public static HomingOutcome Interrupted(string reason) => new(false, reason);

        /// <summary>GRBL rejected `$H` outright, so no motion happened.</summary>
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
