#nullable enable
using coppercli.Core.Communication;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Record whether homing completed and the reason GRBL gave if it failed. Show
    /// <c>Reason</c> to the operator when available.
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
