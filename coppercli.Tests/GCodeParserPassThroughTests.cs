using System.IO;
using System.Linq;
using coppercli.Core.GCode;
using coppercli.Core.GCode.GCodeCommands;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// Non-motion G-codes (G53, G10, G43.1, G38.x, G28, G30) carry axis words that must
    /// never be reinterpreted as ordinary work-coordinate motion.
    ///
    /// The regression these pin: G53 means "this block is in machine coordinates". If the
    /// G53 is stripped but "Z-1" survives, the retract becomes a work-coordinate G0 Z-1 —
    /// on a PCB job, work Z0 is the copper surface, so that is a rapid 1mm into the board.
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

        /// <summary>The emitted program must not contain a work-coordinate move to the G53 depth.</summary>
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

            // Any G0/G1 the parser emits must come from our own setup line, not from the
            // axis words of the non-motion block.
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

        /// <summary>The block must survive verbatim — losing a safety retract is also unsafe.</summary>
        [Fact]
        public void G53Retract_IsPreservedVerbatim()
        {
            var file = ParseLines("G21", "G90", "G0 X0 Y0 Z5", "G53 G0 Z-1");

            Assert.Contains(file.GetGCode(), l => l.Contains("G53") && l.Contains("Z-1"));
        }

        /// <summary>
        /// After a block we could not model, the machine is somewhere we cannot compute.
        /// The file's own recovery move must survive intact - the parser used to still
        /// believe Z was where it had been before the G53, so "G0 Z5" looked like a move
        /// to where the tool already was and was deleted, leaving the next cut to run at
        /// the retract depth.
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

            // Three motions must survive: the setup move, the recovery, and the cut.
            // The recovery is the one that used to vanish.
            var motions = file.Toolpath.OfType<Line>().ToList();

            Assert.Equal(3, motions.Count);
            Assert.Contains(motions, m => !m.StartTrusted);
        }

        /// <summary>A genuine no-op move is still dropped when we do know the start.</summary>
        [Fact]
        public void ZeroLengthMoveWithKnownStart_IsStillDropped()
        {
            var file = ParseLines("G21", "G90", "G0 X5 Y5 Z0", "G0 X5 Y5 Z0", "G1 X6 Y6 F100");

            Assert.Equal(2, file.Toolpath.OfType<Line>().Count());
        }

    }
}
