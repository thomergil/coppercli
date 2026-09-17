namespace coppercli.Core.Controllers
{
    /// <summary>
    /// What the machine is doing, in the cases a display or a control needs to tell apart.
    /// <see cref="MachineWait.GetActivity"/> is the only place these are decided.
    /// </summary>
    public enum MachineActivity
    {
        /// <summary>No link, or GRBL has not answered since the port opened.</summary>
        Disconnected,

        /// <summary>Alarmed. Nothing moves until the alarm is cleared with $X.</summary>
        Alarm,

        /// <summary>The enclosure may be open.</summary>
        DoorOpen,

        /// <summary>The door is closed and the machine is parked, waiting to be resumed.</summary>
        DoorHolding,

        /// <summary>Restoring from the park: the tool is moving back and the spindle is starting.</summary>
        DoorResuming,

        /// <summary>Held at a feed hold, waiting for a cycle start.</summary>
        Hold,

        /// <summary>Executing.</summary>
        Running,

        /// <summary>Stopped and ready.</summary>
        Idle,

        /// <summary>
        /// Asleep ($SLP). The steppers and spindle are off and GRBL accepts nothing but a
        /// soft reset, so no command from a screen has any effect.
        /// </summary>
        Sleep,

        /// <summary>
        /// GRBL's Jog, Home and Check. Screens show GRBL's own word for these and leave the
        /// controls enabled. A new GRBL state that should disable them needs its own member
        /// and an entry in <see cref="MachineWait.NeedsAttention"/>, not this fallback.
        /// </summary>
        Other
    }
}
