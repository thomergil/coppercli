using System.IO;
using System.Linq;
using coppercli.Core.GCode;
using coppercli.Core.GCode.GCodeCommands;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// Non-motion G-codes (G53, G10, G43.1, G38.x, G28, G30) carry axis words that must
    /// never be reinterpreted as ordinary work-coordinate motion. G53 means the block is in
    /// machine coordinates, so a stripped G53 with "Z-1" still on the line turns a retract
    /// into a work-coordinate G0 Z-1 - on a PCB job work Z0 is the copper surface, which
    /// makes that a rapid 1mm into the board.
    /// </summary>
    public class GCodeParserPassThroughTests
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

        [Theory]
        [InlineData("G53 G0 Z-1")]
        [InlineData("G53 G0 X0 Y0")]
        [InlineData("G10 L2 P1 X-50 Y-30")]
        [InlineData("G43.1 Z-12.5")]
        [InlineData("G38.2 Z-5 F50")]
        [InlineData("G92 X-77 Y-88")]
        public void NonMotionBlock_IsNeverReemittedAsWorkCoordinateMotion(string line)
        {
            var file = ParseLines("G21", "G90", "G0 X0 Y0 Z5", line);

            // Any G0/G1 the parser emits must come from the setup line, not from the axis
            // words of the non-motion block.
            var motionLines = file.GetGCode()
                .Where(l => l.StartsWith("G0 ") || l.StartsWith("G1 ") ||
                            l == "G0" || l == "G1")
                .ToList();

            Assert.All(motionLines, l =>
            {
                Assert.DoesNotContain("Z-1", l);
                Assert.DoesNotContain("Z-12.5", l);
                Assert.DoesNotContain("Z-5", l);
                Assert.DoesNotContain("X-50", l);
                Assert.DoesNotContain("X-77", l);
            });
        }

        /// <summary>Dropping the block rather than re-emitting it loses a safety retract.</summary>
        [Fact]
        public void G53Retract_IsPreservedVerbatim()
        {
            var file = ParseLines("G21", "G90", "G0 X0 Y0 Z5", "G53 G0 Z-1");

            Assert.Contains(file.GetGCode(), l => l.Contains("G53") && l.Contains("Z-1"));
        }

        /// <summary>
        /// After a block the parser cannot model, the tool position is unknown, so the file's
        /// own recovery move has to survive. Carrying the pre-G53 Z forward makes "G0 Z5" look
        /// like a move to where the tool already is, and deleting it leaves the next cut at
        /// the retract height.
        /// </summary>
        [Fact]
        public void RecoveryMoveAfterG53_IsNotDeletedAsZeroLength()
        {
            var file = ParseLines(
                "G21", "G90",
                "G0 X0 Y0 Z5",
                "G53 G0 Z-40",
                "G0 Z5",
                "G1 X10 Y10 F100");

            // The three motions are the setup move, the recovery, and the cut.
            var motions = file.Toolpath.OfType<Line>().ToList();

            Assert.Equal(3, motions.Count);
            Assert.Contains(motions, m => !m.StartValid);
        }

        /// <summary>
        /// The same after a recovery that gives X and Y first: the move that then gives Z still
        /// starts at the Z from before the block, which is not where the tool is.
        /// </summary>
        [Fact]
        public void SecondMoveAfterG53_IsNotDeletedAsZeroLength_WhileZIsUnknown()
        {
            var file = ParseLines(
                "G21", "G90",
                "G0 X0 Y0 Z5",
                "G53 G0 Z-40",
                "G0 X0 Y0",
                "G0 X0 Y0 Z5",
                "G1 Z-0.1 F100");

            Assert.Equal(4, file.Toolpath.OfType<Line>().Count());
        }

        /// <summary>
        /// A G0 or G1 that names no axis moves nothing. Made into a move, it is written back as
        /// a bare "G1", and once a G53 block has made the position unknown nothing drops it.
        /// </summary>
        [Fact]
        public void AMotionWordNamingNoAxis_MakesNoMove()
        {
            var file = ParseLines("G21", "G90", "G01 F600", "G00 S12000", "G0 X0 Y0 Z5", "G53 G0 Z-1", "G1 F300", "G0 Z5");

            Assert.Equal(2, file.Toolpath.OfType<Line>().Count());
            Assert.DoesNotContain(file.GetGCode(), l => l is "G0" or "G1");
        }

        [Fact]
        public void ZeroLengthMoveWithKnownStart_IsStillDropped()
        {
            var file = ParseLines("G21", "G90", "G0 X5 Y5 Z0", "G0 X5 Y5 Z0", "G1 X6 Y6 F100");

            Assert.Equal(2, file.Toolpath.OfType<Line>().Count());
        }

    }
}
