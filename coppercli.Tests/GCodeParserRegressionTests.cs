using System.IO;
using System.Linq;
using coppercli.Core.GCode;
using coppercli.Core.GCode.GCodeCommands;
using Xunit;

namespace coppercli.Tests
{
    public class GCodeParserRegressionTests
    {
        private static GCodeFile ParseLines(params string[] lines)
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllLines(path, lines);
                return GCodeFile.Load(path);
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// Comment stripping must remove the ")" along with the "(" before it. An orphan ")"
        /// is found ahead of the next "(" on the following pass, so end &lt; start and the
        /// whole file is rejected as having mismatched parentheses.
        /// </summary>
        [Fact]
        public void TwoCommentsOnOneLine_DoesNotFailTheLoad()
        {
            var file = ParseLines(
                "G21",
                "G90",
                "T1 (0.2mm end mill) M6 (change tool)",
                "G0 X1 Y1");

            Assert.NotNull(file);
        }

        [Fact]
        public void SingleComment_LeavesNoStrayParenthesis()
        {
            var file = ParseLines("G21", "G90", "G0 X1 Y1 (rapid to start)");

            Assert.All(file.GetGCode(), l => Assert.DoesNotContain(")", l));
        }

        /// <summary>
        /// A full circle starts and ends at the same point. Treating that as a zero-length
        /// move deletes drilled holes and circular isolation contours from the job with no
        /// warning.
        /// </summary>
        [Fact]
        public void FullCircleArc_IsNotDeletedAsAZeroLengthMove()
        {
            var file = ParseLines(
                "G21", "G90", "G17",
                "G0 X5 Y0 Z0",
                "G1 F200",
                "G2 X5 Y0 I-5 J0");

            Assert.Contains(file.Toolpath, c => c is Arc);
        }

        [Fact]
        public void ZeroLengthStraightMove_IsStillDropped()
        {
            var file = ParseLines("G21", "G90", "G0 X5 Y5 Z0", "G1 X5 Y5 F100");

            Assert.DoesNotContain(file.Toolpath.OfType<Line>(), l => l.Start == l.End);
        }

        /// <summary>
        /// Machine intercepts an M6 line through GrblProtocol.M6Pattern and never sends it to
        /// GRBL, while the pause comes from ClassifyPauseLine. A line that one matches and the
        /// other does not is swallowed without a pause, and the job cuts on with the previous
        /// tool.
        /// </summary>
        [Theory]
        [InlineData("M6", true)]
        [InlineData("M06", true)]
        [InlineData("T1 M6", true)]      // an anchored pattern would miss this
        [InlineData("G0 M6 X10", true)]
        [InlineData("M60", false)]
        [InlineData("M16", false)]
        [InlineData("G1 X1", false)]
        public void M6Recognition_MatchesExpectedLines(string line, bool expected)
        {
            Assert.Equal(expected, GCodeParser.IsM6Line(line));
        }

        /// <summary>
        /// pcb2gcode emits "G64 P&lt;tolerance&gt;" in the header, before any motion command.
        /// Its P word must not fall through to the motion handler, which has no motion mode
        /// active yet and fails the whole load.
        /// </summary>
        [Fact]
        public void UnknownGCodeInHeader_DoesNotFailTheLoad()
        {
            var file = ParseLines(
                "G94",
                "G21",
                "G90",
                "G64 P0.01000",
                "G01 F600",
                "G00 X1 Y1 Z1");

            Assert.NotNull(file);
            Assert.Contains(file.Warnings, w => w.Contains("G64"));
        }

        [Fact]
        public void UnknownGCodeAlongsideAMove_KeepsTheMove()
        {
            var file = ParseLines("G21", "G90", "G0 X0 Y0 Z0", "G1 X10 Y10 G64 F100");

            Assert.Contains(file.Toolpath.OfType<Line>(), l => l.End.X == 10 && l.End.Y == 10);
        }

        /// <summary>
        /// G28 rapids to a stored position across whatever is clamped to the bed. The parser
        /// cannot model where that is, so it drops the block with a warning: the block must
        /// neither reach the machine nor become an ordinary move.
        /// </summary>
        [Theory]
        [InlineData("G28")]
        [InlineData("G28 Z0")]
        [InlineData("G30 X0 Y0")]
        public void HomingBlockInFile_IsRefusedNotExecuted(string line)
        {
            var file = ParseLines("G21", "G90", "G0 X5 Y5 Z5", line, "G1 X6 Y6 F100");

            Assert.DoesNotContain(file.GetGCode(), l => l.Contains("G28") || l.Contains("G30"));
            Assert.Contains(file.Warnings, w => w.Contains("Home"));
            // The axis words must not have become a move either.
            Assert.DoesNotContain(file.Toolpath.OfType<Line>(), l => l.End.Z == 0 && l.Start.Z == 5);
        }
    }
}
