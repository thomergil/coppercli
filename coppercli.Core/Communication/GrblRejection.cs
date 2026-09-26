namespace coppercli.Core.Communication
{
    /// <summary>
    /// A command GRBL refused, and why. Waiting for Idle cannot separate a refusal from a
    /// command still running, because a refused command never moved the machine and that
    /// wait succeeds at once.
    /// </summary>
    public readonly record struct GrblRejection(int Code, string Command, string Description)
    {
        /// <summary>GRBL error:5 - the homing cycle is disabled in the machine's settings ($22).</summary>
        public const int HomingNotEnabled = 5;

        /// <summary>GRBL error:9 - G-code is locked out while alarmed or jogging.</summary>
        public const int LockedOut = 9;

        /// <summary>
        /// GRBL error:13 - the door is open. GRBL refuses $X and $H until it closes, and while
        /// alarmed it reports no Door state, so this answer is the only sign of it.
        /// </summary>
        public const int DoorOpen = 13;
    }
}
