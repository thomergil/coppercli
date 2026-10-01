#nullable enable
using System.Collections.Generic;
using System.Linq;
using coppercli.Core.GCode;
using coppercli.Core.GCode.GCodeCommands;
using coppercli.Core.Util;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// Tests M6 and M0 recognition, tool details near M6, and agreement with
    /// ClassifyPauseLine. OpenCNCPilot has no reference tool-change implementation.
    /// </summary>
    public class GCodeParserM6Tests
    {
        [Theory]
        [InlineData("M6", true)]
        [InlineData("M06", true)]
        [InlineData("m6", true)]
        [InlineData("m06", true)]
        [InlineData("M6 T1", true)]
        [InlineData("M06 T2", true)]
        [InlineData("T1 M6", true)]
        [InlineData("  M6  ", true)]
        [InlineData("G0 M6 X10", true)]
        [InlineData("G0 X0", false)]
        [InlineData("M60", false)]
        [InlineData("M16", false)]
        [InlineData("", false)]
        [InlineData("M0", false)]
        public void IsM6Line_DetectsM6Correctly(string line, bool expected)
        {
            Assert.Equal(expected, GCodeParser.IsM6Line(line));
        }

        [Theory]
        [InlineData("M0", true)]
        [InlineData("M00", true)]
        [InlineData("m0", true)]
        [InlineData("m00", true)]
        [InlineData("M000", true)]
        [InlineData("  M0  ", true)]
        [InlineData("G0 M0", true)]
        [InlineData("M01", false)]  // M01 is an optional stop
        [InlineData("M6", false)]
        [InlineData("G0 X0", false)]
        [InlineData("", false)]
        public void IsM0Line_DetectsM0Correctly(string line, bool expected)
        {
            Assert.Equal(expected, GCodeParser.IsM0Line(line));
        }

        [Theory]
        [InlineData("T1", 1)]
        [InlineData("T01", 1)]
        [InlineData("T12", 12)]
        [InlineData("M6 T5", 5)]
        [InlineData("T3 M6", 3)]
        [InlineData("t7", 7)]
        [InlineData("  T99  ", 99)]
        [InlineData("G0 X0", null)]
        [InlineData("M6", null)]
        [InlineData("", null)]
        public void ExtractToolNumber_ExtractsCorrectly(string line, int? expected)
        {
            Assert.Equal(expected, GCodeParser.ExtractToolNumber(line));
        }

        [Theory]
        [InlineData("T1 (0.8mm drill)", "0.8mm drill")]
        [InlineData("(End Mill)", "End Mill")]
        [InlineData("M6 T1 (V-bit 60deg)", "V-bit 60deg")]
        [InlineData("T1", null)]
        [InlineData("G0 X0", null)]
        [InlineData("", null)]
        [InlineData("(  spaced name  )", "spaced name")]
        public void ExtractToolName_ExtractsCorrectly(string line, string? expected)
        {
            Assert.Equal(expected, GCodeParser.ExtractToolName(line));
        }

        [Fact]
        public void FindToolInfo_FindsToolOnSameLine()
        {
            var lines = new List<string>
            {
                "G0 X0",
                "M6 T2 (drill bit)",
                "G0 X10"
            };

            var (number, name) = GCodeParser.FindToolInfo(lines, 1);

            Assert.Equal(2, number);
            Assert.Equal("drill bit", name);
        }

        [Fact]
        public void FindToolInfo_FindsToolOnPreviousLine()
        {
            var lines = new List<string>
            {
                "G0 X0",
                "T3 (end mill)",
                "M6",
                "G0 X10"
            };

            var (number, name) = GCodeParser.FindToolInfo(lines, 2);

            Assert.Equal(3, number);
            Assert.Equal("end mill", name);
        }

        [Fact]
        public void FindToolInfo_ReturnsNullWhenNoTool()
        {
            var lines = new List<string>
            {
                "G0 X0",
                "M6",
                "G0 X10"
            };

            var (number, name) = GCodeParser.FindToolInfo(lines, 1);

            Assert.Null(number);
            Assert.Null(name);
        }

        [Fact]
        public void FindToolInfo_SearchesBackwardUpToToolInfoSearchLines()
        {
            var lines = new List<string>
            {
                "T5 (far away tool)",  // line 0, further back than ToolInfoSearchLines
                "G0 X0",
                "G0 X1",
                "G0 X2",
                "G0 X3",
                "G0 X4",
                "G0 X5",
                "G0 X6",
                "G0 X7",
                "G0 X8",
                "G0 X9",
                "T7 (nearby tool)",    // line 11, within range of the M6 on line 12
                "M6"
            };

            var (number, name) = GCodeParser.FindToolInfo(lines, 12);

            Assert.Equal(7, number);
            Assert.Equal("nearby tool", name);
        }

        [Fact]
        public void FindToolInfo_FindsToolNameOnSeparateLine()
        {
            // pcb2gcode writes the tool name as a comment on the line before the M6.
            var lines = new List<string>
            {
                "G0 X0",
                "(isolation cutter)",
                "M6 T2",
                "G0 X10"
            };

            var (number, name) = GCodeParser.FindToolInfo(lines, 2);

            Assert.Equal(2, number);
            Assert.Equal("isolation cutter", name);
        }

        [Fact]
        public void FindToolInfo_HandlesEmptyList()
        {
            var lines = new List<string>();

            var (number, name) = GCodeParser.FindToolInfo(lines, 0);

            Assert.Null(number);
            Assert.Null(name);
        }

        [Fact]
        public void FindToolInfo_HandlesOutOfBoundsIndex()
        {
            // FindToolInfo searches lineIndex - ToolInfoSearchLines to lineIndex - 1, so an
            // index past the end reads the lines that do exist instead of throwing.
            var lines = new List<string>
            {
                "G0 X0",
                "G0 X1"
            };

            int outOfBoundsIndex = Constants.ToolInfoSearchLines;
            var (number, name) = GCodeParser.FindToolInfo(lines, outOfBoundsIndex);

            Assert.Null(number);
            Assert.Null(name);
        }

        [Fact]
        public void FindToolInfo_NegativeIndexReturnsNull()
        {
            var lines = new List<string>
            {
                "T1 M6"
            };

            var (number, name) = GCodeParser.FindToolInfo(lines, -1);

            Assert.Null(number);
            Assert.Null(name);
        }

        // Machine.cs withholds a line from GRBL when IsM6Line accepts it, and pauses where
        // ClassifyPauseLine reports a tool change. A line only IsM6Line accepts is withheld
        // and never paused for, so the job cuts on with the old tool in the spindle.
        [Theory]
        [InlineData("m6")]
        [InlineData("M6")]
        [InlineData("t2 m06")]
        [InlineData("M06 (Tool change.)")]
        public void ToolChangeLines_MatchBothRecognitionChecks(string line)
        {
            Assert.True(GCodeParser.IsM6Line(line));
            Assert.Equal(GCodeNumbers.PauseMCode.ToolChange, GCodeParser.ClassifyPauseLine(line));
        }

        [Theory]
        [InlineData("G1 X1 Y1 (rapid before M6)")]
        [InlineData("G1 X1 Y1 ; then M6 by hand")]
        public void AnMCodeInsideACommentIsNotAToolChange(string line)
        {
            Assert.False(GCodeParser.IsM6Line(line));
            Assert.Equal(GCodeNumbers.PauseMCode.None, GCodeParser.ClassifyPauseLine(line));
        }

        [Theory]
        [InlineData("G1 X1 Y1 (finished, was M2)")]
        [InlineData("G1 X1 Y1 (pause here, not M0)")]
        [InlineData("G1 X1 Y1 ; M30 comes later")]
        public void AnMCodeInsideACommentDoesNotEndOrPauseTheProgram(string line)
        {
            Assert.Equal(GCodeNumbers.PauseMCode.None, GCodeParser.ClassifyPauseLine(line));
            Assert.False(GCodeParser.IsM0Line(line));
        }

        [Fact]
        public void MCodeWithComment_IsRecognized()
        {
            Assert.Equal(GCodeNumbers.PauseMCode.ProgramEnd,
                GCodeParser.ClassifyPauseLine("M2 ( Program end. )"));
            Assert.Equal(GCodeNumbers.PauseMCode.ProgramStop,
                GCodeParser.ClassifyPauseLine("M0 (pause for inspection)"));
        }

        /// <summary>
        /// A job whose second tool's work starts with the retract pcb2gcode writes after the
        /// tool change, at the height the first tool's work ended at.
        /// </summary>
        private static readonly string[] RetractRepeatedAfterToolChange =
        {
            "G21", "G90", "G0 X0 Y0 Z35", "G1 Z-0.1 F200", "G1 X10 F600", "G0 Z35",
            "M6", "G0 Z35", "G0 X5 Y5", "G0 Z1", "G1 Z-1 F600"
        };

        private const string ToolChangeLine = "M6";
        private const string RetractAxis = "Z35";
        private const string CutFeedLine = "F600";

        /// <summary>
        /// Catches the retract after a tool change being dropped as a move to where the tool
        /// already is: without a tool setter the operator leaves the tool at the surface, and
        /// the next rapid would cross the board there.
        /// </summary>
        [Fact]
        public void TheRetractAfterAToolChange_IsKept()
        {
            var lines = GCodeFile.FromList(RetractRepeatedAfterToolChange).GetGCode();

            int change = lines.IndexOf(ToolChangeLine);
            int firstMoveAfter = lines.FindIndex(change, l => l.StartsWith("G0 "));
            Assert.Contains(RetractAxis, lines[firstMoveAfter]);
        }

        /// <summary>
        /// Catches the feed of the first cut after a tool change being left out because it
        /// equals the feed before it: the tool setter's probe has set the machine's feed since.
        /// </summary>
        [Fact]
        public void TheFeedAfterAToolChange_IsWrittenAgain()
        {
            var lines = GCodeFile.FromList(RetractRepeatedAfterToolChange).GetGCode();

            int change = lines.IndexOf(ToolChangeLine);
            int cut = lines.FindIndex(change, l => l.StartsWith("G1 "));
            Assert.Contains(CutFeedLine, lines.GetRange(change, cut - change));
        }

        /// <summary>
        /// Catches a tool change on a line the machine holds back whole, which would lose the
        /// move or probe on it, or on a G28 line, which is dropped with the tool change in it.
        /// </summary>
        [Theory]
        [InlineData("G53 G0 Z-1 M6")]
        [InlineData("G38.2 Z-5 F10 M6")]
        [InlineData("G28 M6")]
        public void AToolChangeOnALineWithAMachineMoveProbeOrHoming_IsRefused(string line)
        {
            var error = Assert.Throws<ParseException>(() => GCodeFile.FromList(new[] { "G21", "G90", "G0 X0 Y0 Z5", line }));

            Assert.Equal(Constants.ParseErrorToolChangeInBlock, error.Error);
            Assert.Equal(LineAfterThePositioning, error.Line);
        }

        /// <summary>The 1-based line after "G21", "G90" and "G0 X0 Y0 Z5".</summary>
        private const int LineAfterThePositioning = 4;

        /// <summary>
        /// Catches a move relative to the tool, or an arc, straight after a tool change loading
        /// from where the tool was before it, and the refusal not naming the tool change.
        /// </summary>
        [Theory]
        [InlineData("G91 G0 X1")]
        [InlineData("G2 X5 Y5 I1 J0 F100")]
        public void AMoveFromTheToolsPlace_RightAfterAToolChange_IsRefused_NamingTheToolChange(string move)
        {
            var error = Assert.Throws<ParseException>(() => GCodeFile.FromList(new[] { "G21", "G90", "G0 X0 Y0 Z5", ToolChangeLine, move }));

            Assert.EndsWith(string.Format(Constants.ParseErrorPositionLostAtToolChange, LineAfterThePositioning), error.Error);
        }

        /// <summary>Catches a tool change blamed for a position the file never gave before it.</summary>
        [Fact]
        public void AMoveFromTheToolsPlace_AfterAToolChangeBeforeAnyPosition_DoesNotNameTheToolChange()
        {
            var error = Assert.Throws<ParseException>(() => GCodeFile.FromList(new[] { "G21", "G90", "T1", ToolChangeLine, "G91 G0 X1" }));

            Assert.DoesNotContain(string.Format(Constants.ParseErrorPositionLostAtToolChange, LineAfterThePositioning), error.Error);
        }

        /// <summary>Catches a position lost to a G53 block blamed on a tool change before it.</summary>
        [Fact]
        public void AMoveFromTheToolsPlace_AfterAG53Block_DoesNotNameAnEarlierToolChange()
        {
            var error = Assert.Throws<ParseException>(() => GCodeFile.FromList(
                new[] { "G21", "G90", "G0 X0 Y0 Z5", ToolChangeLine, "G0 X0 Y0 Z5", "G53 G0 Z-1", "G91 G0 X1" }));

            Assert.DoesNotContain(string.Format(Constants.ParseErrorPositionLostAtToolChange, LineAfterThePositioning), error.Error);
        }

        /// <summary>
        /// Catches a cut whose start a tool change lost being cut at the file's depth, without
        /// the map: the map cannot follow it along its length, so it is fitted at its end.
        /// </summary>
        [Fact]
        public void ACutRightAfterAToolChange_EndsAtTheMapsHeightThere()
        {
            var file = GCodeFile.FromList(new[] { "G21", "G90", "G0 X0 Y0 Z1", ToolChangeLine, "G1 X10 Y0 Z-0.1 F100" });

            var cut = Assert.Single(file.ApplyProbeGrid(SlopedMap()).Toolpath.OfType<Line>(), line => !line.Rapid);

            Assert.Equal(CutZ + MapRisePerColumn, cut.End.Z, Precision);
        }

        /// <summary>
        /// Catches the map's travel taking a rapid after a tool change to start where the tool was
        /// before it: the writer then leaves out the axes that did not change, and the rapid
        /// neither rises to the file's height nor goes where the file says.
        /// </summary>
        [Fact]
        public void ARapidRightAfterAToolChange_WithAMap_WritesEveryAxis()
        {
            var file = GCodeFile.FromList(new[] { "G21", "G90", "G0 X10 Y5 Z35", "G1 Z-0.1 F100", "G0 Z35", ToolChangeLine, "G0 X5 Y5 Z35" });

            var lines = file.ApplyProbeGrid(SlopedMap()).GetGCode();

            string firstMoveAfter = lines[lines.FindIndex(lines.IndexOf(ToolChangeLine), l => l.StartsWith("G0 "))];
            Assert.Equal(new[] { 'X', 'Y', 'Z' }, firstMoveAfter.Split(' ').Skip(1).Select(word => word[0]));
        }

        /// <summary>
        /// A map that rises one step per column, so a move fitted at the wrong point, or with
        /// X and Y swapped, gets a different height.
        /// </summary>
        private static ProbeGrid SlopedMap()
        {
            var map = new ProbeGrid(MapGridSize, new Vector2(0, 0), new Vector2(MapSpan, MapSpan));
            for (int x = 0; x < map.SizeX; x++)
            {
                for (int y = 0; y < map.SizeY; y++)
                {
                    map.RecordMeasurement(x, y, x * MapRisePerColumn);
                }
            }
            return map;
        }

        private const double MapGridSize = 10.0;
        private const double MapSpan = 20.0;
        private const double MapRisePerColumn = 0.2;
        private const double CutZ = -0.1;
        private const int Precision = 6;

        /// <summary>
        /// The tool change between the traces and the drills of a pcb2gcode-combine file,
        /// trimmed from Mistopher-small_board_000_all.ngc.
        /// </summary>
        private static readonly string[] Pcb2GcodeToolChange =
        {
            "G90        ( Absolute distance mode )", "G21        ( Units: mm )", "G17        ( XY plane selection )",
            "G01 F600.00000 ( Feedrate. )", "G00 S12000     (RPM spindle speed.)", "M3      (Spindle on clockwise.)",
            "G00 X59.78115 Y1.62177 ( rapid move to begin. )", "G00 Z1.00000 ( retract )", "G01 F300.00000",
            "G01 Z-0.10000", "G01 F600.00000", "G01 X60.84717 Y83.04716",
            "G00 Z1.00000 ( retract after operations )", "G00 Z35.00000 (Retract)", "T2", "M5      (Spindle stop.)",
            "G04 P3.00000", "(MSG, Change tool bit to drill size 0.8mm)", "M6      (Tool change.)",
            "G00 S14000     (RPM spindle speed.)", "M3      (Spindle on clockwise.)",
            "G90        ( Ensure absolute mode before operations )", "G00 Z35.00000 ( safety retract )",
            "G4 P0 ( dwell to ensure Z complete before XY )", "G0 X16.73500 Y50.73000", "G0 Z1.00000",
            "G04 P3.00000", "G1 F200.00000", "G1 Z-1.70000"
        };

        /// <summary>
        /// Catches pcb2gcode's own tool change shape refused at load, or written without its
        /// safety retract or the drill's feed.
        /// </summary>
        [Fact]
        public void APcb2GcodeToolChange_KeepsItsSafetyRetract_AndTheDrillsFeed()
        {
            var lines = GCodeFile.FromList(Pcb2GcodeToolChange).GetGCode();

            int change = lines.IndexOf(ToolChangeLine);
            Assert.Equal("G0 Z35", lines[lines.FindIndex(change, l => l.StartsWith("G0 "))]);
            int drill = lines.FindIndex(change, l => l.StartsWith("G1 "));
            Assert.Contains("F200", lines.GetRange(change, drill - change));
        }
    }
}
