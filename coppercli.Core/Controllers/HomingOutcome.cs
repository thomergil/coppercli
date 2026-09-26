#nullable enable
using coppercli.Core.Communication;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Record whether homing completed, and why not if it did not. Show
    /// <see cref="FailureMessage"/> to the operator.
    /// </summary>
    public readonly record struct HomingOutcome
    {
        /// <summary>Why homing stopped when GRBL did not refuse it.</summary>
        private readonly string? _interruption;

        private HomingOutcome(bool success, string? interruption, GrblRejection? rejection)
        {
            Success = success;
            _interruption = interruption;
            Rejection = rejection;
        }

        public bool Success { get; }

        /// <summary>GRBL's refusal of $H, or null if it did not refuse it.</summary>
        public GrblRejection? Rejection { get; }

        public static readonly HomingOutcome Homed = new(true, null, null);

        public static HomingOutcome Interrupted(string reason) => new(false, reason, null);

        /// <summary>GRBL refused `$H`, or it was never sent. Either way no motion happened.</summary>
        public static HomingOutcome Refused(GrblRejection? rejection) => new(false, null, rejection);

        /// <summary>
        /// GRBL refused $H because the door is open. A caller can retry homing once the door
        /// is closed.
        /// </summary>
        public bool DoorOpen => Rejection?.Code == GrblRejection.DoorOpen;

        /// <summary>
        /// error:5 means a setting disables the command; for $H that setting is $22, which
        /// only this refusal can name.
        /// </summary>
        public string? Reason => Rejection is { } rejection
            ? rejection.Code == GrblRejection.HomingNotEnabled
                ? ControllerConstants.ErrorHomingDisabledOnMachine
                : MachineWait.DescribeRefusal(rejection)
            : _interruption;

        /// <summary>What to tell the operator, or null if the machine homed.</summary>
        public string? FailureMessage =>
            Success ? null
            : Reason == null ? ControllerConstants.ErrorHomingFailed
            : string.Format(ControllerConstants.ErrorHomingFailedBecause, Reason);
    }
}
