using System;
using System.IO;
using System.Linq;
using coppercli.Core.GCode;
using coppercli.Core.GCode.GCodeCommands;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// I, J and K give the arc center as an offset from the start point. Which of them is
    /// legal, and whether it lands on the arc's first or second axis, depends on the plane
    /// in force. Get that mapping wrong and the cutter sweeps an arc around the wrong
    /// center at feed rate.
    ///
    /// All nine combinations are pinned, because the mapping is a table and a table is
    /// what a reader checks by spot-reading one row and assuming the rest.
    /// </summary>
    public class ArcCentreWordTests
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

        private static Arc FirstArc(params string[] lines) =>
            ParseLines(lines).Toolpath.OfType<Arc>().First();

        // Each plane takes the two words naming its own axes: the first axis sets U, the
        // second sets V. Offsets are relative to the start point, which is the origin here.
        [Theory]
        [InlineData("G17", "I3", 3.0, 0.0)]   // XY: I is the first axis
        [InlineData("G17", "J3", 0.0, 3.0)]   // XY: J is the second
        [InlineData("G19", "J3", 3.0, 0.0)]   // YZ: J is the first
        [InlineData("G19", "K3", 0.0, 3.0)]   // YZ: K is the second
        [InlineData("G18", "K3", 3.0, 0.0)]   // ZX: K is the first
        [InlineData("G18", "I3", 0.0, 3.0)]   // ZX: I is the second
        public void ACentreWord_LandsOnTheAxisItsPlaneGivesIt(
            string planeWord, string centreWord, double expectedU, double expectedV)
        {
            var arc = FirstArc(
                "G21", "G90", planeWord,
                "G0 X0 Y0 Z0",
                "G1 F200",
                $"G2 X1 Y1 Z1 {centreWord}");

            Assert.Equal(expectedU, arc.U, 6);
            Assert.Equal(expectedV, arc.V, 6);
        }

        // The third word names an axis the plane does not contain, so it cannot describe a
        // center in that plane. Silently ignoring it would cut an arc nobody asked for.
        [Theory]
        [InlineData("G17", "K3")]
        [InlineData("G19", "I3")]
        [InlineData("G18", "J3")]
        public void TheWordNamingTheThirdAxis_IsRefused(string planeWord, string centreWord)
        {
            var ex = Assert.ThrowsAny<Exception>(() => ParseLines(
                "G21", "G90", planeWord,
                "G0 X0 Y0 Z0",
                "G1 F200",
                $"G2 X1 Y1 Z1 {centreWord}"));

            Assert.Contains("invalid", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
