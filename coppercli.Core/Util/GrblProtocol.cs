// Extracted from Machine.cs and Program.cs

namespace coppercli.Core.Util
{
    /// <summary>
    /// GRBL protocol constants including response strings, status fields, control characters, and commands.
    /// </summary>
    public static class GrblProtocol
    {
        // =========================================================================
        // Response strings (from GRBL to host)
        // =========================================================================
        public const string ResponseOk = "ok";
        public const string ResponseErrorPrefix = "error:";
        public const string ResponseProbePrefix = "[PRB:";
        public const string ResponseAlarmPrefix = "ALARM";
        public const string ResponseGrblPrefix = "grbl";
        public const string ResponseTloPrefix = "[TLO:";

        // =========================================================================
        // Status field names (in status report)
        // =========================================================================
        public const string FieldOverride = "Ov";
        public const string FieldWorkCoordOffset = "WCO";
        public const string FieldBuffer = "Bf";
        public const string FieldPins = "Pn";
        public const string FieldFeed = "F";
        public const string FieldFeedSpindle = "FS";
        public const string FieldMachinePos = "MPos";
        public const string FieldWorkPos = "WPos";

        // =========================================================================
        // Status values (machine states)
        // =========================================================================
        public const string StatusIdle = "Idle";
        public const string StatusRun = "Run";
        public const string StatusHome = "Home";
        public const string StatusHold = "Hold";
        public const string StatusAlarm = "Alarm";
        public const string StatusDoor = "Door";
        public const string StatusDisconnected = "Disconnected";

        // =========================================================================
        // Control characters (real-time commands, sent without newline)
        // =========================================================================
        public const char SoftReset = (char)0x18;
        public const char JogCancel = (char)0x85;
        public const char FeedHold = '!';
        public const char CycleStart = '~';
        public const char StatusQuery = '?';

        // =========================================================================
        // Feedrate override (real-time commands)
        // =========================================================================
        public const char FeedOverrideReset = (char)0x90;      // Reset to 100%
        public const char FeedOverrideIncrease10 = (char)0x91; // Increase 10%
        public const char FeedOverrideDecrease10 = (char)0x92; // Decrease 10%
        public const char FeedOverrideIncrease1 = (char)0x93;  // Increase 1%
        public const char FeedOverrideDecrease1 = (char)0x94;  // Decrease 1%

        // =========================================================================
        // System commands
        // =========================================================================
        public const string CmdHome = "$H";
        public const string CmdUnlock = "$X";
        public const string CmdViewGCodeState = "$G";
        public const string CmdViewParameters = "$#";

        // =========================================================================
        // G-code commands
        // =========================================================================
        public const string CmdAbsolute = "G90";
        public const string CmdRelative = "G91";
        public const string CmdRapidMove = "G0";
        public const string CmdLinearMove = "G1";
        public const string CmdProbeToward = "G38.3";  // Probe toward workpiece, stop on contact (no error if no contact)

        // =========================================================================
        // M-code commands
        // =========================================================================
        public const string CmdSpindleOff = "M5";

        // =========================================================================
        // Work coordinate system commands
        // =========================================================================
        public const string CmdZeroWorkOffset = "G10 L20 P0";  // Zero work offset (add axis letters after)
        public const string CmdSetWorkOffset = "G10 L2 P1";    // Set G54 work offset directly (add axis=value after)
        public const string CmdMachineCoords = "G53";          // Use machine coordinates for next move
        public const string CmdToolLengthOffset = "G43.1";     // Apply tool length offset (volatile, lost on reset)

        // =========================================================================
        // M-code and T-code patterns (for detection)
        // =========================================================================
        public const string M6Pattern = @"\bM0*6\b";           // M6 or M06 tool change command
        public const string TCodePattern = @"\bT(\d+)";        // T1, T01, T12 etc. - captures tool number

        // =========================================================================
        // Pin state characters (in Pn: field)
        // =========================================================================
        public const char PinLimitX = 'X';
        public const char PinLimitY = 'Y';
        public const char PinLimitZ = 'Z';
        public const char PinProbe = 'P';

        // =========================================================================
        // Jog command format
        // =========================================================================
        public const string JogPrefix = "$J=";
    }
}
