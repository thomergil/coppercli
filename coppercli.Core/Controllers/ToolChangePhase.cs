namespace coppercli.Core.Controllers
{
    /// <summary>
    /// The step of work a tool change is on: physical operations, never the run's own state,
    /// which is <see cref="ControllerState"/>. Two phases wait on the operator and each screen
    /// shows something different - WaitingForToolChange prompts for the tool,
    /// WaitingForZeroZ offers the jog screen - and in every other phase the machine is moving.
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
