using coppercli.Core.GCode;
using coppercli.Core.Util;
using Xunit;

namespace coppercli.Tests
{
    // Covers the warnings a file carries: where the job lies against work zero, and that a
    // file derived from another keeps what the parser reported.
    public class GCodeFileWarningsTests
    {
        /// <summary>
        /// pcb2gcode's back side with no board outline is mirrored about X=0, so work zero
        /// is at the board's right edge. If the operator zeroes at the lower-left, the tool runs
        /// the board's width left of where they expect.
        /// </summary>
        private static readonly string[] ZeroedAtTheRightEdge =
        {
            "G21", "G90",
            "G64 P0.01",
            "G0 Z1",
            "G0 X-9.164 Y8.447",
            "G1 Z-0.07 F50",
            "G1 X-59.679 Y-0.139 F100",
            "G1 X1.339 Y83.739"
        };

        [Fact]
        public void AJobLeftOfWorkZero_WarnsFirst_AndIsConfirmed()
        {
            var file = GCodeFile.FromList(ZeroedAtTheRightEdge);

            // The parser's G64 note comes after it: the terminal shows only the first few.
            string expected = string.Format(Constants.WarningJobOriginFormat, file.Min.X, file.Min.Y, -file.Min.X, -file.Min.Y);
            Assert.Equal(2, file.Warnings.Count);
            Assert.Equal(expected, file.Warnings[0]);
            Assert.Contains(expected, file.WarningsToConfirm);
        }

        [Fact]
        public void AJobBelowWorkZero_Warns()
        {
            var file = GCodeFile.FromList(new[] { "G21", "G90", "G1 X10 Y-20 Z-0.1 F100", "G1 X40 Y10" });

            Assert.Single(file.WarningsToConfirm);
        }

        /// <summary>
        /// Isolation passes around a board zeroed at its lower-left corner reach a fraction of
        /// a millimeter past zero; that is where the operator zeroed it, so nothing is said.
        /// </summary>
        [Fact]
        public void AJobThatReachesJustPastZero_DoesNotWarn()
        {
            var file = GCodeFile.FromList(new[] { "G21", "G90", "G1 X-0.5 Y-0.139 Z-0.07 F100", "G1 X61 Y83.3" });

            Assert.Empty(file.Warnings);
        }

        /// <summary>
        /// Applying a height map builds a new file, which must keep the parser's warnings: most
        /// milled jobs are height-mapped.
        /// </summary>
        [Fact]
        public void AHeightMappedFile_KeepsItsWarningsToConfirm()
        {
            var file = GCodeFile.FromList(new[]
            {
                "G21", "G90", "G0 Z1", "G28",
                "G1 X-40 Y0 Z-0.1 F100", "G1 X0 Y20"
            });

            var grid = ProbeGrid.ForJob(file.Min.GetXY(), file.Max.GetXY(), 1.0, 10.0);
            while (grid.TryPeekNext(out var point))
            {
                grid.RecordMeasurement(point.X, point.Y, 0.0);
            }

            var mapped = file.ApplyProbeGrid(grid);

            Assert.Equal(2, file.WarningsToConfirm.Count);
            Assert.Equal(file.WarningsToConfirm, mapped.WarningsToConfirm);
        }

        /// <summary>
        /// The parser also notes things the operator need not confirm, such as a G-code it
        /// ignores; only DANGER and INCHES warnings are shown for the operator to confirm.
        /// </summary>
        [Fact]
        public void OnlyDangerAndInchesWarnings_AreToConfirm()
        {
            var file = GCodeFile.FromList(new[] { "G20", "G90", "G64 P0.01", "G1 X1 Y1 Z-0.1 F10" });

            Assert.Contains(file.Warnings, w => w.Contains("G64"));
            Assert.Single(file.WarningsToConfirm);
            Assert.StartsWith(Constants.WarningPrefixInches, file.WarningsToConfirm[0]);
        }

        public static TheoryData<string> Transforms => new() { "Split", "ArcsToLines", "RotateCW" };

        /// <summary>Every file built from another keeps what the parser said about the source.</summary>
        [Theory]
        [MemberData(nameof(Transforms))]
        public void ADerivedFile_KeepsTheParsersWarnings(string transform)
        {
            var file = GCodeFile.FromList(new[] { "G21", "G90", "G28", "G1 X1 Y1 Z-0.1 F100", "G1 X20 Y10" });

            var derived = transform switch
            {
                "Split" => file.Split(1.0),
                "ArcsToLines" => file.ArcsToLines(1.0),
                _ => file.RotateCW()
            };

            // Rotating can move the job past work zero, which adds a warning of its own.
            Assert.Contains(Assert.Single(file.WarningsToConfirm), derived.WarningsToConfirm);
        }
    }
}
