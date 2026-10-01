#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using coppercli;
using coppercli.Core.GCode;
using coppercli.Core.GCode.GCodeCommands;
using coppercli.Core.Util;
using coppercli.Helpers;
using coppercli.WebServer;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// The boards and the geometry checks the sections tests share, so each fact about what
    /// "kept" means is written once.
    /// </summary>
    internal static class SectionTestSupport
    {
        public const double Tolerance = 1e-9;
        public const double SampleTolerance = 1e-6;
        public const double OutputDecimals = 0.0005;
        public const int SamplesPerCut = 40;

        /// <summary>Whether <paramref name="command"/> is the clip's rise to the machine's safe height, not a block of the file.</summary>
        public static bool IsTheClipsRetract(Command command) =>
            command is PassThrough { Line: var line } && line == PartClip.MachineSafeHeightLine;

        /// <summary>Where the file's first probe, machine-coordinate or offset block is in <paramref name="toolpath"/>.</summary>
        public static int IndexOfFilesBlock(List<Command> toolpath) =>
            toolpath.FindIndex(c => c is PassThrough && !IsTheClipsRetract(c));

        /// <summary>
        /// A pcb2gcode-shaped board: one trace outline 2..18 in both axes at Z-0.1, and a drill at
        /// (5,5) and another at (15,15). Two by two sections cut at X = 10 and Y = 10.
        /// </summary>
        public static readonly string[] Board =
        {
            "G21", "G90",
            "G0 Z10",
            "G0 X0 Y0",
            "G0 Z1",
            "M3",
            "S10000",
            "G0 X2 Y2",
            "G1 Z-0.1 F200",
            "G1 X18 Y2 F600",
            "G1 X18 Y18",
            "G1 X2 Y18",
            "G0 Z1",
            "G0 X5 Y5",
            "G1 Z-1.7 F100",
            "G1 Z1",
            "G0 X15 Y15",
            "G1 Z-1.7",
            "G1 Z1",
            "G0 Z10",
            "M5",
            "M2"
        };

        /// <summary>Two separate traces, one inside the lower-left section and one inside the upper-right one.</summary>
        public static readonly string[] TwoIslands =
        {
            "G21", "G90",
            "G0 Z10", "G0 X0 Y0", "G0 Z1",
            "G0 X2 Y2", "G1 Z-0.1 F200", "G1 X8 Y2 F600", "G1 X8 Y8", "G0 Z1",
            "G0 X12 Y12", "G1 Z-0.1 F200", "G1 X18 Y12 F600", "G1 X18 Y18", "G0 Z1",
            "G0 Z10", "M5", "M2"
        };

        /// <summary>A trace outline twice as wide (16) as high (8).</summary>
        public static readonly string[] WideBoard =
        {
            "G21", "G90", "G0 Z10", "G0 X0 Y0", "G0 Z1",
            "G0 X2 Y2", "G1 Z-0.1 F200", "G1 X18 Y2 F600", "G1 X18 Y10", "G1 X2 Y10",
            "G0 Z1", "G0 Z10", "M5", "M2"
        };

        /// <summary>A trace outline twice as high (16) as wide (8).</summary>
        public static readonly string[] TallBoard =
        {
            "G21", "G90", "G0 Z10", "G0 X0 Y0", "G0 Z1",
            "G0 X2 Y2", "G1 Z-0.1 F200", "G1 X10 Y2 F600", "G1 X10 Y18", "G1 X2 Y18",
            "G0 Z1", "G0 Z10", "M5", "M2"
        };

        public static GCodeFile Parse(params string[] lines) => GCodeFile.FromList(lines);

        public static BoardSections Choose(GCodeFile file, int columns, int rows, params (int Column, int Row)[] chosen)
        {
            var (sections, refused) = BoardSections.Choose(
                file, columns, rows, chosen.Select(c => new BoardCell(c.Column, c.Row)));
            Assert.Null(refused);
            return Assert.IsType<BoardSections>(sections);
        }

        public static IEnumerable<Motion> Cuts(GCodeFile file) =>
            file.Toolpath.OfType<Motion>().Where(m => m.FullyKnown && m.IsCut);

        public static bool Exact(Vector3 a, Vector3 b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z;

        /// <summary>The highest Z at which the file moves across the board without cutting.</summary>
        public static double TravelHeight(GCodeFile file) =>
            file.Toolpath.OfType<Motion>()
                .Where(m => m.FullyKnown && !m.IsCut && m.MovesAcrossTheBoard && m.Start.Z == m.End.Z)
                .Max(m => m.End.Z);

        /// <summary>Whether the point is inside a chosen section, grown by <paramref name="margin"/> (shrunk when negative).</summary>
        public static bool InChosen(BoardSections sections, Vector3 p, double margin)
        {
            var d = sections.Division;
            double w = (d.Max.X - d.Min.X) / d.Columns;
            double h = (d.Max.Y - d.Min.Y) / d.Rows;
            return sections.Chosen.Any(s =>
                p.X >= d.Min.X + s.Column * w - margin && p.X <= d.Min.X + (s.Column + 1) * w + margin
                && p.Y >= d.Min.Y + s.Row * h - margin && p.Y <= d.Min.Y + (s.Row + 1) * h + margin);
        }

        /// <summary>How far the point is from the path, for a line or an arc in the XY plane.</summary>
        public static double DistanceTo(Motion motion, Vector3 p)
        {
            if (motion is Arc arc)
            {
                double angle = Math.Atan2(p.Y - arc.V, p.X - arc.U);
                double span = arc.AngleSpan;
                double turned = span > 0 ? angle - arc.StartAngle : arc.StartAngle - angle;
                turned -= 2 * Math.PI * Math.Floor(turned / (2 * Math.PI));
                double ratio = Math.Clamp(turned / Math.Abs(span), 0, 1);
                return (p - arc.Interpolate(ratio)).Magnitude;
            }

            Vector3 delta = motion.End - motion.Start;
            double lengthSquared = delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z;
            double along = lengthSquared == 0
                ? 0
                : Math.Clamp(((p - motion.Start).X * delta.X + (p - motion.Start).Y * delta.Y + (p - motion.Start).Z * delta.Z) / lengthSquared, 0, 1);
            return (p - (motion.Start + delta * along)).Magnitude;
        }

        /// <summary>every point of every output cut lies in a chosen section, within the tolerance.</summary>
        public static void AssertEveryCutIsInAChosenSection(GCodeFile output, BoardSections sections, string context)
        {
            foreach (var cut in Cuts(output))
            {
                foreach (double ratio in Enumerable.Range(0, SamplesPerCut + 1).Select(i => (double)i / SamplesPerCut))
                {
                    Vector3 p = cut.Interpolate(ratio);
                    Assert.True(InChosen(sections, p, Constants.SectionEdgeToleranceMm + Tolerance),
                        $"{context}: a cut at ratio {ratio} is at ({p.X}, {p.Y}, {p.Z}), outside every chosen section");
                }
            }
        }

        /// <summary>each point of the source's cuts well inside a chosen section is on an output cut.</summary>
        public static void AssertNothingChosenIsLost(GCodeFile source, GCodeFile output, BoardSections sections, string context)
        {
            var kept = Cuts(output).ToList();
            foreach (var cut in Cuts(source))
            {
                for (int i = 0; i <= SamplesPerCut; i++)
                {
                    Vector3 p = cut.Interpolate((double)i / SamplesPerCut);
                    if (!InChosen(sections, p, -(Constants.SectionEdgeToleranceMm + SampleTolerance)))
                    {
                        continue;
                    }

                    Assert.True(kept.Any(k => DistanceTo(k, p) < SampleTolerance),
                        $"{context}: the source cuts ({p.X}, {p.Y}, {p.Z}) inside a chosen section and no output cut passes there");
                }
            }
        }

        /// <summary>The tool moves sideways below the travel height only along a cut, or on the file's own travel.</summary>
        public static void AssertNoSidewaysTravelBelowTravelHeight(GCodeFile source, GCodeFile output, string context)
        {
            double travel = TravelHeight(source);
            var filesOwn = source.Toolpath.ToHashSet(ReferenceEqualityComparer.Instance);
            foreach (var move in output.Toolpath.OfType<Motion>())
            {
                // The file's own travel, written as it wrote it, is at the file's own height.
                if (!move.FullyKnown || move.IsCut || !move.MovesAcrossTheBoard || filesOwn.Contains(move))
                {
                    continue;
                }

                Assert.True(Math.Min(move.Start.Z, move.End.Z) >= travel - Tolerance,
                    $"{context}: a move that is not a cut goes from ({move.Start.X}, {move.Start.Y}, {move.Start.Z}) to ({move.End.X}, {move.End.Y}, {move.End.Z}), below the travel height {travel}");
            }
        }

        public static GCodeFile Keep(GCodeFile source, BoardSections? sections, ChosenPhases? phases = null)
        {
            var (output, refused) = source.KeepPart(phases, sections);
            Assert.Null(refused);
            return Assert.IsType<GCodeFile>(output);
        }
    }

    // KeepPart and the types under it: the division, the choice, the clip and the
    // geometry they rely on, then the machine's G-code AppState builds from them.
    public class BoardSectionsCoreTests
    {
        private const double Tolerance = SectionTestSupport.Tolerance;
        private const int RandomBoards = 200;
        private const int RandomSeedBase = 4200;
        private const int MaxRandomColumnsAndRows = 4;
        private const int RandomPathsMin = 2;
        private const int RandomPathsMax = 5;
        private const int RandomSegmentsMin = 1;
        private const int RandomSegmentsMax = 4;
        private const int RandomDrillsMax = 3;

        /// <summary>The heights a random board retracts to after a path, so it crosses the board at more than one.</summary>
        private static readonly double[] RandomRetractHeights = { 1, 3 };
        private const int RandomCoordinateMax = 400;
        private const double RandomCoordinateStep = 0.1;
        private const double ArcCenterX = 7;
        private const double ArcCenterY = 5;
        private const double ArcRadius = 2;
        private const double LineBetweenSections = 10;
        private const double ExpectedPlungeFeed = 200;
        private const double SlicedLength = 1.3;
        private const double WarningCheckLength = 0.5;

        private static readonly string[] UnknownCommandFile =
        {
            "G21", "G90", "G999", "G0 Z10", "G0 X0 Y0", "G0 Z1",
            "G0 X2 Y2", "G1 Z-0.1 F200", "G1 X18 Y2 F600", "G1 X18 Y18", "G0 Z1", "G0 Z10"
        };

        private static readonly string[] ArcBoard =
        {
            "G21", "G90",
            "G0 X5 Y5 Z1",
            "G1 Z-0.1 F100",
            "G2 X5 Y5 I2 J0",
            "G0 Z1",
            "G0 X1 Y2",
            "G1 Z-0.1 F100",
            "G1 X13 Y2",
            "G0 Z1",
            "G0 X1 Y8",
            "G1 Z-0.1 F100",
            "G1 X13 Y8",
            "G0 Z1"
        };

        private static readonly string[] NoTravelHeightBoard =
        {
            "G21", "G90",
            "G0 X0 Y0 Z0",
            "G1 Z-0.1 F100",
            "G1 X20",
            "G1 X20 Y20",
            "G1 X0 Y20",
            "G1 X0 Y0"
        };

        private static readonly string[] XZArcBoard =
        {
            "G21", "G90", "G0 X0 Y0 Z1", "G18", "G2 X5 Y0 Z-1 I2.5 K0 F100"
        };

        private static readonly string[] NoAreaBoard =
        {
            "G21", "G90", "G0 X0 Y0 Z1", "G1 Z-0.1 F100", "G1 X10 Y0", "G0 Z1"
        };

        // ---- 1. CuttingBounds ----

        /// <summary>
        /// Catches a division laid over the travel (the park at X0 Y0, Z10) instead of the cut:
        /// the sections would be mostly empty copper-free space.
        /// </summary>
        [Fact]
        public void CuttingBounds_AreTheFeedMoveBounds_WhenTheyHaveAnArea()
        {
            var file = SectionTestSupport.Parse(SectionTestSupport.Board);

            var (min, max) = file.CuttingBounds;

            Assert.Equal(2, min.X, Tolerance);
            Assert.Equal(2, min.Y, Tolerance);
            Assert.Equal(18, max.X, Tolerance);
            Assert.Equal(18, max.Y, Tolerance);
            Assert.NotEqual(file.Min.X, min.X);
        }

        /// <summary>
        /// Catches a file whose cuts are a straight line getting an empty area: it must fall
        /// back to the whole toolpath bounds, which do have width and height.
        /// </summary>
        [Fact]
        public void CuttingBounds_FallBackToTheWholeToolpath_WhenTheFeedMovesHaveNoArea()
        {
            var file = SectionTestSupport.Parse(NoAreaBoard.Concat(new[] { "G0 X4 Y9" }).ToArray());

            var (min, max) = file.CuttingBounds;

            Assert.Equal(file.Min.X, min.X, Tolerance);
            Assert.Equal(file.Min.Y, min.Y, Tolerance);
            Assert.Equal(file.Max.X, max.X, Tolerance);
            Assert.Equal(file.Max.Y, max.Y, Tolerance);
            Assert.Equal(9, max.Y, Tolerance);
        }

        // ---- 2. BoardDivision ----

        /// <summary>
        /// Catches dividing a file the sections cannot follow (an XZ arc) instead of refusing it.
        /// </summary>
        [Fact]
        public void Division_RefusesArcsOutsideTheXYPlane()
        {
            var (division, refused) = BoardDivision.Of(SectionTestSupport.Parse(XZArcBoard), 2, 2);

            Assert.Null(division);
            Assert.Equal(Constants.ErrorArcsOutsideXYPlane, refused);
        }

        /// <summary>
        /// Catches a file that cuts along one line getting a division with zero-height sections.
        /// </summary>
        [Fact]
        public void Division_RefusesAFileWhoseCutsHaveNoHeight()
        {
            var (division, refused) = BoardDivision.Of(SectionTestSupport.Parse(NoAreaBoard), 2, 2);

            Assert.Null(division);
            Assert.Equal(Constants.ErrorNoAreaToDivide, refused);
        }

        /// <summary>
        /// Catches the column or row counted from the wrong side, or a point on a section line
        /// put in the section left of / below it (a cut on the line would then be kept or
        /// dropped with the wrong side).
        /// </summary>
        [Fact]
        public void CellAt_CountsFromTheLeftAndBottom_AndAPointOnALineBelongsToTheCellRightOrAboveIt()
        {
            var (division, _) = BoardDivision.Of(SectionTestSupport.Parse(SectionTestSupport.Board), 2, 2);
            Assert.NotNull(division);

            Assert.Equal(new BoardCell(0, 0), division!.CellAt(3, 3));
            Assert.Equal(new BoardCell(1, 0), division.CellAt(15, 3));
            Assert.Equal(new BoardCell(0, 1), division.CellAt(3, 15));
            Assert.Equal(new BoardCell(1, 1), division.CellAt(15, 15));
            Assert.Equal(new BoardCell(1, 0), division.CellAt(LineBetweenSections, 3));
            Assert.Equal(new BoardCell(0, 1), division.CellAt(3, LineBetweenSections));
            Assert.Equal(new BoardCell(1, 1), division.CellAt(LineBetweenSections, LineBetweenSections));
        }

        /// <summary>
        /// Catches a point outside the area throwing or wrapping instead of landing in the
        /// nearest section.
        /// </summary>
        [Fact]
        public void CellAt_PutsAPointOutsideTheAreaInTheNearestCell()
        {
            var (division, _) = BoardDivision.Of(SectionTestSupport.Parse(SectionTestSupport.Board), 3, 2);

            Assert.Equal(new BoardCell(0, 0), division!.CellAt(-50, -50));
            Assert.Equal(new BoardCell(2, 1), division.CellAt(500, 500));
            Assert.Equal(new BoardCell(2, 0), division.CellAt(500, -50));
        }

        // ---- 3. BoardSections.Choose ----

        /// <summary>
        /// Catches a count of 0 or one past the limit being accepted (a 9-column picture).
        /// </summary>
        [Theory]
        [InlineData(0, 2)]
        [InlineData(2, 0)]
        [InlineData(Constants.MaxSectionsPerAxis + 1, 2)]
        [InlineData(2, Constants.MaxSectionsPerAxis + 1)]
        [InlineData(-1, 2)]
        public void Choose_RefusesACountOutsideTheLimits(int columns, int rows)
        {
            var file = SectionTestSupport.Parse(SectionTestSupport.Board);

            var (sections, refused) = BoardSections.Choose(file, columns, rows, new[] { new BoardCell(0, 0) });

            Assert.Null(sections);
            Assert.Equal(string.Format(Constants.ErrorSectionCountFormat, Constants.MaxSectionsPerAxis), refused);
        }

        /// <summary>
        /// Catches the limit itself being refused: MaxSectionsPerAxis columns and rows is a
        /// division the picker offers, so the choice must be taken.
        /// </summary>
        [Fact]
        public void Choose_TakesTheMostColumnsAndRows()
        {
            var file = SectionTestSupport.Parse(SectionTestSupport.Board);

            var (sections, refused) = BoardSections.Choose(
                file, Constants.MaxSectionsPerAxis, Constants.MaxSectionsPerAxis, new[] { new BoardCell(0, 0) });

            Assert.Null(refused);
            Assert.Equal(Constants.MaxSectionsPerAxis, sections!.Division.Columns);
            Assert.Equal(Constants.MaxSectionsPerAxis, sections.Division.Rows);
        }

        /// <summary>
        /// Catches a chosen section outside the division being silently ignored.
        /// </summary>
        [Fact]
        public void Choose_RefusesASectionOutsideTheDivision()
        {
            var file = SectionTestSupport.Parse(SectionTestSupport.Board);

            var (sections, refused) = BoardSections.Choose(file, 2, 2, new[] { new BoardCell(2, 0) });
            var (negative, refusedNegative) = BoardSections.Choose(file, 2, 2, new[] { new BoardCell(0, -1) });
            var (asManyAsTheBoard, refusedAsMany) = BoardSections.Choose(
                file, 2, 1, new[] { new BoardCell(0, 0), new BoardCell(5, 0) });

            Assert.Null(sections);
            Assert.Equal(Constants.ErrorSectionOutsideBoard, refused);
            Assert.Null(negative);
            Assert.Equal(Constants.ErrorSectionOutsideBoard, refusedNegative);
            Assert.Null(asManyAsTheBoard);
            Assert.Equal(Constants.ErrorSectionOutsideBoard, refusedAsMany);
        }

        /// <summary>
        /// Catches "none" or "all" being kept as a choice: both mean the whole board, which is
        /// null with no refusal, so the machine's G-code is the file's own.
        /// </summary>
        [Fact]
        public void Choose_ReturnsTheWholeBoard_ForNoneOrAllSections()
        {
            var file = SectionTestSupport.Parse(SectionTestSupport.Board);
            var all = new[] { new BoardCell(0, 0), new BoardCell(1, 0), new BoardCell(0, 1), new BoardCell(1, 1) };

            Assert.Equal((null, null), BoardSections.Choose(file, 2, 2, Array.Empty<BoardCell>()));
            Assert.Equal((null, null), BoardSections.Choose(file, 2, 2, all));
            Assert.Equal((null, null), BoardSections.Choose(file, 2, 2, all.Concat(all)));
        }

        /// <summary>
        /// Catches a choice that loses its division or its sections.
        /// </summary>
        [Fact]
        public void Choose_KeepsTheDivisionAndTheChosenSections()
        {
            var file = SectionTestSupport.Parse(SectionTestSupport.Board);

            var sections = SectionTestSupport.Choose(file, 3, 2, (0, 0), (2, 1), (2, 1));

            Assert.Equal(3, sections.Division.Columns);
            Assert.Equal(2, sections.Division.Rows);
            Assert.Equal(new[] { new BoardCell(0, 0), new BoardCell(2, 1) }.OrderBy(s => s.Column),
                sections.Chosen.OrderBy(s => s.Column));
        }

        // ---- 4. KeepPart ----

        /// <summary>
        /// Catches a cut left in an unchosen section, or a chosen one lost, for each
        /// way of choosing on the fixture.
        /// </summary>
        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 0)]
        [InlineData(0, 1)]
        [InlineData(1, 1)]
        public void KeepPart_KeepsEveryCutInTheChosenSectionAndNoOther(int column, int row)
        {
            var source = SectionTestSupport.Parse(SectionTestSupport.Board);
            var sections = SectionTestSupport.Choose(source, 2, 2, (column, row));

            var output = SectionTestSupport.Keep(source, sections);

            SectionTestSupport.AssertEveryCutIsInAChosenSection(output, sections, $"section {column},{row}");
            SectionTestSupport.AssertNothingChosenIsLost(source, output, sections, $"section {column},{row}");
            SectionTestSupport.AssertNoSidewaysTravelBelowTravelHeight(source, output, $"section {column},{row}");
            Assert.NotEmpty(SectionTestSupport.Cuts(output));
        }

        /// <summary>
        /// Catches a straight cut clipped short of, or past, the section line: the
        /// trace along Y2 must end at X10 exactly, not at the last point that happens to be inside.
        /// </summary>
        [Fact]
        public void KeepPart_CutsAStraightCutExactlyAtTheSectionLine()
        {
            var source = SectionTestSupport.Parse(SectionTestSupport.Board);
            var sections = SectionTestSupport.Choose(source, 2, 2, (0, 0));

            var output = SectionTestSupport.Keep(source, sections);

            var alongY2 = SectionTestSupport.Cuts(output).OfType<Line>()
                .Where(l => l.Start.Y == 2 && l.End.Y == 2 && l.Start.X != l.End.X).ToList();
            Assert.Single(alongY2);
            Assert.Equal(2, alongY2[0].Start.X, Tolerance);
            Assert.Equal(LineBetweenSections, alongY2[0].End.X, Tolerance);
        }

        /// <summary>
        /// Catches a stretch that starts mid-cut being entered by a rapid or at the wrong
        /// speed: the plunge from the travel height to the cut's Z must be a feed move at the
        /// file's plunge feed, at the section line.
        /// </summary>
        [Fact]
        public void KeepPart_PlungesIntoAStretchThatStartsMidCut_WithAFeedMoveAtThePlungeFeed()
        {
            var source = SectionTestSupport.Parse(SectionTestSupport.Board);
            var sections = SectionTestSupport.Choose(source, 2, 2, (1, 0));

            var output = SectionTestSupport.Keep(source, sections);

            var motions = output.Toolpath.OfType<Motion>().ToList();
            int firstCut = motions.FindIndex(m => m.FullyKnown && m.IsCut && m.Start.X != m.End.X);
            Assert.True(firstCut > 0, "the output has no sideways cut");
            var along = motions[firstCut];
            Assert.Equal(LineBetweenSections, along.Start.X, Tolerance);

            var plunge = Assert.IsType<Line>(motions[firstCut - 1]);
            Assert.False(plunge.Rapid, "the plunge into the stretch is a rapid");
            Assert.Equal(ExpectedPlungeFeed, plunge.Feed, Tolerance);
            Assert.Equal(TravelHeightOfBoard, plunge.Start.Z, Tolerance);
            Assert.Equal(along.Start.Z, plunge.End.Z, Tolerance);
            Assert.Equal(LineBetweenSections, plunge.End.X, Tolerance);
            Assert.Equal(2, plunge.End.Y, Tolerance);
            Assert.Equal(LineBetweenSections, plunge.Start.X, Tolerance);
        }

        private const double TravelHeightOfBoard = 1;

        private const double ProbePlateX = 30;
        private const double ProbePlateY = 15;

        /// <summary>
        /// A trace across both halves of the board, then a tool-length probe on a plate right of
        /// the board and a Z reset there, then a trace in the left half.
        /// </summary>
        private static readonly string[] ProbeAfterTrace =
        {
            "G21", "G90", "G0 Z10", "G0 X0 Y0", "G0 Z1",
            "G0 X2 Y2", "G1 Z-0.1 F200", "G1 X18 Y2 F600", "G1 X18 Y18", "G0 Z1",
            "G0 X30 Y15", "G38.2 Z-10 F50", "G92 Z0", "G0 Z1",
            "G0 X4 Y12", "G1 Z-0.1 F200", "G1 X8 Y12 F600", "G0 Z1",
            "G0 Z10", "M2"
        };

        /// <summary>The same probe, written in the middle of the trace, where the right half is left out.</summary>
        private static readonly string[] ProbeInsideATrace =
        {
            "G21", "G90", "G0 Z10", "G0 X0 Y0", "G0 Z1",
            "G0 X2 Y2", "G1 Z-0.1 F200", "G1 X18 Y2 F600", "G38.2 Z-10 F50", "G1 X18 Y18", "G0 Z1",
            "G0 Z10", "M2"
        };

        /// <summary>
        /// Catches a probe or an offset change made above the chosen section instead of where
        /// the file put the tool: the file's move to the plate crossed a left-out section and was
        /// dropped, so the tool has to be taken to the plate before the block.
        /// </summary>
        [Fact]
        public void KeepPart_TakesTheToolWhereTheFileHasIt_BeforeAProbeOrOffsetBlock()
        {
            var source = SectionTestSupport.Parse(ProbeAfterTrace);
            var sections = SectionTestSupport.Choose(source, 2, 1, (0, 0));

            var toolpath = SectionTestSupport.Keep(source, sections).Toolpath.ToList();

            int probe = SectionTestSupport.IndexOfFilesBlock(toolpath);
            Assert.True(probe > 0, "the probe block was dropped");
            var before = Assert.IsAssignableFrom<Motion>(toolpath[probe - 1]);
            Assert.Equal(ProbePlateX, before.End.X, Tolerance);
            Assert.Equal(ProbePlateY, before.End.Y, Tolerance);
            Assert.Equal(TravelHeightOfBoard, before.End.Z, Tolerance);
            Assert.IsType<PassThrough>(toolpath[probe + 1]);
        }

        /// <summary>
        /// Catches a probe block written where the tool cannot be taken without cutting a
        /// left-out section: the choice is refused instead.
        /// </summary>
        [Fact]
        public void KeepPart_RefusesAProbeInsideACutThatIsLeftOut()
        {
            var source = SectionTestSupport.Parse(ProbeInsideATrace);
            var sections = SectionTestSupport.Choose(source, 2, 1, (0, 0));

            var (output, refused) = source.KeepPart(null, sections);

            Assert.Null(output);
            Assert.Equal(Constants.ErrorSectionsBlockInLeftOutCut, refused);
        }

        /// <summary>
        /// Two traces linked at a low retract of Z0.5, and one level crossing of the board at
        /// the clearance of Z5, as CAM output with a retract and a clearance plane writes it.
        /// </summary>
        private static readonly string[] LowLinksHighClearance =
        {
            "G21", "G90", "G0 Z10", "G0 X0 Y0", "G0 Z5",
            "G0 X2 Y2", "G0 Z0.5", "G1 Z-0.1 F200", "G1 X18 Y2 F600", "G0 Z0.5",
            "G0 X2 Y18", "G1 Z-0.1 F200", "G1 X18 Y18 F600", "G0 Z5",
            "M2"
        };

        private const double LowRetract = 0.5;
        private const double Clearance = 5;

        /// <summary>
        /// Catches travel between kept stretches made at the file's last, low link height: a
        /// long move across the board has to cross at the clearance, then come down to the
        /// height the file plunges from.
        /// </summary>
        [Fact]
        public void KeepPart_CrossesAtTheClearance_AndFeedsFromWhereTheFilesLastRapidDownEnded()
        {
            var source = SectionTestSupport.Parse(LowLinksHighClearance);
            var sections = SectionTestSupport.Choose(source, 2, 1, (1, 0));

            var motions = SectionTestSupport.Keep(source, sections).Toolpath.OfType<Motion>().ToList();

            int secondEntry = motions.FindIndex(m => m.FullyKnown && m.IsCut && m.Start.Y == 18 && m.Start.X != m.End.X);
            Assert.True(secondEntry > 0, "the second trace's kept stretch is missing");
            var plunge = Assert.IsType<Line>(motions[secondEntry - 1]);
            var down = Assert.IsType<Line>(motions[secondEntry - 2]);
            var across = Assert.IsType<Line>(motions[secondEntry - 3]);

            Assert.False(plunge.Rapid);
            Assert.Equal(LowRetract, plunge.Start.Z, Tolerance);
            Assert.True(down.Rapid);
            Assert.Equal(LowRetract, down.End.Z, Tolerance);
            Assert.True(across.Rapid);
            Assert.Equal(Clearance, across.Start.Z, Tolerance);
            Assert.Equal(Clearance, across.End.Z, Tolerance);
            Assert.Equal(LineBetweenSections, across.End.X, Tolerance);
        }

        private const double PlateHeight = 10;

        /// <summary>The same plate, reached by one rapid that climbs to Z10 on the way.</summary>
        private static readonly string[] RapidUpToAHighPlate =
        {
            "G21", "G90", "G0 Z10", "G0 X0 Y0", "G0 Z1",
            "G0 X2 Y2", "G1 Z-0.1 F200", "G1 X18 Y2 F600", "G0 Z1",
            "G0 X30 Y15 Z10", "G38.2 Z-10 F50", "G0 Z10", "M2"
        };

        /// <summary>
        /// Catches the tool crossing at the clearance and rising to the target only once under
        /// it: the file reached the plate on a climbing rapid, so no crossing height of the file
        /// is as high as the plate.
        /// </summary>
        [Fact]
        public void KeepPart_CrossesAtTheTargetsHeight_WhenItIsAboveTheClearance()
        {
            var source = SectionTestSupport.Parse(RapidUpToAHighPlate);
            var sections = SectionTestSupport.Choose(source, 2, 1, (0, 0));

            var toolpath = SectionTestSupport.Keep(source, sections).Toolpath.ToList();

            int probe = SectionTestSupport.IndexOfFilesBlock(toolpath);
            var across = Assert.IsAssignableFrom<Motion>(toolpath[probe - 1]);
            Assert.Equal(ProbePlateX, across.End.X, Tolerance);
            Assert.Equal(PlateHeight, across.Start.Z, Tolerance);
            Assert.Equal(PlateHeight, across.End.Z, Tolerance);
        }

        /// <summary>
        /// Two traces, with the file feeding across the board between them at Z8 while its
        /// rapids stay at Z1.
        /// </summary>
        private static readonly string[] FeedsAcrossHigherThanItRapids =
        {
            "G21", "G90", "G0 Z10", "G0 X0 Y0", "G0 Z1",
            "G0 X2 Y2", "G1 Z-0.1 F200", "G1 X18 Y2 F600",
            "G1 Z8 F500", "G1 X2 Y18", "G0 Z1",
            "G1 Z-0.1 F200", "G1 X18 Y18 F600", "G0 Z1",
            "M2"
        };

        private const double FeedAcrossHeight = 8;

        /// <summary>
        /// Catches a crossing height taken from the file's rapids alone: the file moved across
        /// the board at Z8 by feed, so the output crosses no lower.
        /// </summary>
        [Fact]
        public void KeepPart_CountsTheFilesFeedAcrossTheBoard_InTheClearance()
        {
            var source = SectionTestSupport.Parse(FeedsAcrossHigherThanItRapids);
            var sections = SectionTestSupport.Choose(source, 2, 1, (1, 0));

            var motions = SectionTestSupport.Keep(source, sections).Toolpath.OfType<Motion>().ToList();

            int entry = motions.FindIndex(m => m.FullyKnown && m.IsCut && m.Start.Y == 18 && m.Start.X != m.End.X);
            Assert.True(entry > 0, "the second trace's kept stretch is missing");
            var across = motions.Take(entry).Last(m => m.MovesAcrossTheBoard);
            Assert.Equal(FeedAcrossHeight, across.End.Z, Tolerance);
        }

        /// <summary>
        /// The file rapids down to Z5 once, and later rapids down to Z0, level with the copper,
        /// before each plunge.
        /// </summary>
        private static readonly string[] RapidsDownToTheSurface =
        {
            "G21", "G90", "G0 Z10", "G0 X0 Y0", "G0 Z5",
            "G0 X2 Y2", "G0 Z0", "G1 Z-0.1 F100", "G1 X18 Y2 F600", "G0 Z5",
            "G0 X2 Y18", "G0 Z0", "G1 Z-0.1 F100", "G1 X18 Y18 F600", "G0 Z5",
            "M2"
        };

        private const double LastRapidDownAboveTheSurface = 5;

        /// <summary>
        /// Catches the output taking a rapid down to the surface as its approach height: only a
        /// rapid that stops above the copper counts.
        /// </summary>
        [Fact]
        public void KeepPart_IgnoresARapidDownToTheSurface_ForItsApproachHeight()
        {
            var source = SectionTestSupport.Parse(RapidsDownToTheSurface);
            var sections = SectionTestSupport.Choose(source, 2, 1, (1, 0));

            var motions = SectionTestSupport.Keep(source, sections).Toolpath.OfType<Motion>().ToList();

            int entry = motions.FindIndex(m => m.FullyKnown && m.IsCut && m.Start.Y == 18 && m.Start.X != m.End.X);
            var plunge = Assert.IsType<Line>(motions[entry - 1]);
            Assert.False(plunge.Rapid);
            Assert.Equal(LastRapidDownAboveTheSurface, plunge.Start.Z, Tolerance);
        }

        /// <summary>
        /// A two-pass outline as pcb2gcode writes it: the file rapids to Z35 and down to Z1
        /// before it has said where X and Y are, and never crosses the board level.
        /// </summary>
        private static readonly string[] OutlineFromAnUnknownStart =
        {
            "G21", "G90", "G0 Z35", "G0 Z1", "G0 X2 Y2",
            "G1 Z-0.3 F200", "G1 X18 Y2 F600", "G1 X18 Y18", "G1 X2 Y18", "G1 X2 Y2",
            "G1 Z-0.6 F200", "G1 X18 Y2 F600", "G1 X18 Y18", "G1 X2 Y18", "G1 X2 Y2",
            "G0 Z35", "M2"
        };

        private const double OutlineApproach = 1;

        /// <summary>
        /// Catches a rapid down the file makes before it has said where X and Y are being
        /// ignored for the approach height: the tool would feed all the way down from the
        /// clearance, Z35, into each kept stretch.
        /// </summary>
        [Fact]
        public void KeepPart_TakesItsApproachHeight_FromARapidDownWithUnknownXY()
        {
            var source = SectionTestSupport.Parse(OutlineFromAnUnknownStart);
            var sections = SectionTestSupport.Choose(source, 2, 1, (1, 0));

            var motions = SectionTestSupport.Keep(source, sections).Toolpath.OfType<Motion>().ToList();

            int entry = motions.FindIndex(m => m.FullyKnown && m.IsCut && m.MovesAcrossTheBoard && m.Start.X == LineBetweenSections);
            Assert.True(entry > 1, "the first kept stretch is missing");
            var plunge = Assert.IsType<Line>(motions[entry - 1]);
            var down = Assert.IsType<Line>(motions[entry - 2]);
            Assert.False(plunge.Rapid);
            Assert.Equal(OutlineApproach, plunge.Start.Z, Tolerance);
            Assert.True(down.Rapid);
            Assert.Equal(OutlineApproach, down.End.Z, Tolerance);
        }

        /// <summary>
        /// The file rapids down to Z1 and feeds from there through Z0 into the copper, as some
        /// CAM output does.
        /// </summary>
        private static readonly string[] FeedFromZ1ThroughZ0 =
        {
            "G21", "G90", "G0 Z10", "G0 X0 Y0", "G0 Z5",
            "G0 X2 Y2", "G0 Z1", "G1 Z0 F100", "G1 Z-0.1", "G1 X18 Y2 F600", "G0 Z5",
            "G0 X2 Y18", "G0 Z1", "G1 Z0 F100", "G1 Z-0.1", "G1 X18 Y18 F600", "G0 Z5",
            "M2"
        };

        private const double RapidDownTo = 1;

        /// <summary>
        /// Catches the output rapiding down onto the copper: the height it comes down to at
        /// rapid speed is where the file's own rapids down end, never the surface.
        /// </summary>
        [Fact]
        public void KeepPart_ComesDownAtRapidSpeedOnlyAsFarAsTheFileDoes()
        {
            var source = SectionTestSupport.Parse(FeedFromZ1ThroughZ0);
            var sections = SectionTestSupport.Choose(source, 2, 1, (1, 0));

            var motions = SectionTestSupport.Keep(source, sections).Toolpath.OfType<Motion>().ToList();

            Assert.All(motions.OfType<Line>().Where(l => l.Rapid && l.End.Z < l.Start.Z),
                rapid => Assert.True(rapid.End.Z >= RapidDownTo,
                    $"a rapid comes down to Z{rapid.End.Z}, below the Z{RapidDownTo} the file's rapids stop at"));
            int entry = motions.FindIndex(m => m.FullyKnown && m.IsCut && m.Start.Y == 18 && m.Start.X != m.End.X);
            var plunge = Assert.IsType<Line>(motions[entry - 1]);
            Assert.False(plunge.Rapid);
            Assert.Equal(RapidDownTo, plunge.Start.Z, Tolerance);
        }

        /// <summary>
        /// Crossings at Z20 before a G92, then a new frame whose only travel height is Z1, and
        /// a trace in each half after it.
        /// </summary>
        private static readonly string[] HeightsBeforeAnOffsetChange =
        {
            "G21", "G90", "G0 Z20", "G0 X0 Y0", "G0 X1 Y1",
            "G92 Z0", "G0 Z1", "G0 X2 Y2",
            "G1 Z-0.1 F200", "G1 X18 Y2 F600", "G0 Z1",
            "G0 X2 Y18", "G1 Z-0.1 F200", "G1 X18 Y18 F600", "G0 Z1",
            "M2"
        };

        private const double NewFrameClearance = 1;

        /// <summary>
        /// Catches heights noted before an offset change used after it: the block can move
        /// work zero, so the crossing after it is at the new frame's own height.
        /// </summary>
        [Fact]
        public void KeepPart_ForgetsTheHeightsOfTheFrameBeforeAnOffsetChange()
        {
            var source = SectionTestSupport.Parse(HeightsBeforeAnOffsetChange);
            var sections = SectionTestSupport.Choose(source, 2, 1, (1, 0));

            var toolpath = SectionTestSupport.Keep(source, sections).Toolpath.ToList();
            var motions = toolpath.OfType<Motion>().ToList();

            int block = SectionTestSupport.IndexOfFilesBlock(toolpath);
            Assert.True(block > 0, "the offset block was dropped");
            Assert.All(motions.Where(m => m.FullyKnown && !m.IsCut && m.MovesAcrossTheBoard && m.Start.X >= 2),
                crossing => Assert.Equal(NewFrameClearance, crossing.End.Z, Tolerance));
        }

        /// <summary>The last trace ends in the right half, then the file retracts and stops the spindle.</summary>
        private static readonly string[] SpindleStopsAfterALeftOutCut =
        {
            "G21", "G90", "G0 Z10", "G0 X0 Y0", "G0 Z2",
            "G0 X2 Y2", "G1 Z-0.1 F100", "G1 X18 Y2 F300", "G1 X18 Y18", "G0 Z2",
            "M5", "M2"
        };

        private const double RetractHeight = 2;
        private const int SpindleStopCode = 5;

        /// <summary>
        /// Catches the spindle stopping, or the program ending, with the tool still in the
        /// copper where the kept stretch ended: before a command that is not a move, the tool
        /// rises in place to the file's height.
        /// </summary>
        [Fact]
        public void KeepPart_RisesInPlaceBeforeACommand_WhenTheLastCutWasLeftOut()
        {
            var source = SectionTestSupport.Parse(SpindleStopsAfterALeftOutCut);
            var sections = SectionTestSupport.Choose(source, 2, 1, (0, 0));

            var toolpath = SectionTestSupport.Keep(source, sections).Toolpath.ToList();

            int stop = toolpath.FindIndex(c => c is MCode { Code: SpindleStopCode });
            var before = toolpath.Take(stop).OfType<Motion>().Last();
            Assert.Equal(RetractHeight, before.End.Z, Tolerance);
            Assert.Equal(LineBetweenSections, before.End.X, Tolerance);
            Assert.Equal(before.Start.X, before.End.X, Tolerance);
            Assert.Equal(before.Start.Y, before.End.Y, Tolerance);
        }

        /// <summary>The same board with no command after the retract.</summary>
        private static readonly string[] EndsAfterALeftOutCut =
        {
            "G21", "G90", "G0 Z10", "G0 X0 Y0", "G0 Z2",
            "G0 X2 Y2", "G1 Z-0.1 F100", "G1 X18 Y2 F300", "G1 X18 Y18", "G0 Z2"
        };

        /// <summary>Catches a program that ends with the tool in the copper where the kept stretch ended.</summary>
        [Fact]
        public void KeepPart_EndsAboveTheSurface_WhenTheFilesLastCutIsLeftOut()
        {
            var source = SectionTestSupport.Parse(EndsAfterALeftOutCut);
            var sections = SectionTestSupport.Choose(source, 2, 1, (0, 0));

            var last = SectionTestSupport.Keep(source, sections).Toolpath.OfType<Motion>().Last();

            Assert.Equal(RetractHeight, last.End.Z, Tolerance);
        }

        /// <summary>
        /// The file rapids down to Z0.5 before a G92, then in the new frame only rapids up to Z5
        /// and feeds down from there.
        /// </summary>
        private static readonly string[] ApproachBeforeAnOffsetChange =
        {
            "G21", "G90", "G0 Z10", "G0 X0 Y0", "G0 Z0.5",
            "G92 Z0",
            "G0 Z5", "G0 X2 Y2", "G1 Z-0.1 F200", "G1 X18 Y2 F600", "G0 Z5",
            "M2"
        };

        private const double NewFrameLowestRapid = 5;

        /// <summary>
        /// Catches an approach height noted before an offset change used after it: the new frame
        /// never rapids below Z5, so the output's rapids after the block come down no lower.
        /// </summary>
        [Fact]
        public void KeepPart_ForgetsTheApproachHeightOfTheFrameBeforeAnOffsetChange()
        {
            var source = SectionTestSupport.Parse(ApproachBeforeAnOffsetChange);
            var sections = SectionTestSupport.Choose(source, 2, 1, (1, 0));

            var toolpath = SectionTestSupport.Keep(source, sections).Toolpath.ToList();

            int block = SectionTestSupport.IndexOfFilesBlock(toolpath);
            Assert.True(block > 0, "the offset block was dropped");
            Assert.All(toolpath.Skip(block).OfType<Line>().Where(l => l.Rapid && l.End.Z < l.Start.Z),
                rapid => Assert.True(rapid.End.Z >= NewFrameLowestRapid,
                    $"a rapid after the offset change comes down to Z{rapid.End.Z}"));
        }

        /// <summary>
        /// Three traces: the file crosses the board at Z1 between the first two, and at Z5
        /// before the third.
        /// </summary>
        private static readonly string[] CrossesHigherLater =
        {
            "G21", "G90", "G0 Z10", "G0 X0 Y0", "G0 Z1",
            "G0 X2 Y2", "G1 Z-0.1 F200", "G1 X18 Y2 F600", "G0 Z1",
            "G0 X2 Y10", "G1 Z-0.1 F200", "G1 X18 Y10 F600", "G0 Z5",
            "G0 X2 Y18", "G1 Z-0.1 F200", "G1 X18 Y18 F600", "G0 Z5",
            "M2"
        };

        private const double LaterCrossingHeight = 5;

        /// <summary>
        /// Catches a crossing made at the height the file had crossed at so far, below a height
        /// it crosses at later with the same tool, for a clamp it steers around, say.
        /// </summary>
        [Fact]
        public void KeepPart_CrossesAtTheStagesHighestCrossing_EvenOneTheFileMakesLater()
        {
            var source = SectionTestSupport.Parse(CrossesHigherLater);
            var sections = SectionTestSupport.Choose(source, 2, 1, (1, 0));

            var motions = SectionTestSupport.Keep(source, sections).Toolpath.OfType<Motion>().ToList();

            int entry = motions.FindIndex(m => m.FullyKnown && m.IsCut && m.Start.Y == 10 && m.Start.X != m.End.X);
            Assert.True(entry > 0, "the second trace's kept stretch is missing");
            var across = motions.Take(entry).Last(m => m.MovesAcrossTheBoard);
            Assert.Equal(LaterCrossingHeight, across.End.Z, Tolerance);
        }

        /// <summary>
        /// Two traces crossed between at Z1, then a tool change after which the file crosses at
        /// Z35 to reach a drill hole.
        /// </summary>
        private static readonly string[] HighCrossingAfterAToolChange =
        {
            "G21", "G90", "G0 Z10", "G0 X0 Y0", "G0 Z1",
            "G0 X2 Y2", "G1 Z-0.1 F200", "G1 X18 Y2 F600", "G0 Z1",
            "G0 X2 Y18", "G1 Z-0.1 F200", "G1 X18 Y18 F600", "G0 Z35",
            "T2", "M6",
            "G0 X15 Y15", "G0 Z1", "G1 Z-1.7 F100", "G1 Z1", "G0 Z35",
            "M2"
        };

        /// <summary>
        /// Catches a crossing height from one tool's work used for another's: the isolation
        /// traces cross at Z1, and the Z35 crossing the drill makes after the tool change does
        /// not raise them.
        /// </summary>
        [Fact]
        public void KeepPart_TakesTheClearanceOfEachToolsStage_OnItsOwn()
        {
            var source = SectionTestSupport.Parse(HighCrossingAfterAToolChange);
            var sections = SectionTestSupport.Choose(source, 2, 1, (1, 0));

            var toolpath = SectionTestSupport.Keep(source, sections).Toolpath.ToList();

            int toolChange = toolpath.FindIndex(c => c is MCode { Code: GCodeNumbers.MCodeToolChange });
            var motions = toolpath.Take(toolChange).OfType<Motion>().ToList();
            int entry = motions.FindIndex(m => m.FullyKnown && m.IsCut && m.Start.Y == 18 && m.Start.X != m.End.X);
            Assert.True(entry > 0, "the second trace's kept stretch is missing");
            var across = motions.Take(entry).Last(m => m.MovesAcrossTheBoard);
            Assert.Equal(TravelHeightOfBoard, across.End.Z, Tolerance);
        }

        /// <summary>A file that sets work zero, then plunges before it has said where the tool is in Z.</summary>
        private static readonly string[] PlungesWithNoKnownStart =
        {
            "G21", "G90", "G0 Z10", "G0 X0 Y0", "G0 Z1",
            "G0 X30 Y2", "G1 Z-0.1 F100", "G1 X40 Y2", "G0 Z1",
            "G92 X0 Y0 Z0", "G1 X5 Y5 F100", "G1 Z-0.1", "G1 X40 Y5", "G0 Z1",
            "M2"
        };

        /// <summary>
        /// Catches a cut written although nothing says which sections it crosses: a plunge whose
        /// start the file never gave is refused rather than made wherever the tool is.
        /// </summary>
        [Fact]
        public void KeepPart_RefusesACutWhoseStartTheFileNeverGave()
        {
            var source = SectionTestSupport.Parse(PlungesWithNoKnownStart);
            var sections = SectionTestSupport.Choose(source, 2, 1, (1, 0));

            var (output, refused) = source.KeepPart(null, sections);

            Assert.Null(output);
            Assert.Equal(Constants.ErrorSectionsCutWithUnknownStart, refused);
        }

        /// <summary>A board whose only travel is level with the copper.</summary>
        private static readonly string[] TravelAtTheSurface =
        {
            "G21", "G90", "G0 X0 Y0 Z0",
            "G0 X2 Y2", "G1 Z-0.1 F200", "G1 X18 Y2 F600", "G1 X18 Y18", "G0 Z0",
            "M2"
        };

        /// <summary>
        /// Catches travel between sections made at the copper surface: a height of zero is no
        /// clearance, so the choice is refused.
        /// </summary>
        [Fact]
        public void KeepPart_RefusesAFileWhoseOnlyTravelIsAtTheSurface()
        {
            var source = SectionTestSupport.Parse(TravelAtTheSurface);
            var sections = SectionTestSupport.Choose(source, 2, 2, (1, 1));

            var (output, refused) = source.KeepPart(null, sections);

            Assert.Null(output);
            Assert.Equal(Constants.ErrorSectionsNoTravelHeight, refused);
        }

        private const double RiseAfterHome = 5;

        /// <summary>After a trace cut in two by the section line, a G28 the parser refuses, then moves that say where the tool is again.</summary>
        private static readonly string[] RefusedHomeAfterALeftOutCut =
        {
            "G21", "G90", "G0 Z10", "G0 X0 Y0", "G0 Z1",
            "G0 X2 Y2", "G1 Z-0.1 F200", "G1 X18 Y2 F600",
            "G28", "G0 Z5", "G0 X4 Y12",
            "G1 Z-0.1 F200", "G1 X8 Y12 F600", "G0 Z1", "M2"
        };

        /// <summary>
        /// Catches the tool moving across from inside the copper, where the kept stretch left it,
        /// after the file lost its position (a refused G28): the file's own moves after that are
        /// travel to a cut in the chosen section, so the tool rises straight up first.
        /// </summary>
        [Fact]
        public void KeepPart_RisesOutOfTheCopperBeforeMovingAcross_WhenTheFileLostItsPosition()
        {
            var source = SectionTestSupport.Parse(RefusedHomeAfterALeftOutCut);
            var sections = SectionTestSupport.Choose(source, 2, 1, (0, 0));

            var motions = SectionTestSupport.Keep(source, sections).Toolpath.OfType<Motion>().ToList();

            int keptEnd = motions.FindIndex(m => m.FullyKnown && m.IsCut && m.End.X == LineBetweenSections);
            Assert.True(keptEnd >= 0, "the kept stretch of the first trace is missing");
            var rise = Assert.IsType<Line>(motions[keptEnd + 1]);
            Assert.True(rise.Rapid);
            Assert.Equal(LineBetweenSections, rise.End.X, Tolerance);
            Assert.Equal(rise.Start.Y, rise.End.Y, Tolerance);
            Assert.True(rise.End.Z >= TravelHeightOfBoard, $"the tool moved on at Z{rise.End.Z}, inside the copper");
        }

        /// <summary>
        /// Shaped like the start of the owner's merged files: the spindle starts, then the file's
        /// first move goes across to its first path, in the right half, and comes down there,
        /// with no Z given before it.
        /// </summary>
        private static readonly string[] OpensOverALeftOutPath =
        {
            "G21", "G90", "S12000", "M3", "G4 P3",
            "G0 X18 Y2", "G0 Z1", "G1 Z-0.1 F200", "G1 X2 Y2 F600", "G0 Z1",
            "G0 X2 Y18", "G1 Z-0.1 F200", "G1 X18 Y18 F600", "G0 Z1",
            "M5", "M2"
        };

        private const double LeftOutPathX = 18;
        private const double FirstPathY = 2;

        /// <summary>
        /// Catches the tool coming down over a section that is left out. The file's opening
        /// moves lead to a path that starts in the right half, so they are dropped: the tool
        /// rises to the machine's safe height, moves across in X and Y, and only then comes down,
        /// over the chosen left half.
        /// </summary>
        [Fact]
        public void KeepPart_TravelsAcrossBeforeComingDown_WhenTheFilesFirstPathIsLeftOut()
        {
            var source = SectionTestSupport.Parse(OpensOverALeftOutPath);
            var sections = SectionTestSupport.Choose(source, 2, 1, (0, 0));

            var toolpath = SectionTestSupport.Keep(source, sections).Toolpath.ToList();

            int retract = toolpath.FindIndex(SectionTestSupport.IsTheClipsRetract);
            var lines = toolpath.OfType<Line>().ToList();
            var firstAcross = lines.First(l => l.PositionValid[0]);
            Assert.True(retract >= 0 && retract < toolpath.IndexOf(firstAcross),
                "the tool moved across before rising to the machine's safe height");
            Assert.False(firstAcross.PositionValid[2], "the first move across also changes the height");
            Assert.Equal(LineBetweenSections, firstAcross.End.X, Tolerance);
            Assert.Equal(FirstPathY, firstAcross.End.Y, Tolerance);
            Assert.DoesNotContain(lines, l => l.PositionValid[0] && l.End.X == LeftOutPathX && l.End.Y == FirstPathY);
            Assert.All(lines.Where(l => l.PositionValid[0] && l.PositionValid[2]),
                l => Assert.True(l.End.X <= LineBetweenSections + Tolerance,
                    $"the tool goes to ({l.End.X}, {l.End.Y}, {l.End.Z}), over the half that is left out"));
        }

        /// <summary>
        /// Catches a path wholly inside the chosen sections being rewritten: once the output
        /// knows where the tool is, the path's own commands, including the file's travel to it,
        /// must be in the output as written.
        /// </summary>
        [Fact]
        public void KeepPart_WritesAPathInsideTheChosenSectionsAsTheFileWroteIt()
        {
            var source = SectionTestSupport.Parse(SectionTestSupport.TwoIslands);
            var sections = SectionTestSupport.Choose(source, 2, 2, (0, 0), (1, 1));

            var output = SectionTestSupport.Keep(source, sections);

            var outputMotions = output.Toolpath.OfType<Motion>().ToList();
            // From the rise after the first island through the last cut of the second.
            var sourceMotions = source.Toolpath.OfType<Motion>().ToList();
            int first = sourceMotions.FindIndex(m => m.FullyKnown && m.Start.X == 8 && m.Start.Y == 8 && m.End.Z == 1);
            int last = sourceMotions.FindIndex(m => m.FullyKnown && m.End.X == 18 && m.End.Y == 18);
            Assert.True(first >= 0 && last > first, "the fixture lost its second island");

            for (int i = first; i <= last; i++)
            {
                var own = sourceMotions[i];
                Assert.True(
                    outputMotions.Any(o => ReferenceEquals(o, own)
                        || (SectionTestSupport.Exact(o.Start, own.Start) && SectionTestSupport.Exact(o.End, own.End)
                            && o.Feed == own.Feed && (o as Line)?.Rapid == (own as Line)?.Rapid)),
                    $"the file's own move {i} ({own.Start.X},{own.Start.Y},{own.Start.Z}) -> ({own.End.X},{own.End.Y},{own.End.Z}) is not in the output");
            }
        }

        /// <summary>
        /// Catches the tool still going to a cut that is left out: a drill hole in an
        /// unchosen section must not be the end of any output move.
        /// </summary>
        [Theory]
        [InlineData(0, 0, 15, 15)]
        [InlineData(1, 1, 5, 5)]
        public void KeepPart_NeverGoesToADrillHoleInAnUnchosenSection(int column, int row, double holeX, double holeY)
        {
            var source = SectionTestSupport.Parse(SectionTestSupport.Board);
            var sections = SectionTestSupport.Choose(source, 2, 2, (column, row));

            var output = SectionTestSupport.Keep(source, sections);

            Assert.DoesNotContain(output.Toolpath.OfType<Motion>(),
                m => m.End.X == holeX && m.End.Y == holeY);
            Assert.Contains(output.Toolpath.OfType<Motion>(),
                m => m.End.X == (holeX == 5 ? 15 : 5) && m.End.Y == (holeY == 5 ? 15 : 5) && m.End.Z < 0);
        }

        /// <summary>
        /// Catches a spindle, tool or M code dropped, added or reordered by the clip.
        /// </summary>
        [Fact]
        public void KeepPart_KeepsTheNonMotionCommandsInTheSameOrder()
        {
            var source = SectionTestSupport.Parse(SectionTestSupport.Board);
            var sections = SectionTestSupport.Choose(source, 2, 2, (0, 1), (1, 0));

            var output = SectionTestSupport.Keep(source, sections);

            Assert.Equal(NonMotion(source), NonMotion(output));
            Assert.Contains("S10000", NonMotion(output));
            Assert.Contains("M2", NonMotion(output));
        }

        /// <summary>
        /// Board with a dwell after the spindle starts, and one after each plunge: the left
        /// trace in the left section, the right trace in the right one.
        /// </summary>
        private static readonly string[] DwellBoard =
        {
            "G21", "G90", "G0 Z10", "G0 X0 Y0", "G0 Z1", "M3", "G4 P3",
            "G0 X2 Y2", "G1 Z-0.1 F200", "G4 P0", "G1 X8 Y2 F600", "G1 X8 Y18", "G0 Z1",
            "G0 X12 Y2", "G1 Z-0.1 F200", "G4 P0", "G1 X18 Y2 F600", "G1 X18 Y18", "G0 Z1",
            "G0 Z10", "M5", "M2"
        };

        private const double SpindleDwellSeconds = 3;
        private const double PlungeDwellSeconds = 0;

        /// <summary>
        /// Catches two faults: a left-out cut's dwell still written, which stops the machine once
        /// for each left-out path, and a kept dwell dropped along with it.
        /// </summary>
        [Fact]
        public void KeepPart_DropsTheDwellOfALeftOutCut_AndKeepsTheRest()
        {
            var source = SectionTestSupport.Parse(DwellBoard);
            var sections = SectionTestSupport.Choose(source, 2, 1, (0, 0));

            var dwells = SectionTestSupport.Keep(source, sections).Toolpath.OfType<Dwell>()
                .Select(d => d.Seconds).ToList();

            Assert.Equal(new[] { SpindleDwellSeconds, PlungeDwellSeconds }, dwells);
        }

        /// <summary>The commands that are not moves, by name: the clip's rise to the machine's safe height is a move.</summary>
        private static List<string> NonMotion(GCodeFile file) =>
            file.Toolpath.Where(c => c is not Motion && !SectionTestSupport.IsTheClipsRetract(c))
                .Select(c => c switch
                {
                    Spindle s => FormattableString.Invariant($"S{s.Speed}"),
                    MCode m => FormattableString.Invariant($"M{m.Code}"),
                    TCode t => FormattableString.Invariant($"T{t.ToolNumber}"),
                    _ => c.GetType().Name
                }).ToList();

        /// <summary>
        /// Catches a full-circle arc being kept or dropped whole instead of split at the line:
        /// the ends must be on the line and on the circle, with the same center and direction,
        /// and only the chosen half kept.
        /// </summary>
        [Fact]
        public void KeepPart_SplitsAFullCircleArcAtTheLine_KeepingOnlyTheChosenHalf()
        {
            var source = SectionTestSupport.Parse(ArcBoard);
            var (division, _) = BoardDivision.Of(source, 2, 1);
            Assert.Equal(ArcCenterX, division!.Min.X + (division.Max.X - division.Min.X) / 2, Tolerance);
            var sections = SectionTestSupport.Choose(source, 2, 1, (0, 0));

            var output = SectionTestSupport.Keep(source, sections);

            var arcs = output.Toolpath.OfType<Arc>().ToList();
            Assert.Equal(2, arcs.Count);
            foreach (var arc in arcs)
            {
                Assert.Equal(ArcCenterX, arc.U, Tolerance);
                Assert.Equal(ArcCenterY, arc.V, Tolerance);
                Assert.Equal(ArcDirection.CW, arc.Direction);
                foreach (var end in new[] { arc.Start, arc.End })
                {
                    double fromCenter = Math.Sqrt(Math.Pow(end.X - ArcCenterX, 2) + Math.Pow(end.Y - ArcCenterY, 2));
                    Assert.Equal(ArcRadius, fromCenter, SectionTestSupport.SampleTolerance);
                }
                Assert.True(arc.Interpolate(0.5).X < ArcCenterX, "the kept arc bulges into the right half");
            }

            Assert.Contains(arcs, a => Math.Abs(a.End.X - ArcCenterX) < Tolerance);
            Assert.Contains(arcs, a => Math.Abs(a.Start.X - ArcCenterX) < Tolerance);
            SectionTestSupport.AssertEveryCutIsInAChosenSection(output, sections, "arc");
            SectionTestSupport.AssertNothingChosenIsLost(source, output, sections, "arc");
        }

        /// <summary>
        /// Catches a file that never rises above the surface before cutting getting a
        /// travel height made up for it: there is no safe height to travel at, so refuse.
        /// </summary>
        [Fact]
        public void KeepPart_RefusesAFileWithNoTravelHeight_WhenTheChoiceNeedsTravel()
        {
            var source = SectionTestSupport.Parse(NoTravelHeightBoard);
            var sections = SectionTestSupport.Choose(source, 2, 2, (0, 0), (1, 1));

            var (output, refused) = source.KeepPart(null, sections);

            Assert.Null(output);
            Assert.Equal(Constants.ErrorSectionsNoTravelHeight, refused);
        }

        /// <summary>
        /// Catches the output losing the file's identity or warnings, or the clip editing the
        /// source's own toolpath.
        /// </summary>
        [Fact]
        public void KeepPart_KeepsTheNamePathAndWarnings_AndLeavesTheSourceAlone()
        {
            string path = Path.Combine(Path.GetTempPath(), "coppercli-sections-" + Guid.NewGuid().ToString("N") + ".ngc");
            File.WriteAllLines(path, UnknownCommandFile);
            try
            {
                var source = GCodeFile.Load(path);
                Assert.NotEmpty(source.Warnings);
                var before = source.GetGCode();
                var sections = SectionTestSupport.Choose(source, 2, 2, (0, 0));

                var output = SectionTestSupport.Keep(source, sections);

                Assert.Equal(source.FileName, output.FileName);
                Assert.Equal(source.FilePath, output.FilePath);
                Assert.NotEmpty(output.FilePath);
                Assert.Equal(source.Warnings, output.Warnings);
                Assert.Equal(before, source.GetGCode());
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// Catches output G-code that cannot be read back, or whose moves move: the
        /// 3-decimal text must parse to the same number of moves with the same end points.
        /// </summary>
        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 1)]
        public void KeepPart_OutputSurvivesARoundTripThroughText(int column, int row)
        {
            var source = SectionTestSupport.Parse(SectionTestSupport.Board);
            var sections = SectionTestSupport.Choose(source, 3, 3, (column, row), (2, 0));
            var output = SectionTestSupport.Keep(source, sections);

            var parsed = GCodeFile.FromList(output.GetGCode());

            var expected = output.Toolpath.OfType<Motion>().ToList();
            var actual = parsed.Toolpath.OfType<Motion>().ToList();
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                for (int axis = 0; axis < 3; axis++)
                {
                    // A move that does not give an axis leaves it where it was, which may not be known.
                    if (expected[i] is Line { PositionValid: var given } && !given[axis])
                    {
                        continue;
                    }
                    Assert.Equal(expected[i].End[axis], actual[i].End[axis], SectionTestSupport.OutputDecimals);
                }
            }
        }

        /// <summary>
        /// Catches a split arc written with a center or ends that read back elsewhere: the I and
        /// J words are relative to where the arc starts, which the split moved.
        /// </summary>
        [Fact]
        public void KeepPart_ASplitArcSurvivesARoundTripThroughText()
        {
            var source = SectionTestSupport.Parse(ArcBoard);
            var output = SectionTestSupport.Keep(source, SectionTestSupport.Choose(source, 2, 1, (0, 0)));

            var parsed = GCodeFile.FromList(output.GetGCode());

            var expected = output.Toolpath.OfType<Arc>().ToList();
            var actual = parsed.Toolpath.OfType<Arc>().ToList();
            Assert.NotEmpty(expected);
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].U, actual[i].U, SectionTestSupport.OutputDecimals);
                Assert.Equal(expected[i].V, actual[i].V, SectionTestSupport.OutputDecimals);
                Assert.Equal(expected[i].Start.X, actual[i].Start.X, SectionTestSupport.OutputDecimals);
                Assert.Equal(expected[i].Start.Y, actual[i].Start.Y, SectionTestSupport.OutputDecimals);
                Assert.Equal(expected[i].End.X, actual[i].End.X, SectionTestSupport.OutputDecimals);
                Assert.Equal(expected[i].End.Y, actual[i].End.Y, SectionTestSupport.OutputDecimals);
                Assert.Equal(expected[i].Direction, actual[i].Direction);
            }
        }

        /// <summary>
        /// Catches any board the operator can build breaking the rules: 200 random boards,
        /// random divisions and choices, each checked for 4a, 4b and 4d.
        /// </summary>
        [Fact]
        public void KeepPart_HoldsOnRandomBoards()
        {
            int kept = 0;
            for (int board = 0; board < RandomBoards; board++)
            {
                var random = new Random(RandomSeedBase + board);
                var source = SectionTestSupport.Parse(RandomBoard(random));
                int columns = random.Next(1, MaxRandomColumnsAndRows + 1);
                int rows = random.Next(1, MaxRandomColumnsAndRows + 1);
                if (columns * rows < 2)
                {
                    columns = 2;
                }

                var all = Enumerable.Range(0, columns).SelectMany(c => Enumerable.Range(0, rows).Select(r => new BoardCell(c, r))).ToList();
                var chosen = all.Where(_ => random.Next(2) == 0).ToList();
                if (chosen.Count == 0)
                {
                    chosen.Add(all[random.Next(all.Count)]);
                }
                else if (chosen.Count == all.Count)
                {
                    chosen.RemoveAt(random.Next(chosen.Count));
                }

                var (sections, refusedChoice) = BoardSections.Choose(source, columns, rows, chosen);
                Assert.True(refusedChoice == null && sections != null, $"board {board}: the choice was refused: {refusedChoice}");

                var (output, refused) = source.KeepPart(null, sections!);
                if (!source.CellsCut(sections!.Division, null).Overlaps(sections.Chosen))
                {
                    Assert.Equal(Constants.ErrorNothingToCut, refused);
                    kept++;
                    continue;
                }
                Assert.True(refused == null && output != null, $"board {board}: KeepPart refused: {refused}");

                string context = $"board {board} ({columns}x{rows}, {chosen.Count} chosen)";
                SectionTestSupport.AssertEveryCutIsInAChosenSection(output!, sections!, context);
                SectionTestSupport.AssertNothingChosenIsLost(source, output!, sections!, context);
                SectionTestSupport.AssertNoSidewaysTravelBelowTravelHeight(source, output!, context);
                kept++;
            }

            Assert.Equal(RandomBoards, kept);
        }

        private static string[] RandomBoard(Random random)
        {
            string Coordinate() =>
                (random.Next(RandomCoordinateMax) * RandomCoordinateStep).ToString("0.0", CultureInfo.InvariantCulture);

            var lines = new List<string> { "G21", "G90", "G0 Z10", "G0 X0 Y0", "G0 Z1" };
            int paths = random.Next(RandomPathsMin, RandomPathsMax + 1);
            for (int p = 0; p < paths; p++)
            {
                lines.Add($"G0 X{Coordinate()} Y{Coordinate()}");
                lines.Add("G1 Z-0.1 F200");
                int segments = random.Next(RandomSegmentsMin, RandomSegmentsMax + 1);
                for (int s = 0; s < segments; s++)
                {
                    lines.Add($"G1 X{Coordinate()} Y{Coordinate()} F600");
                }
                lines.Add(FormattableString.Invariant($"G0 Z{RandomRetractHeights[random.Next(RandomRetractHeights.Length)]}"));
            }

            int drills = random.Next(RandomDrillsMax + 1);
            for (int d = 0; d < drills; d++)
            {
                lines.Add($"G0 X{Coordinate()} Y{Coordinate()}");
                lines.Add("G1 Z-1.7 F100");
                lines.Add("G1 Z1");
            }

            lines.AddRange(new[] { "G0 Z10", "M5", "M2" });
            return lines.ToArray();
        }

        // ---- 5. Motion ----

        private static IEnumerable<Motion> SampleMotions()
        {
            yield return new Line
            {
                Start = new Vector3(0.1, 0.2, -0.3), End = new Vector3(7.7, 3.3, -0.1), Feed = 100,
                PositionValid = new[] { true, true, true }, StartValid = true
            };
            yield return new Line
            {
                Start = new Vector3(1, 1, 1), End = new Vector3(1, 1, -1.7), Feed = 100,
                PositionValid = new[] { true, true, true }, StartValid = true
            };
            yield return SectionTestSupport.Parse(ArcBoard).Toolpath.OfType<Arc>().Single();
            yield return SectionTestSupport.Parse("G0 X0 Y0 Z1", "G1 Z-0.1 F100", "G3 X4 Y0 I2 J0").Toolpath.OfType<Arc>().Single();
        }

        /// <summary>
        /// Catches an endpoint that drifts by rounding (Interpolate(1) computed along the
        /// path instead of returned): neighbors would leave a gap or overlap.
        /// </summary>
        [Fact]
        public void Interpolate_GivesTheStartAndTheEndExactly()
        {
            foreach (var motion in SampleMotions())
            {
                Assert.True(SectionTestSupport.Exact(motion.Interpolate(0), motion.Start), $"{motion.GetType().Name} Interpolate(0)");
                Assert.True(SectionTestSupport.Exact(motion.Interpolate(1), motion.End), $"{motion.GetType().Name} Interpolate(1)");
            }
        }

        /// <summary>
        /// Catches pieces that do not join (a gap in the path), are too long, or do not reach
        /// the ends exactly.
        /// </summary>
        [Theory]
        [InlineData(SlicedLength)]
        [InlineData(0.37)]
        [InlineData(100)]
        public void Split_JoinsExactly_AndNoPieceIsLongerThanRequested(double length)
        {
            foreach (var motion in SampleMotions())
            {
                var pieces = motion.Split(length).ToList();

                Assert.True(SectionTestSupport.Exact(pieces[0].Start, motion.Start), "the first piece starts at Start");
                Assert.True(SectionTestSupport.Exact(pieces[^1].End, motion.End), "the last piece ends at End");
                for (int i = 1; i < pieces.Count; i++)
                {
                    Assert.True(SectionTestSupport.Exact(pieces[i].Start, pieces[i - 1].End),
                        $"{motion.GetType().Name}: piece {i} does not start where piece {i - 1} ends");
                }
                Assert.All(pieces, p => Assert.True(p.Length <= length + Tolerance,
                    $"{motion.GetType().Name}: a piece is {p.Length} long, over {length}"));
            }
        }

        /// <summary>
        /// Catches Slice measuring from the wrong end or rounding its ends.
        /// </summary>
        [Fact]
        public void Slice_OfALine_RunsFromTheInterpolatedStartToTheInterpolatedEnd()
        {
            var line = (Line)SampleMotions().First();

            var piece = line.Slice(0.25, 0.8);

            Assert.True(SectionTestSupport.Exact(piece.Start, line.Interpolate(0.25)));
            Assert.True(SectionTestSupport.Exact(piece.End, line.Interpolate(0.8)));
            Assert.Equal(line.Feed, piece.Feed);
        }

        /// <summary>
        /// Catches a crossing missed, reported at the ends, or placed at the wrong ratio.
        /// </summary>
        [Fact]
        public void RatiosWhereXIsAndYIs_AreStrictlyInsideAndOnTheValue()
        {
            var line = (Line)SampleMotions().First();
            var circle = SampleMotions().OfType<Arc>().First();

            foreach (var (motion, x, y, crossingsX, crossingsY) in new (Motion, double, double, int, int)[]
            {
                (line, 4.0, 2.0, 1, 1),
                (circle, ArcCenterX, ArcCenterY, 2, 1)
            })
            {
                var atX = motion.RatiosWhereXIs(x).ToList();
                var atY = motion.RatiosWhereYIs(y).ToList();

                Assert.Equal(crossingsX, atX.Count);
                Assert.Equal(crossingsY, atY.Count);
                Assert.All(atX, r => { Assert.InRange(r, double.Epsilon, 1 - 1e-15); Assert.Equal(x, motion.Interpolate(r).X, Tolerance); });
                Assert.All(atY, r => { Assert.InRange(r, double.Epsilon, 1 - 1e-15); Assert.Equal(y, motion.Interpolate(r).Y, Tolerance); });
            }
        }

        /// <summary>A quarter circle of radius ArcRadius about the origin, from (ArcRadius, 0) to (0, ArcRadius).</summary>
        private static readonly string[] QuarterCounterclockwise =
            { "G21", "G90", "G0 X2 Y0 Z1", "G1 Z-0.1 F100", "G3 X0 Y2 I-2 J0" };

        /// <summary>The same quarter circle, the other way round.</summary>
        private static readonly string[] QuarterClockwise =
            { "G21", "G90", "G0 X0 Y2 Z1", "G1 Z-0.1 F100", "G2 X2 Y0 I0 J-2" };

        private const double QuarterCrossingX = 1;

        /// <summary>
        /// Catches an arc's crossings measured in the wrong direction around the circle. A full
        /// circle hides it, because it crosses a line at the same ratios either way; a quarter
        /// circle crosses X = QuarterCrossingX once, a third of the way along clockwise and two
        /// thirds counterclockwise.
        /// </summary>
        [Theory]
        [InlineData(true, 2.0 / 3)]
        [InlineData(false, 1.0 / 3)]
        public void ArcRatios_FollowTheArcsDirection(bool counterclockwise, double expectedRatio)
        {
            var arc = SectionTestSupport.Parse(counterclockwise ? QuarterCounterclockwise : QuarterClockwise)
                .Toolpath.OfType<Arc>().Single();

            var ratios = arc.RatiosWhereXIs(QuarterCrossingX).ToList();

            Assert.Equal(expectedRatio, Assert.Single(ratios), Tolerance);
            var crossing = arc.Interpolate(ratios[0]);
            Assert.Equal(QuarterCrossingX, crossing.X, Tolerance);
            Assert.Equal(ArcRadius, Math.Sqrt(crossing.X * crossing.X + crossing.Y * crossing.Y), Tolerance);
        }

        /// <summary>
        /// Catches a line that does not reach the value, or only touches it at an end, being
        /// given a crossing (which would cut a sliver of zero length).
        /// </summary>
        [Fact]
        public void RatiosWhereXIsAndYIs_AreEmpty_ForALineThatDoesNotCross()
        {
            var line = (Line)SampleMotions().First();
            var vertical = (Line)SampleMotions().ElementAt(1);

            Assert.Empty(line.RatiosWhereXIs(50));
            Assert.Empty(line.RatiosWhereYIs(-50));
            Assert.Empty(line.RatiosWhereXIs(line.Start.X));
            Assert.Empty(line.RatiosWhereXIs(line.End.X));
            Assert.Empty(vertical.RatiosWhereXIs(vertical.Start.X));
            Assert.Empty(vertical.RatiosWhereYIs(vertical.Start.Y));
            Assert.Empty(SampleMotions().OfType<Arc>().First().RatiosWhereXIs(50));
        }

        /// <summary>
        /// Catches an XZ arc being sliced as if it were in XY, which would cut it in the
        /// wrong places.
        /// </summary>
        [Fact]
        public void ArcRatios_ThrowForAnArcOutsideTheXYPlane()
        {
            var arc = SectionTestSupport.Parse(XZArcBoard).Toolpath.OfType<Arc>().Single();

            Assert.Throws<InvalidOperationException>(() => arc.RatiosWhereXIs(1));
            Assert.Throws<InvalidOperationException>(() => arc.RatiosWhereYIs(1));
        }

        // ---- 6. CellsCut ----

        /// <summary>
        /// Catches a section that only has a rapid or a travel in it counting as cut, which
        /// would show copper-free sections as holding work.
        /// </summary>
        [Fact]
        public void CellsCut_ListsTheCellsACutPassesThrough_NotThoseWithOnlyTravel()
        {
            var source = SectionTestSupport.Parse(SectionTestSupport.TwoIslands.Concat(new string[0]).ToArray());
            var (division, _) = BoardDivision.Of(source, 2, 2);

            var cut = source.CellsCut(division!, null);

            Assert.Equal(new HashSet<BoardCell> { new(0, 0), new(1, 1) }, cut);
        }

        /// <summary>
        /// Catches a cut across a line counting for one side only.
        /// </summary>
        [Fact]
        public void CellsCut_IncludesEveryCellACutCrosses()
        {
            var source = SectionTestSupport.Parse(SectionTestSupport.Board);
            var (division, _) = BoardDivision.Of(source, 2, 2);

            Assert.Equal(4, source.CellsCut(division!, null).Count);
        }
    }

    // What AppState builds from a choice of sections: the machine's G-code, the version and
    // the reset rules, next to the depth adjustment and the height map.
    [Collection(WebServerCollection.Name)]
    public class BoardSectionsAppStateTests : JobFixtureTests
    {
        private const double Tolerance = 1e-9;
        private const double TestDepth = -0.10;
        private const double OtherDepth = -0.26;
        private const double ChosenCutZ = -0.2;
        private const double ChosenDrillZ = -1.8;
        private const int SectionColumns = 2;
        private const int SectionRows = 2;
        private const int TotalSections = SectionColumns * SectionRows;
        private const int ChosenCount = 2;

        public BoardSectionsAppStateTests(WebServerFixture web) : base(web)
        {
        }

        private GCodeFile GivenTheBoardIsLoaded(string[]? lines = null)
        {
            var loaded = _boards.Load(lines ?? SectionTestSupport.Board);
            Assert.Null(AppState.MillSections);
            return loaded;
        }


        private static BoardCell[] LowerLeft() => new[] { new BoardCell(0, 0) };

        private const double MapBaseHeight = 0.05;
        private const double MapRisePerColumn = 0.1;

        /// <summary>
        /// Catches applying the height map dropping the chosen sections, which would mill the
        /// whole board while the pre-mill screens still name the sections.
        /// </summary>
        [Fact]
        public void ApplyingTheMap_KeepsTheSections_AndAppliesTheMapOverTheClippedFile()
        {
            var loaded = GivenTheBoardIsLoaded();
            Persistence.ClearProbeAutoSave();
            Assert.Null(AppState.AdoptProbeGrid(WebServerFixture.CompleteMapForThisJob(MapBaseHeight, MapRisePerColumn)));
            Assert.Null(AppState.ChooseMillSections(SectionColumns, SectionRows, LowerLeft()));
            var sections = AppState.MillSections!;
            long before = AppState.MachineFileVersion;

            Assert.Null(AppState.ApplyProbeData());

            Assert.Same(sections, AppState.MillSections);
            Assert.True(AppState.AreProbePointsApplied);
            Assert.NotEqual(before, AppState.MachineFileVersion);
            Assert.Equal(loaded.KeepPart(null, sections).File!.ApplyProbeGrid(AppState.ProbePoints!).GetGCode(), OnTheMachine());
        }

        /// <summary>
        /// Catches a choice that changes the number and not the lines: the machine's file
        /// must drop the cuts outside the chosen section and the machine must hold those lines.
        /// </summary>
        [Fact]
        public void ChoosingSections_RebuildsTheMachinesGCode_WithOnlyTheChosenCuts()
        {
            var loaded = GivenTheBoardIsLoaded();
            long before = AppState.MachineFileVersion;

            Assert.Null(AppState.ChooseMillSections(SectionColumns, SectionRows, LowerLeft()));

            Assert.NotNull(AppState.MillSections);
            Assert.NotEqual(before, AppState.MachineFileVersion);
            var sections = AppState.MillSections!;
            Assert.Equal(new[] { new BoardCell(0, 0) }, sections.Chosen);
            var machineFile = AppState.MachineFile!;
            SectionTestSupport.AssertEveryCutIsInAChosenSection(machineFile, sections, "machine file");
            SectionTestSupport.AssertNothingChosenIsLost(loaded, machineFile, sections, "machine file");
            Assert.Equal(machineFile.GetGCode(), OnTheMachine());
            Assert.NotEqual(loaded.GetGCode(), OnTheMachine());
            Assert.Same(loaded, AppState.CurrentFile);
            Assert.DoesNotContain(machineFile.Toolpath.OfType<Motion>(), m => m.End.X == 15 && m.End.Y == 15 && m.End.Z < 0);
        }

        /// <summary>
        /// Catches "none" or "all" leaving a half-built file on the machine, including one
        /// that stays after an earlier choice.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(TotalSections)]
        public void ChoosingNoneOrAll_MillsTheWholeBoard_AndTheMachineHoldsTheFilesOwnGCode(int count)
        {
            var loaded = GivenTheBoardIsLoaded();
            Assert.Null(AppState.ChooseMillSections(SectionColumns, SectionRows, LowerLeft()));
            Assert.NotEqual(loaded.GetGCode(), OnTheMachine());
            var everySection = Enumerable.Range(0, TotalSections)
                .Select(i => new BoardCell(i % SectionColumns, i / SectionColumns)).Take(count);

            Assert.Null(AppState.ChooseMillSections(SectionColumns, SectionRows, everySection));

            Assert.Null(AppState.MillSections);
            Assert.Equal(loaded.GetGCode(), AppState.MachineFile!.GetGCode());
            Assert.Equal(loaded.GetGCode(), OnTheMachine());
        }

        /// <summary>
        /// Catches a section choice surviving a new file: it would cut the new board with
        /// the old board's division.
        /// </summary>
        [Fact]
        public void LoadingAFile_ResetsTheSections()
        {
            GivenTheBoardIsLoaded();
            Assert.Null(AppState.ChooseMillSections(SectionColumns, SectionRows, LowerLeft()));
            var next = GCodeFile.Load(_boards.Write(SectionTestSupport.TwoIslands));

            Assert.Null(AppState.LoadGCodeIntoMachine(next).Refused);

            Assert.Null(AppState.MillSections);
            Assert.Equal(next.GetGCode(), OnTheMachine());
        }

        /// <summary>
        /// Catches a map adopted for one surface being applied to a section choice made
        /// before it: the map resets the choice like the depth.
        /// </summary>
        [Fact]
        public void AdoptingAMap_ResetsTheSections()
        {
            var loaded = GivenTheBoardIsLoaded();
            Assert.Null(AppState.ChooseMillSections(SectionColumns, SectionRows, LowerLeft()));

            Assert.Null(AppState.AdoptProbeGrid(WebServerFixture.CompleteMapForThisJob(0.05, 0.1)));

            Assert.Null(AppState.MillSections);
            Assert.Equal(loaded.GetGCode(), OnTheMachine());
        }

        /// <summary>
        /// Catches the depth and the sections not composing: every cut is deeper by the
        /// depth and still only in the chosen section, and each change keeps the other.
        /// </summary>
        [Fact]
        public void DepthAndSections_Combine_AndEachChangeKeepsTheOther()
        {
            var loaded = GivenTheBoardIsLoaded();
            Assert.Null(AppState.SetDepthAdjustment(TestDepth));

            Assert.Null(AppState.ChooseMillSections(SectionColumns, SectionRows, LowerLeft()));

            Assert.Equal(TestDepth, AppState.DepthAdjustment, Tolerance);
            var sections = AppState.MillSections!;
            var machineFile = AppState.MachineFile!;
            SectionTestSupport.AssertEveryCutIsInAChosenSection(machineFile, sections, "depth and sections");
            var cutZs = SectionTestSupport.Cuts(machineFile).SelectMany(m => new[] { m.Start.Z, m.End.Z })
                .Where(z => z < 0).Distinct().ToList();
            Assert.NotEmpty(cutZs);
            Assert.All(cutZs, z => Assert.True(
                Math.Abs(z - ChosenCutZ) < Tolerance || Math.Abs(z - ChosenDrillZ) < Tolerance,
                $"a cut is at Z{z}, not the source's Z + {TestDepth}"));
            Assert.Equal(loaded.KeepPart(null, sections).File!.OffsetCutDepth(TestDepth).GetGCode(), OnTheMachine());

            Assert.Null(AppState.SetDepthAdjustment(OtherDepth));
            Assert.Same(sections, AppState.MillSections);
            Assert.Equal(OtherDepth, AppState.DepthAdjustment, Tolerance);

            Assert.Null(AppState.ChooseMillSections(SectionColumns, SectionRows,
                new[] { new BoardCell(1, 1), new BoardCell(0, 1) }));
            Assert.Equal(OtherDepth, AppState.DepthAdjustment, Tolerance);
            Assert.Equal(ChosenCount, AppState.MillSections!.Chosen.Count);
        }

        /// <summary>
        /// Catches a choice with no file loaded creating a build from nothing.
        /// </summary>
        [Fact]
        public void ChoosingSections_WithNoFileLoaded_IsRefused()
        {
            GivenTheBoardIsLoaded();
            AppState.UnloadFileForTest();

            Assert.Equal(CliConstants.ErrorNoFileLoaded,
                AppState.ChooseMillSections(SectionColumns, SectionRows, LowerLeft()));
            Assert.Null(AppState.MillSections);
            Assert.Null(AppState.MachineFile);
        }

        /// <summary>
        /// Catches a choice during a run swapping the file under it, and a refusal that
        /// changes something anyway.
        /// </summary>
        [Fact]
        public async Task ChoosingSections_DuringARun_IsRefused_AndChangesNothing()
        {
            await _web.LoadWithAMapAppliedAsync(_boards.Write(SectionTestSupport.Board));
            Assert.Null(AppState.ChooseMillSections(SectionColumns, SectionRows, LowerLeft()));

            await _web.WhileAMillRunHoldsAtTheDoorAsync(() =>
            {
                long version = AppState.MachineFileVersion;
                string[] lines = OnTheMachine();

                string? refused = AppState.ChooseMillSections(SectionColumns, SectionRows,
                    new[] { new BoardCell(1, 1) });

                Assert.Equal(AppState.WhyTheFileCannotChange(), refused);
                Assert.Equal(CliConstants.ErrorFileChangeDuringRun, refused);
                Assert.Equal(version, AppState.MachineFileVersion);
                Assert.Equal(new[] { new BoardCell(0, 0) }, AppState.MillSections!.Chosen);
                Assert.Equal(lines, OnTheMachine());
                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// Catches a choice the file cannot take (too many columns, a section off the board)
        /// changing the version or the machine's lines anyway.
        /// </summary>
        [Fact]
        public void ARefusedChoice_ChangesNothing()
        {
            GivenTheBoardIsLoaded();
            Assert.Null(AppState.ChooseMillSections(SectionColumns, SectionRows, LowerLeft()));
            long version = AppState.MachineFileVersion;
            string[] lines = OnTheMachine();

            string? tooMany = AppState.ChooseMillSections(Constants.MaxSectionsPerAxis + 1, 1, LowerLeft());
            string? offBoard = AppState.ChooseMillSections(SectionColumns, SectionRows, new[] { new BoardCell(5, 5) });

            Assert.Equal(string.Format(Constants.ErrorSectionCountFormat, Constants.MaxSectionsPerAxis), tooMany);
            Assert.Equal(Constants.ErrorSectionOutsideBoard, offBoard);
            Assert.Equal(version, AppState.MachineFileVersion);
            Assert.Equal(lines, OnTheMachine());
        }

        /// <summary>
        /// Catches the words for the pre-mill screen being derived twice and differing.
        /// </summary>
        [Fact]
        public void TheSectionsText_SaysWholeBoard_OrHowManyOfHowMany()
        {
            var source = SectionTestSupport.Parse(SectionTestSupport.Board);
            var two = SectionTestSupport.Choose(source, 3, 2, (0, 0), (1, 1));

            Assert.Equal(CliConstants.SectionsWholeBoard, DisplayHelpers.GetSectionsText(null));
            Assert.Equal(string.Format(CliConstants.SectionsChosenFormat, 2, 6), DisplayHelpers.GetSectionsText(two));
            Assert.Equal(CliConstants.SectionsWholeBoard, DisplayHelpers.GetSectionsText(6, 6));
            Assert.Equal(CliConstants.SectionsWholeBoard, DisplayHelpers.GetSectionsText(0, 6));
        }
    }
}
