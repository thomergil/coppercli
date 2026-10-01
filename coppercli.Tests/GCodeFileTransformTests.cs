using System;
using System.IO;
using System.Linq;
using coppercli.Core.GCode;
using coppercli.Core.GCode.GCodeCommands;
using coppercli.Core.Util;
using Xunit;

namespace coppercli.Tests
{
    // Covers the two transforms that build the G-code a run streams from a loaded file:
    // OffsetCutDepth (the depth adjustment) and ApplyProbeGrid (the height map). Each returns
    // a new file and leaves the one it was called on alone.
    public class GCodeFileTransformTests
    {
        private const double DeeperBy = -0.1;
        private const double Tolerance = 1e-9;

        private const string Rapid = "G0";
        private const string CutPlungeCode = "G1 Z-0.1";
        private const string DeeperCutPlungeCode = "G1 Z-0.2";
        private const string DrillPlungeCode = "G1 Z-1.7";
        private const string DeeperDrillPlungeCode = "G1 Z-1.8";
        private const string DrillRetractCode = "G1 Z1";
        private const string MilldrillApproachCode = "G1 Z0.34";
        private const string HelixCode = "G2 X8 Y8 Z-0.34";
        private const string DeeperHelixCode = "G2 X8 Y8 Z-0.44";

        /// <summary>
        /// The shapes pcb2gcode writes: a cut with a plunge and XY passes, a drill that plunges
        /// and retracts with a feed move, and a milldrill that approaches above zero and
        /// spirals down in a full-circle arc.
        /// </summary>
        private static readonly string[] Pcb2gcodeShaped =
        {
            "G21", "G90",
            "G0 X0 Y0 Z1",
            "G0 X5 Y5",
            "G1 Z-0.1 F100",
            "G1 X15 Y5",
            "G1 X15 Y15",
            "G0 Z1",
            "G0 X2 Y2",
            "G1 Z-1.7",
            "G1 Z1",
            "G0 X8 Y8",
            "G1 Z0.34",
            "G2 X8 Y8 Z-0.34 I-0.1 J0",
            "G0 Z1",
            "M2"
        };

        private static readonly string[] WithAnXZArc =
        {
            "G21", "G90",
            "G0 X0 Y0 Z1",
            "G18",
            "G2 X5 Y0 Z-1 I2.5 K0 F100"
        };

        private static GCodeFile Parsed(params string[] lines) => GCodeFile.FromList(lines);

        private static Line[] Lines(GCodeFile file) => file.Toolpath.OfType<Line>().ToArray();

        private static string Text(GCodeFile file) => string.Join("\n", file.GetGCode());

        private static string[] RapidLines(GCodeFile file) =>
            file.GetGCode().Where(l => l.StartsWith(Rapid + " ", StringComparison.Ordinal)).ToArray();

        /// <summary>
        /// A shift that also lowered a rapid would drive the tool into the board between cuts,
        /// and one that moved a drill's feed retract would stop it below the retract height the
        /// file wrote. Every point below zero of a G1/G2/G3 moves by the offset, in the
        /// commands and in the text that is sent.
        /// </summary>
        [Fact]
        public void OffsetCutDepth_MovesEveryFeedPointBelowZero_AndNothingElse()
        {
            var source = Parsed(Pcb2gcodeShaped);

            var derived = source.OffsetCutDepth(DeeperBy);

            Assert.Equal(source.Toolpath.Count, derived.Toolpath.Count);
            int movedPoints = 0;
            for (int i = 0; i < source.Toolpath.Count; i++)
            {
                if (source.Toolpath[i] is not Motion before)
                {
                    continue;
                }

                var after = Assert.IsAssignableFrom<Motion>(derived.Toolpath[i]);
                bool rapid = before is Line { Rapid: true };
                Assert.Equal(before.Start.X, after.Start.X, Tolerance);
                Assert.Equal(before.End.Y, after.End.Y, Tolerance);

                double expectedStart = !rapid && before.Start.Z < 0 ? before.Start.Z + DeeperBy : before.Start.Z;
                double expectedEnd = !rapid && before.End.Z < 0 ? before.End.Z + DeeperBy : before.End.Z;
                Assert.Equal(expectedStart, after.Start.Z, Tolerance);
                Assert.Equal(expectedEnd, after.End.Z, Tolerance);
                if (expectedEnd != before.End.Z)
                {
                    movedPoints++;
                }
            }

            Assert.True(movedPoints >= 3, "the file has a cut, a drill and a helix; too few points moved");

            var text = derived.GetGCode();
            Assert.Contains(DeeperCutPlungeCode, text);
            Assert.Contains(DeeperDrillPlungeCode, text);
            Assert.Contains(text, l => l.StartsWith(DeeperHelixCode, StringComparison.Ordinal));
            Assert.DoesNotContain(CutPlungeCode, text);
            Assert.DoesNotContain(DrillPlungeCode, text);

            // A feed move up to zero or above is not a cut.
            Assert.Contains(DrillRetractCode, text);
            Assert.Contains(MilldrillApproachCode, text);

            Assert.Equal(RapidLines(source), RapidLines(derived));
        }

