namespace coppercli.Core.Communication
{
    /// <summary>
    /// A command GRBL refused, and why.
    ///
    /// Delivered to the caller that sent the command, so a controller can tell refused from
    /// still running. Waiting for Idle cannot: a refused command never moved the machine, so
    /// that wait succeeds immediately.
    /// </summary>
    public readonly record struct GrblRejection(int Code, string Command, string Description)
    {
        /// <summary>GRBL error:5 - the homing cycle is disabled in the machine's settings ($22).</summary>
        public const int HomingNotEnabled = 5;

        /// <summary>GRBL error:9 - G-code is locked out while alarmed or jogging.</summary>
        public const int LockedOut = 9;
    }
}
