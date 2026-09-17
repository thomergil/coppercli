namespace coppercli.Core.Util
{
    /// <summary>
    /// The literal strings and bytes of the GRBL serial protocol, as GRBL sends them and as
    /// this code matches them.
    /// </summary>
    public static class GrblProtocol
    {
        public const string ResponseOk = "ok";
        public const string ResponseErrorPrefix = "error:";
        public const string ResponseProbePrefix = "[PRB:";
        public const string ResponseAlarmPrefix = "ALARM";
        public const string ResponseGrblPrefix = "grbl";
        public const string ResponseTloPrefix = "[TLO:";
        public const string ResponseG54Prefix = "[G54:";   // $# reports the G54 offset itself

        public const string FieldOverride = "Ov";
        public const string FieldWorkCoordOffset = "WCO";
        public const string FieldBuffer = "Bf";
        public const string FieldPins = "Pn";
        public const string FieldFeed = "F";
        public const string FieldFeedSpindle = "FS";
        public const string FieldMachinePos = "MPos";
        public const string FieldWorkPos = "WPos";

        public const string StatusIdle = "Idle";
        public const string StatusRun = "Run";
        public const string StatusHold = "Hold";
        public const string StatusAlarm = "Alarm";
        public const string StatusDoor = "Door";

        // States coppercli does not drive: GRBL reports them, screens show the word, and no
        // control changes.
        public const string StatusJog = "Jog";
        public const string StatusHome = "Home";
        public const string StatusCheck = "Check";

        // $SLP. Not one of the three above: GetActivity gives it its own member and
        // NeedsAttention disables the controls for it.
        public const string StatusSleep = "Sleep";

        // GRBL reports the door as Door:<n>: 0 closed and waiting for a cycle start, 1
        // ajar, 2 the parking retract running, 3 restoring from the park. Only 0 and 1 report
        // the switch, so 2 counts as open because GRBL has not re-read it, and 3 as closed
        // because it follows a cycle start the operator asked for.
        public const string DoorSubStateClosed = "0";
        public const string DoorSubStateAjar = "1";
        public const string DoorSubStateRetracting = "2";
        public const string DoorSubStateResuming = "3";

        // Hold and Alarm carry a number too. Nothing branches on either, so they are named
        // only where a test needs a machine that reported one.
        public const string HoldSubStateComplete = "0";
        public const string AlarmSubStateHardLimit = "1";

        public const string StatusDisconnected = "Disconnected";

        // Real-time commands: one byte each, sent without a newline and acted on ahead of
        // anything queued.
        public const char SoftReset = (char)0x18;
        public const char JogCancel = (char)0x85;
        public const char FeedHold = '!';
        public const char CycleStart = '~';
        public const char StatusQuery = '?';

        public const char FeedOverrideReset = (char)0x90;      // Back to 100% of the programmed feed
        public const char FeedOverrideIncrease10 = (char)0x91;
        public const char FeedOverrideDecrease10 = (char)0x92;

        public const string CmdHome = "$H";
        public const string CmdUnlock = "$X";
        public const string CmdViewGCodeState = "$G";
        public const string CmdViewParameters = "$#";

        public const string CmdAbsolute = "G90";
        public const string CmdRelative = "G91";
        public const string CmdRapidMove = "G0";
        public const string CmdLinearMove = "G1";
        public const string CmdProbeToward = "G38.3";  // Probe toward workpiece, stop on contact (no error if no contact)
        public const string CmdPlaneXY = "G17";

        public const string CmdSpindleOff = "M5";

        public const string CmdZeroWorkOffset = "G10 L20 P0";  // Zero work offset (add axis letters after)
        public const string CmdSetWorkOffset = "G10 L2 P1";    // Set G54 work offset directly (add axis=value after)
        public const string CmdMachineCoords = "G53";          // Use machine coordinates for next move

        public const string M6Pattern = @"\bM0*6\b";           // M6 or M06 tool change command
        public const string M0Pattern = @"\bM0+\b";           // M0, M00, M000 - all program stop
        public const string TCodePattern = @"\bT(\d+)";        // T1, T01, T12 etc. - captures tool number

        public const string JogPrefix = "$J=";
    }
}
