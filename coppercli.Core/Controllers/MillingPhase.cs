namespace coppercli.Core.Controllers
{
    /// <summary>
    /// The step of work a milling run is on.
    ///
    /// This names what the machine is doing, never the state of the run.
    /// <see cref="ControllerState"/> says whether a run is paused, waiting on a person,
    /// finishing or finished; read it through the predicates on
    /// <see cref="ControllerBase"/>. Naming one of those here too would let the two be set
    /// apart and disagree, and <see cref="ToolChange"/> is the value Resume reads to decide
    /// whether to skip the M0 pcb2gcode emits after an M6.
    /// </summary>
    public enum MillingPhase
    {
        /// <summary>No step under way.</summary>
        NotStarted,

        /// <summary>Waiting for the machine to stabilize in Idle.</summary>
        Settling,

        /// <summary>Homing the machine.</summary>
        Homing,

        /// <summary>Retracting Z to safe height.</summary>
        Retracting,

        /// <summary>Setting up coordinate modes and the depth adjustment.</summary>
        ConfiguringMachine,

        /// <summary>Streaming the G-code file to the machine.</summary>
        Milling,

        /// <summary>Handling an M6 tool change.</summary>
        ToolChange
    }
}
