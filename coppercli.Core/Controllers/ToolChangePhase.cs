namespace coppercli.Core.Controllers
{
    /// <summary>
    /// FSM states for tool change workflow.
    /// Models real-world physical operations, not UI states.
    ///
    /// Two modes exist:
    ///   Mode A (with tool setter): automatic measurement
    ///   Mode B (without tool setter): manual Z re-zeroing
    ///
    /// Mode A flow:
    ///   NotStarted → RaisingZ → MovingToToolSetter → MeasuringReference
    ///   → RaisingZ → MovingToWorkArea → WaitingForToolChange
    ///   → MovingToToolSetter → MeasuringNewTool → ApplyingOffset
    ///   → Returning → Complete
    ///
    /// Mode B flow:
    ///   NotStarted → RaisingZ → MovingToWorkArea → WaitingForToolChange
    ///   → WaitingForZeroZ → Complete
    ///
    /// UI behavior per phase:
    ///   WaitingForToolChange → show overlay: "Change tool T{N}, press Continue"
    ///   WaitingForZeroZ → jog screen shows "Continue Milling" button
    ///   All other phases → spindle moving autonomously, no user action needed
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
        Returning,

        /// <summary>Tool change complete - ready to resume milling.</summary>
        Complete
    }
}
