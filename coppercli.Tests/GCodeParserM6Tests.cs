#nullable enable
using System.Collections.Generic;
using coppercli.Core.GCode;
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
    }
}
