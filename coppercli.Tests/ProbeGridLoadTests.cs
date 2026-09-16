using System;
using System.IO;
using coppercli.Core.GCode;
using coppercli.Core.Util;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// A saved map decides the commanded Z of every cutting move, so the loader owes the
    /// same checks the constructor makes. These pin the refusals, one per way a file lies.
    /// </summary>
    public class ProbeGridLoadTests
    {
        private static string WriteMap(string body)
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pgrid");
            File.WriteAllText(path, body);
            return path;
        }

        private static string Heightmap(string attributes, string points = "") =>
            $"<heightmap {attributes}>{points}</heightmap>";

        private static void Refuses(string body)
        {
            string path = WriteMap(body);
            try
            {
                Assert.Throws<InvalidDataException>(() => ProbeGrid.Load(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Theory]
        [InlineData("NaN")]
        [InlineData("Infinity")]
        [InlineData("-Infinity")]
        public void AHeightThatIsNotANumber_IsRefused(string height)
        {
            Refuses(Heightmap(
                "MinX='0' MinY='0' MaxX='10' MaxY='10' SizeX='2' SizeY='2'",
                $"<point X='0' Y='0'>{height}</point>"));
        }

        /// <summary>One node per axis has no spacing, so every point reads the same height.</summary>
        [Theory]
        [InlineData("1", "1")]
        [InlineData("2", "1")]
        [InlineData("0", "0")]
        public void AGridTooSmallToInterpolate_IsRefused(string sizeX, string sizeY)
        {
            Refuses(Heightmap($"MinX='0' MinY='0' MaxX='10' MaxY='10' SizeX='{sizeX}' SizeY='{sizeY}'"));
        }

        /// <summary>A grid this large is a request to allocate, not a board.</summary>
        [Fact]
        public void AGridLargerThanAnyBoard_IsRefused()
        {
            Refuses(Heightmap("MinX='0' MinY='0' MaxX='10' MaxY='10' SizeX='2' SizeY='1000000000'"));
        }

        [Theory]
        [InlineData("MinX='NaN' MinY='0' MaxX='10' MaxY='10' SizeX='2' SizeY='2'")]
        [InlineData("MinX='0' MinY='0' MaxX='Infinity' MaxY='10' SizeX='2' SizeY='2'")]
        [InlineData("MinX='10' MinY='0' MaxX='0' MaxY='10' SizeX='2' SizeY='2'")]
        [InlineData("MinX='0' MinY='0' MaxX='0' MaxY='10' SizeX='2' SizeY='2'")]
        public void ExtentsThatDescribeNoBoard_AreRefused(string attributes)
        {
            Refuses(Heightmap(attributes));
        }

        [Fact]
        public void APointOutsideTheGrid_IsRefused()
        {
            Refuses(Heightmap(
                "MinX='0' MinY='0' MaxX='10' MaxY='10' SizeX='2' SizeY='2'",
                "<point X='5' Y='0'>-0.1</point>"));
        }

        [Fact]
        public void APointBeforeTheHeightmap_IsRefused()
        {
            Refuses("<root><point X='0' Y='0'>-0.1</point></root>");
        }

        /// <summary>
        /// A non-finite origin compares false against every tolerance, so a file carrying one
        /// would declare itself applicable to any job. A map that names a file has to carry an
        /// origin that can be read, or it is refused - reading as "no setup recorded" would
        /// get it through every gate instead.
        /// </summary>
        [Theory]
        [InlineData("NaN")]
        [InlineData("Infinity")]
        public void AnOriginThatIsNotANumber_IsRefused(string origin)
        {
            Refuses(Heightmap(
                "MinX='0' MinY='0' MaxX='10' MaxY='10' SizeX='2' SizeY='2' " +
                $"SourceFile='/board.ngc' OriginX='{origin}' OriginY='0' OriginZ='0'",
                "<point X='0' Y='0'>-0.1</point>"));
        }

        /// <summary>
        /// A map written before the setup was recorded carries no origin at all. Those still
        /// load; the refusal above is for a file that records one it cannot express.
        /// </summary>
        [Fact]
        public void AMapWithNoRecordedSetup_StillLoads()
        {
            string path = WriteMap(Heightmap(
                "MinX='0' MinY='0' MaxX='10' MaxY='10' SizeX='2' SizeY='2'",
                "<point X='0' Y='0'>-0.1</point>"));
            try
            {
                Assert.False(ProbeGrid.Load(path).Context.IsKnown);
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>A map that round-trips still loads; the refusals above are not a blanket.</summary>
        [Fact]
        public void AMapThisProgramWrote_LoadsBack()
        {
            var grid = ProbeGrid.ForJob(new Vector2(0, 0), new Vector2(20, 20), 1.0, 10.0);
            grid.RecordMeasurement(0, 0, -0.125);

            string path = WriteMap("placeholder");
            try
            {
                grid.Save(path);
                var loaded = ProbeGrid.Load(path);

                Assert.Equal(grid.SizeX, loaded.SizeX);
                Assert.Equal(grid.SizeY, loaded.SizeY);
                Assert.Equal(-0.125, loaded.Points[0, 0]!.Value, 3);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
