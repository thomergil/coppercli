namespace coppercli.Core.Controllers
{
    /// <summary>
    /// The step of work a tool change is on. Physical operations, not what the screen
    /// shows, and never the run's own state: whether it finished is
    /// <see cref="ControllerState"/>'s to answer.
    ///
    /// With a tool setter, the offset is measured:
    ///   RaisingZ → MovingToToolSetter → MeasuringReference → RaisingZ
    ///   → MovingToWorkArea → WaitingForToolChange → MovingToToolSetter
    ///   → MeasuringNewTool → ApplyingOffset → Returning
    ///
    /// Without one, the operator re-zeroes Z by hand:
    ///   RaisingZ → MovingToWorkArea → WaitingForToolChange → WaitingForZeroZ
    ///
    /// Two phases wait on a person, and each puts a different thing on screen:
    /// WaitingForToolChange asks for the tool, WaitingForZeroZ offers the jog screen. In
    /// every other phase the machine is moving on its own.
    /// </summary>
    public enum ToolChangePhase
    {
        /// <summary>Idle - no tool change in progress.</summary>
        NotStarted,

        /// <summary>Raising Z to clearance height for safe travel.</summary>
        RaisingZ,

        /// <summary>Moving XY to tool setter position (Mode A only).</summary>
        MovingToToolSetter,

        /// <summary>Probing reference tool on tool setter (Mode A only).</summary>
        MeasuringReference,

        /// <summary>Moving XY to work area center for user access.</summary>
        MovingToWorkArea,

        /// <summary>
        /// Waiting for user to change tool (Mode A and B).
        /// User prompt: "Change to tool T{N}, press Continue"
        /// </summary>
        WaitingForToolChange,

        /// <summary>
        /// Waiting for user to set Z0 (Mode B only).
        /// User navigates to jog screen, sets Z0, clicks "Continue Milling".
        /// </summary>
        WaitingForZeroZ,

        /// <summary>Probing new tool on tool setter (Mode A only).</summary>
        MeasuringNewTool,

        /// <summary>Probing PCB surface after tool change.</summary>
        ProbingPCBSurface,

        /// <summary>Calculating and applying Z work offset.</summary>
        ApplyingOffset,

        /// <summary>Returning XY to original position.</summary>
        Returning
    }
}
