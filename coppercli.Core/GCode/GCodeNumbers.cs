// Extracted from Machine.cs

namespace coppercli.Core.GCode
{
    /// <summary>
    /// G-code and M-code numeric constants used for parsing and interpretation.
    /// </summary>
    public static class GCodeNumbers
    {
        // =========================================================================
        // Plane selection (G17-G19)
        // =========================================================================
        public const int PlaneXY = 17;
        public const int PlaneYZ = 18;
        public const int PlaneZX = 19;

        // =========================================================================
        // Units (G20-G21)
        // =========================================================================
        public const int UnitsInches = 20;
        public const int UnitsMillimeters = 21;

        // =========================================================================
        // Distance mode (G90-G91)
        // =========================================================================
        public const int DistanceAbsolute = 90;
        public const int DistanceIncremental = 91;

        // =========================================================================
        // Tool length offset (G43.1, G49)
        // =========================================================================
        public const int ToolLengthOffsetCancel = 49;
        public const double ToolLengthOffsetDynamic = 43.1;

        // =========================================================================
        // M-codes that pause execution
        // These cause GRBL to wait (used for tool changes, program end, etc.)
        // =========================================================================
        public const int MCodeProgramStop = 0;
        public const int MCodeProgramOptionalStop = 1;
        public const int MCodeProgramEnd = 2;
        public const int MCodeProgramEndReset = 30;
        public const int MCodeToolChange = 6;

        /// <summary>
        /// M-codes that pause file execution (used to detect pause lines in G-code files).
        /// </summary>
        public static readonly int[] PauseMCodes = {
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
