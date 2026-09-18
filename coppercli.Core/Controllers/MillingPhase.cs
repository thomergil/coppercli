namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Name each step of milling; <see cref="ControllerState"/> records whether the run
    /// is paused, waiting for input, or finished; do not duplicate those states here.
    /// Resume checks <see cref="ToolChange"/>
    /// to skip the M0 that pcb2gcode emits after M6.
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
