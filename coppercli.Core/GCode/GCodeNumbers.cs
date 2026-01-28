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
        // Distance mode (G90-G91)
        // =========================================================================

        /// <summary>G90: Absolute positioning. Coordinates are relative to work origin.</summary>
        public const int DistanceAbsolute = 90;

        /// <summary>G91: Incremental positioning. Coordinates are relative to current position.</summary>
        public const int DistanceIncremental = 91;

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
