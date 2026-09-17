using System;
using System.IO;
using System.Linq;
using coppercli.Core.GCode;
using coppercli.Core.GCode.GCodeCommands;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// I, J and K give the arc center as an offset from the start point. Which word is legal,
    /// and whether it sets the arc's first or second axis, depends on the active plane, and a
    /// wrong mapping cuts an arc around the wrong center at feed rate. All nine combinations
    /// of plane and center word are covered.
    /// </summary>
    public class ArcCenterWordTests
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

        // Within a plane, the word for the first axis sets U and the word for the second sets
        // V. Offsets are relative to the start point, which is the origin here.
        [Theory]
        [InlineData("G17", "I3", 3.0, 0.0)]   // XY plane
        [InlineData("G17", "J3", 0.0, 3.0)]
        [InlineData("G19", "J3", 3.0, 0.0)]   // YZ plane
        [InlineData("G19", "K3", 0.0, 3.0)]
        [InlineData("G18", "K3", 3.0, 0.0)]   // ZX plane
        [InlineData("G18", "I3", 0.0, 3.0)]
        public void ACenterWord_MapsToItsPlanesAxis(
            string planeWord, string centerWord, double expectedU, double expectedV)
        {
            var arc = FirstArc(
                "G21", "G90", planeWord,
                "G0 X0 Y0 Z0",
                "G1 F200",
                $"G2 X1 Y1 Z1 {centerWord}");

            Assert.Equal(expectedU, arc.U, 6);
            Assert.Equal(expectedV, arc.V, 6);
        }

        // The third word names an axis outside the plane, so it cannot describe a center in
        // that plane. Ignoring it would cut a different arc.
        [Theory]
        [InlineData("G17", "K3")]
        [InlineData("G19", "I3")]
        [InlineData("G18", "J3")]
        public void TheAxisWordOutsideThePlane_IsRefused(string planeWord, string centerWord)
        {
            var ex = Assert.ThrowsAny<Exception>(() => ParseLines(
                "G21", "G90", planeWord,
                "G0 X0 Y0 Z0",
                "G1 F200",
                $"G2 X1 Y1 Z1 {centerWord}"));

            Assert.Contains("invalid", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
