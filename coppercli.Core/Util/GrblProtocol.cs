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
        public const string ResponseG54Prefix = "[G54:";   // $# reports the G54 offset itself

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
        public const string StatusHold = "Hold";
        public const string StatusAlarm = "Alarm";
        public const string StatusDoor = "Door";

        // GRBL reports the door as Door:<n>. 0 = closed and ready to resume, 1 = ajar,
        // 2 = opened with a parking retract under way, 3 = closed and resuming. Without
        // the number "Door" cannot tell an open door from a closed one waiting on the
        // operator, and the display sits on "Door" after they have already closed it.
        public const string DoorSubStateClosed = "0";
        public const string DoorSubStateAjar = "1";
        public const string DoorSubStateOpening = "2";
        public const string DoorSubStateResuming = "3";
        public const string StatusDisconnected = "Disconnected";

        // =========================================================================
        // Control characters (real-time commands, sent without newline)
        // =========================================================================
        public const char SoftReset = (char)0x18;
        public const char JogCancel = (char)0x85;
        public const char FeedHold = '!';
        public const char CycleStart = '~';
        public const char StatusQuery = '?';

        // Feed override (real-time commands)
        public const char FeedOverrideReset = (char)0x90;      // Set 100% of programmed feed rate
        public const char FeedOverrideIncrease10 = (char)0x91; // Increase 10%
        public const char FeedOverrideDecrease10 = (char)0x92; // Decrease 10%

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
        public const string CmdPlaneXY = "G17";        // XY plane selection (standard for PCB milling)

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

        // =========================================================================
        // M-code and T-code patterns (for detection)
        // =========================================================================
        public const string M6Pattern = @"\bM0*6\b";           // M6 or M06 tool change command
        public const string M0Pattern = @"\bM0+\b";           // M0, M00, M000 - all program stop
        public const string TCodePattern = @"\bT(\d+)";        // T1, T01, T12 etc. - captures tool number

        // =========================================================================
        // Jog command format
        // =========================================================================
        public const string JogPrefix = "$J=";
    }
}
