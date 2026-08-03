namespace coppercli.Core.Communication
{
    /// <summary>
    /// A command GRBL refused, and why.
    ///
    /// Rejections used to reach only a display handler, so a controller waiting on a
    /// command could not tell "it was refused" from "it is still running" - and waiting
    /// for Idle after a refusal succeeds immediately, because the machine never moved.
    /// This carries the answer to whoever asked for the command.
    /// </summary>
    public readonly record struct GrblRejection(int Code, string Command, string Description)
    {
        /// <summary>GRBL error:5 - the homing cycle is disabled in the machine's settings ($22).</summary>
        public const int HomingNotEnabled = 5;

        /// <summary>GRBL error:9 - G-code is locked out while alarmed or jogging.</summary>
        public const int LockedOut = 9;
    }
}