        /// <summary>
        /// A zero offset is no change, and giving the same file back means nothing is rebuilt
        /// or re-parsed for the common case of an unadjusted depth.
        /// </summary>
        [Fact]
        public void OffsetCutDepth_ByZero_ReturnsTheSameFile()
        {
            var source = Parsed(Pcb2gcodeShaped);

            Assert.Same(source, source.OffsetCutDepth(0));
        }

        /// <summary>
        /// An arc in the XZ or YZ plane carries Z in its center, which an offset cannot move
        /// with it, so the offset is refused with the shared message. A zero offset changes
        /// nothing and so has nothing to refuse.
        /// </summary>
        [Fact]
        public void OffsetCutDepth_RefusesArcsOutsideTheXYPlane_ButNotForZero()
        {
            var file = Parsed(WithAnXZArc);
            Assert.True(file.HasArcsOutsideXYPlane, "the G18 arc was not parsed as outside the XY plane");
            Assert.False(Parsed(Pcb2gcodeShaped).HasArcsOutsideXYPlane);

            var thrown = Assert.Throws<InvalidOperationException>(() => file.OffsetCutDepth(DeeperBy));
            Assert.Equal(Constants.ErrorArcsOutsideXYPlane, thrown.Message);
            Assert.Same(file, file.OffsetCutDepth(0));
        }

        /// <summary>
        /// The same refusal applies to a height map, which would shift the arc's endpoints and
        /// not its center.
        /// </summary>
        [Fact]
        public void ApplyProbeGrid_RefusesArcsOutsideTheXYPlane()
        {
            var file = Parsed(WithAnXZArc);

            var thrown = Assert.Throws<InvalidOperationException>(() => file.ApplyProbeGrid(MapOf(0)));
            Assert.Equal(Constants.ErrorArcsOutsideXYPlane, thrown.Message);
        }

        /// <summary>
        /// The copy must keep the name and path, which match a height map to its board, and the
        /// parse warnings the operator confirms before milling; losing either would make a map
        /// look like another board's or hide a danger warning. The source must come out
        /// unchanged, because every later change is rebuilt from it.
        /// </summary>
        [Fact]
        public void OffsetCutDepth_KeepsNamePathAndWarnings_AndLeavesTheSourceAlone()
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllLines(path, Pcb2gcodeShaped.Concat(new[] { "G28" }));
                var source = GCodeFile.Load(path);
                Assert.NotEmpty(source.Warnings);
                string before = Text(source);
                var cutsBefore = Lines(source).Select(l => l.End.Z).ToArray();

                var derived = source.OffsetCutDepth(DeeperBy);

                Assert.NotSame(source, derived);
                Assert.Equal(source.FileName, derived.FileName);
                Assert.Equal(Path.GetFullPath(path), derived.FilePath);
                Assert.Equal(source.Warnings, derived.Warnings);
                Assert.Equal(source.WarningsToConfirm, derived.WarningsToConfirm);
                Assert.NotEqual(before, Text(derived));

                Assert.Equal(before, Text(source));
                Assert.Equal(cutsBefore, Lines(source).Select(l => l.End.Z).ToArray());
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static ProbeGrid MapOf(double baseHeight, double perColumn = 0, double perRow = 0)
        {
            var map = new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20));
            for (int x = 0; x < map.SizeX; x++)
            {
                for (int y = 0; y < map.SizeY; y++)
                {
                    map.RecordMeasurement(x, y, baseHeight + perColumn * x + perRow * y);
                }
            }

