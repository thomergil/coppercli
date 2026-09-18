namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Name each step of a tool change; <see cref="ControllerState"/> records run status.
    /// WaitingForToolChange prompts for the tool, and WaitingForZeroZ allows jogging.
    /// The machine moves during the other active phases.
    ///
    /// With a tool setter, the offset is measured:
    ///   RaisingZ → MovingToToolSetter → MeasuringReference → RaisingZ
    ///   → MovingToWorkArea → WaitingForToolChange → MovingToToolSetter
    ///   → MeasuringNewTool → ApplyingOffset → Returning
    ///
    /// Without one, the operator re-zeroes Z by hand:
    ///   RaisingZ → MovingToWorkArea → WaitingForToolChange → WaitingForZeroZ
    /// </summary>
    public enum ToolChangePhase
    {
        NotStarted,

        /// <summary>Raising Z to the clearance height, so XY travel is safe.</summary>
        RaisingZ,

        MovingToToolSetter,

        MeasuringReference,

        /// <summary>Parking where the operator can reach the spindle.</summary>
        MovingToWorkArea,

        WaitingForToolChange,

        WaitingForZeroZ,

        MeasuringNewTool,

        ProbingPCBSurface,

        /// <summary>Applying the measured difference to the Z work offset.</summary>
        ApplyingOffset,

        /// <summary>Moving XY back to where the M6 was reached.</summary>
        Returning
    }
}
