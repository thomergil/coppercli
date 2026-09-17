using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using coppercli.Helpers;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// An overlay box is as wide as its widest content line, capped at the terminal width, and
    /// GetOverlayBoxLine cuts a line that still does not fit. Content has to be wrapped before
    /// it is measured, or the operator reads half a sentence about the enclosure.
    /// </summary>
    public class OverlayWrapTests
    {
        private const int Width = 40;

        private const string LongMessage =
            "The enclosure door is open. If a magnet is on the switch, take it off. "
            + "Close the door. Ready to carry on?";

        // The production hint, so the test fails if it grows past what a narrow box holds.
        private const string KeyHint = coppercli.CliConstants.ContinueOrCancelKeyHint;

        private static readonly string AnsiCodePattern = AnsiEscape + @"\[[0-9;]*m";

        private const char AnsiEscape = (char)27;

        [Fact]
        public void ALongPrompt_KeepsEveryWordInsideTheBox()
        {
            string drawn = DrawBox(LongMessage, KeyHint);

            foreach (string word in Words(LongMessage + " " + KeyHint))
            {
                Assert.Contains(word, drawn);
            }
        }

        [Fact]
        public void TheKeyHint_IsDrawnBelowTheMessageAndDimmed()
        {
            var (lines, colors) = DisplayHelpers.BuildOverlayContent(
                LongMessage, KeyHint, DisplayHelpers.AnsiWarning, Width);

            Assert.Equal(KeyHint, lines.Last());
            Assert.Equal(DisplayHelpers.AnsiDim, colors.Last());
            Assert.Equal(DisplayHelpers.AnsiWarning, colors.First());
        }

        [Fact]
        public void AnOverlayWithNoKeyHint_HasNoBlankLineUnderTheMessage()
        {
            var (lines, _) = DisplayHelpers.BuildOverlayContent(
                "Stopping the run...", null, DisplayHelpers.AnsiWarning, Width);

            Assert.Equal(new[] { "Stopping the run..." }, lines);
        }

        [Fact]
        public void ALineLongerThanTheBox_IsBrokenUpRatherThanCutOff()
        {
            var lines = DisplayHelpers.WrapToWidth(LongMessage, Width);

            Assert.True(lines.Count > 1, "a message twice the box width stayed on one line");
            Assert.All(lines, line => Assert.True(line.Length <= Width,
                $"'{line}' is {line.Length} characters, wider than the {Width}-wide box"));
            Assert.Equal(Words(LongMessage), lines.SelectMany(l => Words(l)));
        }

        [Fact]
        public void TheLineBreaksAlreadyInTheText_AreKept()
        {
            var lines = DisplayHelpers.WrapToWidth("Enclosure Door\n\nClose it.", Width);

            Assert.Equal(new[] { "Enclosure Door", "", "Close it." }, lines);
        }

        [Fact]
        public void AWordWiderThanTheBox_IsLeftWhole()
        {
            var lines = DisplayHelpers.WrapToWidth(new string('x', Width + 10), Width);

            Assert.Single(lines);
        }

        /// <summary>The box as a screen of Width columns draws it, with the color codes stripped.</summary>
        private static string DrawBox(string message, string subtext)
        {
            var (lines, colors) = DisplayHelpers.BuildOverlayContent(
                message, subtext, DisplayHelpers.AnsiWarning, Width);

            int boxWidth = DisplayHelpers.CalculateOverlayBoxWidth(lines, Width);
            var drawn = new StringBuilder();
            for (int i = 0; i < DisplayHelpers.CalculateOverlayBoxHeight(lines); i++)
            {
                drawn.AppendLine(DisplayHelpers.GetOverlayBoxLine(i, boxWidth, lines, colors));
            }

            return Regex.Replace(drawn.ToString(), AnsiCodePattern, string.Empty);
        }

        private static string[] Words(string text) =>
            text.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries);
    }
}
