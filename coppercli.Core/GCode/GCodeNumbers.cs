namespace coppercli.Core.GCode
{
    /// <summary>
    /// G-code and M-code numeric constants used for parsing and interpretation.
    /// These are the numeric values that appear after G or M in G-code commands.
    /// For command strings like "G90" or "M5", see GrblProtocol.cs.
    /// </summary>
    public static class GCodeNumbers
    {
        // =========================================================================
        // Dwell (G4)
        // =========================================================================

        /// <summary>G4: Dwell. Pauses for specified time (P parameter in seconds).</summary>
        public const int Dwell = 4;

        // =========================================================================
        // Plane selection (G17-G19)
        // Determines which two axes form the working plane for arcs and canned cycles.
        // =========================================================================

        /// <summary>G17: XY plane (default). Z is the tool axis.</summary>
        public const int PlaneXY = 17;

        /// <summary>G18: YZ plane. X is the tool axis.</summary>
        public const int PlaneYZ = 18;

        /// <summary>G19: ZX plane. Y is the tool axis.</summary>
        public const int PlaneZX = 19;

        // =========================================================================
        // Units (G20-G21)
        // =========================================================================

        /// <summary>G20: Interpret coordinates as inches.</summary>
        public const int UnitsInches = 20;

        /// <summary>G21: Interpret coordinates as millimeters.</summary>
        public const int UnitsMillimeters = 21;

        // =========================================================================
        // Home / predefined positions (G28-G30)
        // =========================================================================

        /// <summary>G28: Return to home position (may crash into workpiece).</summary>
        public const int Home = 28;

        /// <summary>G30: Return to secondary home position (may crash into workpiece).</summary>
        public const int HomeSecondary = 30;

        // =========================================================================
        // Probing (G38.x)
        // =========================================================================

        /// <summary>G38.2: Probe toward workpiece, stop on contact, signal error if no contact.</summary>
        public const double ProbeToward = 38.2;

        /// <summary>G38.3: Probe toward workpiece, stop on contact, no error if no contact.</summary>
        public const double ProbeTowardNoError = 38.3;

        /// <summary>G38.4: Probe away from workpiece, stop on loss of contact, signal error if no loss.</summary>
        public const double ProbeAway = 38.4;

        /// <summary>G38.5: Probe away from workpiece, stop on loss of contact, no error if no loss.</summary>
        public const double ProbeAwayNoError = 38.5;

        // =========================================================================
        // Coordinate systems (G10, G53-G59)
        // =========================================================================

        /// <summary>G10: Set work offset (coordinate system data).</summary>
        public const int SetWorkOffset = 10;

        /// <summary>G53: Move in machine coordinates (non-modal, applies to one line only).</summary>
        public const int MachineCoordinates = 53;

        // =========================================================================
        // Distance mode (G90-G91)
        // =========================================================================

        /// <summary>G90: Absolute positioning. Coordinates are relative to work origin.</summary>
        public const int DistanceAbsolute = 90;

        /// <summary>G91: Incremental positioning. Coordinates are relative to current position.</summary>
        public const int DistanceIncremental = 91;

        // =========================================================================
        // Arc distance mode (G90.1-G91.1)
        // =========================================================================

        /// <summary>G90.1: Absolute arc center mode (IJK are absolute coordinates).</summary>
        public const double ArcDistanceAbsolute = 90.1;

        /// <summary>G91.1: Incremental arc center mode (IJK are relative to start point). Default.</summary>
        public const double ArcDistanceIncremental = 91.1;

        // =========================================================================
        // Feed rate mode (G93-G94)
        // =========================================================================

        /// <summary>G93: Inverse time feed rate mode. F specifies time to complete move.</summary>
        public const int FeedRateInverseTime = 93;

        /// <summary>G94: Units per minute feed rate mode (default). F specifies distance/minute.</summary>
        public const int FeedRateUnitsPerMinute = 94;

        // =========================================================================
        // Tool length offset (G43.1, G49)
        // =========================================================================

        /// <summary>G49: Cancel tool length offset.</summary>
        public const int ToolLengthOffsetCancel = 49;

        /// <summary>G43.1: Apply dynamic tool length offset (value follows Z parameter).</summary>
        public const double ToolLengthOffsetDynamic = 43.1;

        // =========================================================================
        // M-codes that pause or stop execution
        // =========================================================================

        /// <summary>M0: Program stop. Pauses execution until cycle start.</summary>
        public const int MCodeProgramStop = 0;

        /// <summary>M1: Optional stop. Pauses only if optional stop switch is on.</summary>
        public const int MCodeProgramOptionalStop = 1;

        /// <summary>M2: Program end. Stops execution and resets to start of program.</summary>
        public const int MCodeProgramEnd = 2;

        /// <summary>M30: Program end and reset. Same as M2 in GRBL.</summary>
        public const int MCodeProgramEndReset = 30;

        /// <summary>M6: Tool change. Pauses for manual tool change.</summary>
        public const int MCodeToolChange = 6;

        /// <summary>
        /// M-codes that pause file execution.
        /// Used to detect pause lines when scanning G-code files.
        /// </summary>
        public static readonly int[] PauseMCodes =
        {
            MCodeProgramStop,
            MCodeProgramOptionalStop,
            MCodeProgramEnd,
            MCodeProgramEndReset,
            MCodeToolChange
        };

        /// <summary>
        /// Checks if an M-code causes execution to pause.
        /// </summary>
        public static bool IsPauseMCode(int code) =>
            code == MCodeProgramStop ||
            code == MCodeProgramOptionalStop ||
            code == MCodeProgramEnd ||
            code == MCodeProgramEndReset ||
            code == MCodeToolChange;
    }
}