            return map;
        }

        /// <summary>
        /// The map's local height is added to a cut's Z: a cut at a node that measured 0.15
        /// above the reference goes that far up with the copper. Checked at a node so the
        /// expected value is the measurement itself.
        /// </summary>
        [Fact]
        public void ApplyProbeGrid_AddsTheLocalHeightToFeedMoves()
        {
            var map = MapOf(0, perColumn: 0.1, perRow: 0.05);
            var source = Parsed("G21", "G90", "G0 X0 Y0 Z1", "G1 X10 Y10 Z-0.1 F100");

            var fitted = source.ApplyProbeGrid(map);

            var cut = Lines(fitted).Last(l => !l.Rapid);
            Assert.Equal(10, cut.End.X, Tolerance);
            Assert.Equal(-0.1 + 0.15, cut.End.Z, Tolerance);
        }

        /// <summary>
        /// Travel must clear the highest copper on the board, not just the copper under it, so
        /// every rapid with a Z rises by the map's highest point. Without the lift, a rapid over
        /// a high spot clears the copper by less than the height the file asked for.
        /// </summary>
        [Fact]
        public void ApplyProbeGrid_RaisesEveryRapidByTheMapsHighestPoint()
        {
            var map = MapOf(0, perColumn: 0.1, perRow: 0.05);
            Assert.Equal(0.3, map.MaxHeight, Tolerance);
            var source = Parsed("G21", "G90", "G0 X0 Y0 Z1", "G0 Z2", "G1 Z-0.1 F100", "G0 Z1");

            var fitted = source.ApplyProbeGrid(map);

            var rapidZs = Lines(fitted).Where(l => l.Rapid).Select(l => l.End.Z).ToArray();
            Assert.Equal(new[] { 1.3, 2.3, 1.3 }, rapidZs.Select(z => Math.Round(z, 6)));
        }

        /// <summary>
        /// A map whose highest point is below the reference would lower travel toward the
        /// board if the rapid followed it. Rapids keep the height the file wrote instead, and
        /// only ever rise.
        /// </summary>
        [Fact]
        public void ApplyProbeGrid_NeverLowersARapid()
        {
            var map = MapOf(-0.2);
            Assert.True(map.MaxHeight < 0);
            var source = Parsed("G21", "G90", "G0 X0 Y0 Z1", "G0 Z2", "G0 Z1");

            var fitted = source.ApplyProbeGrid(map);

            Assert.Equal(new[] { 1.0, 2.0, 1.0 }, Lines(fitted).Where(l => l.Rapid).Select(l => l.End.Z));
        }

        /// <summary>
        /// After a drill the tool sits at its retract height plus the local map height (1.15
        /// here), and the next travel is raised by the map's maximum (to 1.3). Rising on the
        /// diagonal while moving in X and Y could clip copper on the way up, so the rapid is
        /// split into a Z-only climb and then the XY traverse at height.
        /// </summary>
        [Fact]
        public void ApplyProbeGrid_ClimbsBeforeATraverseThatHasToRise()
        {
            var map = MapOf(0, perColumn: 0.1, perRow: 0.05);
            var source = Parsed(
                "G21", "G90",
                "G0 X10 Y10 Z1",
                "G1 Z-1.7 F100",
                "G1 Z1",
                "G0 X0 Y0 Z1");

            var fitted = source.ApplyProbeGrid(map);

            var text = fitted.GetGCode();
            int retract = text.FindIndex(l => l == "G1 Z1.15");
            Assert.True(retract >= 0, "the drill retract was not fitted to the map:\n" + Text(fitted));
            Assert.Equal(new[] { "G0 Z1.3", "G0 X0 Y0" }, text.Skip(retract + 1).ToArray());

            Line? previous = null;
            foreach (var line in Lines(fitted))
            {
                if (line.Rapid && previous != null)
                {
                    bool movesInXY = line.End.X != previous.End.X || line.End.Y != previous.End.Y;
                    bool rises = line.End.Z > previous.End.Z;
                    Assert.False(movesInXY && rises, "a rapid rose while moving in X or Y: " + Text(fitted));
                }

                previous = line;
            }
        }

        /// <summary>
        /// A rapid that starts at the height it is raised to needs no climb, and splitting it
        /// would only add a move; one that descends is no climb either, and must not sink
        /// the tool in place before it traverses. The last rapid here descends on the diagonal.
        /// </summary>
        [Fact]
        public void ApplyProbeGrid_DoesNotSplitARapidAlreadyAtItsLiftedHeight()
        {
            var map = MapOf(0, perColumn: 0.1, perRow: 0.05);
            var source = Parsed("G21", "G90", "G0 X0 Y0 Z1", "G0 X10 Y0 Z1", "G0 X20 Y0 Z1", "G0 X20 Y10 Z0.5");

            var fitted = source.ApplyProbeGrid(map);

            Assert.Equal(source.Toolpath.Count, fitted.Toolpath.Count);
            Assert.Equal(new[] { "G0 X0 Y0 Z1.3", "G0 X10", "G0 X20", "G0 Y10 Z0.8" }, RapidLines(fitted));
        }

        /// <summary>
        /// Fitting to a map builds a new file and must leave the loaded one as it was, or the
        /// next change would apply a map on top of a map. The copy keeps the path the map is
        /// matched against.
        /// </summary>
        [Fact]
        public void ApplyProbeGrid_LeavesTheSourceAlone_AndKeepsThePath()
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllLines(path, Pcb2gcodeShaped);
                var source = GCodeFile.Load(path);
                string before = Text(source);

                var fitted = source.ApplyProbeGrid(MapOf(0, perColumn: 0.1, perRow: 0.05));

                Assert.NotEqual(before, Text(fitted));
                Assert.Equal(before, Text(source));
                Assert.Equal(source.FilePath, fitted.FilePath);
                Assert.Equal(source.FileName, fitted.FileName);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static int CountOf(GCodeFile file) => file.Toolpath.Count;

        /// <summary>
        /// A line the parser passes through (G53 here) may move the tool anywhere, so where the
        /// tool is after it is unknown. The rapid after it must not get a climb worked out from
        /// the cut before it; the same file without the line does get one, which shows the
        /// line is what withholds the climb.
        /// </summary>
        [Fact]
        public void ApplyProbeGrid_InsertsNoClimb_AfterAPassThrough()
        {
            var map = MapOf(0, perColumn: 0.1, perRow: 0.05);
            var withoutPassThrough = Parsed("G21", "G90", "G0 X0 Y0 Z1", "G1 Z-0.1 F100", "G0 X10 Y10 Z1");
            var withPassThrough = Parsed("G21", "G90", "G0 X0 Y0 Z1", "G1 Z-0.1 F100", "G53 G0 Z0", "G0 X10 Y10 Z1");

            var control = withoutPassThrough.ApplyProbeGrid(map);
            var fitted = withPassThrough.ApplyProbeGrid(map);

            Assert.Equal(CountOf(withoutPassThrough) + 1, CountOf(control));
            Assert.Equal(CountOf(withPassThrough), CountOf(fitted));
            Assert.Equal(new[] { "G0 X0 Y0 Z1.3", "G0 X10 Y10 Z1.3" }, RapidLines(fitted));
        }

        /// <summary>
        /// A refused G28 leaves the parser without the tool's height, so the XY rapid after it
        /// has no Z to raise, cannot be judged to rise, and is not split.
        /// </summary>
        [Fact]
        public void ApplyProbeGrid_InsertsNoClimb_AfterARapidWhoseZIsUnknown()
        {
            var map = MapOf(0, perColumn: 0.1, perRow: 0.05);
            var source = Parsed("G21", "G90", "G0 X0 Y0 Z1", "G1 Z-0.1 F100", "G28", "G0 X5 Y5", "G0 X10 Y10 Z1");

            var fitted = source.ApplyProbeGrid(map);

            Assert.Equal(CountOf(source), CountOf(fitted));
            Assert.Equal(new[] { "G0 X0 Y0 Z1.3", "G0 X5 Y5", "G0 X10 Y10 Z1.3" }, RapidLines(fitted));
        }

        /// <summary>
        /// An XY rapid that ends lower than it starts is not a climb, and is not split: it goes
        /// as one move, raised by the map's highest point like any other rapid.
        /// </summary>
        [Fact]
        public void ApplyProbeGrid_DoesNotSplitADescendingXYRapid()
        {
            var map = MapOf(0, perColumn: 0.1, perRow: 0.05);
            var source = Parsed("G21", "G90", "G0 X0 Y0 Z2", "G0 X10 Y10 Z1");

            var fitted = source.ApplyProbeGrid(map);

            Assert.Equal(CountOf(source), CountOf(fitted));
            Assert.Equal(new[] { "G0 X0 Y0 Z2.3", "G0 X10 Y10 Z1.3" }, RapidLines(fitted));
        }
    }
}
