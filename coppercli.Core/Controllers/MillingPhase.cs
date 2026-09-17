namespace coppercli.Core.Controllers
{
    /// <summary>
    /// The step of work a milling run is on, never the state of the run itself: whether it is
    /// paused, waiting on the operator or finished lives in <see cref="ControllerState"/>, and
    /// naming it here too would let the two be set independently and disagree.
    /// <see cref="ToolChange"/> is the value Resume reads to decide whether to skip the M0
    /// that pcb2gcode emits after an M6.
    /// </summary>
    public enum MillingPhase
    {
        NotStarted,

        /// <summary>Waiting for the machine to stabilize in Idle.</summary>
        Settling,

        Homing,

        /// <summary>Raising Z to the safe height.</summary>
        Retracting,

        /// <summary>Setting up coordinate modes and the depth adjustment.</summary>
        ConfiguringMachine,

        /// <summary>Streaming the G-code file to the machine.</summary>
        Milling,

        ToolChange
    }
}
