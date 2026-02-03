#nullable enable
using System.Collections.Generic;
using coppercli.Core.GCode;
using coppercli.Core.Util;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// Tests for GCodeParser M6 detection utilities.
    /// </summary>
    public class GCodeParserM6Tests
    {
        // =========================================================================
        // IsM6Line tests
        // =========================================================================

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

        // =========================================================================
        // IsM0Line tests
        // =========================================================================

        [Theory]
        [InlineData("M0", true)]
        [InlineData("M00", true)]
        [InlineData("m0", true)]
        [InlineData("m00", true)]
        [InlineData("M000", true)]
        [InlineData("  M0  ", true)]
        [InlineData("G0 M0", true)]
        [InlineData("M01", false)]  // M01 is optional stop, not pause
        [InlineData("M6", false)]
        [InlineData("G0 X0", false)]
        [InlineData("", false)]
        public void IsM0Line_DetectsM0Correctly(string line, bool expected)
        {
            Assert.Equal(expected, GCodeParser.IsM0Line(line));
        }

        // =========================================================================
        // ExtractToolNumber tests
        // =========================================================================

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

        // =========================================================================
        // ExtractToolName tests
        // =========================================================================

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

        // =========================================================================
        // FindToolInfo tests
        // =========================================================================

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
            // Constants.ToolInfoSearchLines defines how far back we search (10 lines)
            var lines = new List<string>
            {
                "T5 (far away tool)",  // Line 0 - beyond search range
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
                "T7 (nearby tool)",    // Line 11 - within search range of M6
                "M6"                   // Line 12
            };

            var (number, name) = GCodeParser.FindToolInfo(lines, 12);

            Assert.Equal(7, number);
            Assert.Equal("nearby tool", name);
        }

        [Fact]
        public void FindToolInfo_FindsToolNameOnSeparateLine()
        {
            // Tool number on M6 line, tool name on previous line (common pcb2gcode format)
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
            // When lineIndex is out of bounds, the function searches backwards
            // from valid indices, so it can still find tools within range
            var lines = new List<string>
            {
                "G0 X0",
                "G0 X1"  // No tool on this line
            };

            // With index beyond list length, backwards search still works.
            // The search range is lineIndex - ToolInfoSearchLines to lineIndex - 1.
            // Valid indices 0 and 1 are in range but have no tool.
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
    }
}
