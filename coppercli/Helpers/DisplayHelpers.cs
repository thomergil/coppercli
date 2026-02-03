using System.Text;
using static coppercli.CliConstants;

namespace coppercli.Helpers
{
    /// <summary>
    /// Helper functions for console display, especially for flicker-free full-screen modes.
    /// </summary>
    /// <remarks>
    /// WHY TWO COLOR SYSTEMS?
    ///
    /// This codebase uses two color systems:
    ///
    /// 1. Spectre.Console markup ([{ColorError}], [{ColorSuccess}], etc.)
    ///    - Used for: Menus, dialogs, static output
    ///    - Rendered by Spectre's pipeline with rich formatting
    ///
    /// 2. Raw ANSI escape codes (AnsiError, AnsiSuccess, etc.)
    ///    - Used for: Live displays (JogMenu, MillMenu, MacroRunner, SetupToolSetter)
    ///    - Required for flicker-free updates using Console.SetCursorPosition(0, 0)
    ///    - WriteLineTruncated() pads lines to full width, overwriting old content
    ///    - Spectre.Console's rendering doesn't support this precise cursor control
    ///
    /// Both systems use the same semantic color names (Error=red, Success=green, etc.)
    /// defined in CliConstants. The ANSI codes below map to the same meanings.
    /// </remarks>
    internal static class DisplayHelpers
    {
        // =========================================================================
        // Raw ANSI escape codes
        // =========================================================================
        public const string AnsiReset = "\u001b[0m";
        private const string AnsiCodeCyan = "\u001b[36m";
        private const string AnsiCodeBoldCyan = "\u001b[1;36m";
        private const string AnsiCodeYellow = "\u001b[93m";
        private const string AnsiCodeGreen = "\u001b[32m";
        private const string AnsiCodeBoldGreen = "\u001b[1;32m";
        private const string AnsiCodeBlue = "\u001b[34m";
        private const string AnsiCodeBoldBlue = "\u001b[1;34m";
        private const string AnsiCodeRed = "\u001b[31m";
        private const string AnsiCodeBoldRed = "\u001b[1;31m";
        private const string AnsiCodeDim = "\u001b[2m";

        // =========================================================================
        // Semantic ANSI colors (match CliConstants color theme)
        // =========================================================================

        /// <summary>ANSI code for errors (red). Matches ColorError.</summary>
        public const string AnsiError = AnsiCodeRed;

        /// <summary>ANSI code for success/confirmation (green). Matches ColorSuccess.</summary>
        public const string AnsiSuccess = AnsiCodeGreen;

        /// <summary>ANSI code for warnings/values (yellow). Matches ColorWarning.</summary>
        public const string AnsiWarning = AnsiCodeYellow;

        /// <summary>ANSI code for prompts/headers (blue). Matches ColorPrompt.</summary>
        public const string AnsiPrompt = AnsiCodeBoldBlue;

        /// <summary>ANSI code for info/labels (cyan). Matches ColorInfo.</summary>
        public const string AnsiInfo = AnsiCodeCyan;

        /// <summary>ANSI code for secondary/disabled text. Matches ColorDim.</summary>
        public const string AnsiDim = AnsiCodeDim;

        /// <summary>ANSI escape sequence to clear from cursor to end of line.</summary>
        public const string AnsiClearToEol = "\u001b[K";

        // =========================================================================
        // Bold variants for emphasis (highlighting important values/states)
        // =========================================================================

        /// <summary>ANSI code for emphasized success values (bold green).</summary>
        public const string AnsiSuccessBold = AnsiCodeBoldGreen;

        /// <summary>ANSI code for critical errors/alerts (bold red).</summary>
        public const string AnsiCritical = AnsiCodeBoldRed;

        /// <summary>
        /// Gets the console window size safely, returning defaults if unavailable.
        /// </summary>
        public static (int Width, int Height) GetSafeWindowSize()
        {
            try
            {
                return (Console.WindowWidth, Console.WindowHeight);
            }
            catch
            {
                return (CliConstants.FallbackTerminalWidth, CliConstants.FallbackTerminalHeight);
            }
        }

        /// <summary>
        /// Writes a line to the console, truncated or padded to exactly maxWidth display characters.
        /// Handles ANSI escape codes correctly (they don't count toward display width).
        /// This enables flicker-free updates when used with Console.SetCursorPosition(0, 0).
        /// </summary>
        /// <param name="addNewline">If false, omits the trailing newline. Use for the last line
        /// of a full-screen layout to prevent scrolling when terminal height equals content height.</param>
        public static void WriteLineTruncated(string text, int maxWidth, bool addNewline = true)
        {
            // Calculate display length (excluding ANSI codes)
            int displayLen = CalculateDisplayLength(text);

            if (displayLen > maxWidth)
            {
                // Truncate to maxWidth display characters
                text = TruncateToDisplayWidth(text, maxWidth);
            }
            else if (displayLen < maxWidth)
            {
                // Pad to full width to overwrite old content
                text = text + new string(' ', maxWidth - displayLen);
            }

            if (addNewline)
            {
                Console.WriteLine(text);
            }
            else
            {
                Console.Write(text);
            }
        }

        /// <summary>
        /// Calculates the display length of a string, excluding ANSI escape codes.
        /// </summary>
        public static int CalculateDisplayLength(string text)
        {
            int displayLen = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\u001b')
                {
                    // Skip ANSI sequence
                    while (i < text.Length && text[i] != 'm')
                    {
                        i++;
                    }
                }
                else
                {
                    displayLen++;
                }
            }
            return displayLen;
        }

        /// <summary>
        /// Truncates a string to a maximum display width, preserving ANSI codes.
        /// </summary>
        public static string TruncateToDisplayWidth(string text, int maxWidth)
        {
            var result = new StringBuilder();
            int displayed = 0;

            for (int i = 0; i < text.Length && displayed < maxWidth; i++)
            {
                if (text[i] == '\u001b')
                {
                    // Copy entire ANSI sequence
                    while (i < text.Length && text[i] != 'm')
                    {
                        result.Append(text[i]);
                        i++;
                    }
                    if (i < text.Length)
                    {
                        result.Append(text[i]); // 'm'
                    }
                }
                else
                {
                    result.Append(text[i]);
                    displayed++;
                }
            }

            return result.ToString();
        }

        /// <summary>
        /// Formats a TimeSpan as HH:MM:SS.
        /// </summary>
        public static string FormatTimeSpan(TimeSpan ts)
        {
            return ts.ToString(@"hh\:mm\:ss");
        }

        /// <summary>
        /// Formats a duration in a human-readable short form (e.g., "1h 23m 45s").
        /// </summary>
        public static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalHours >= 1)
            {
                return $"{(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s";
            }
            if (duration.TotalMinutes >= 1)
            {
                return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
            }
            return $"{duration.Seconds}s";
        }

        // =========================================================================
        // Overlay box helpers (shared by MacroRunner and MillMenu)
        // =========================================================================

        /// <summary>
        /// Fixed lines in overlay box: 2 margins + 2 borders + 2 padding = 6.
        /// Total height = OverlayBoxFixedLines + number of content lines.
        /// </summary>
        public const int OverlayBoxFixedLines = 6;

        /// <summary>
        /// Total height for legacy 2-line overlay (for backward compatibility).
        /// </summary>
        public const int OverlayBoxHeight = OverlayBoxFixedLines + 2;

        /// <summary>Padding added to content width for overlay box (border + inner padding on each side).</summary>
        public const int OverlayBoxPadding = 6;

        /// <summary>Minimum width for overlay box for aesthetics.</summary>
        public const int OverlayBoxMinWidth = 20;

        /// <summary>Margin from grid edge for overlay box.</summary>
        public const int OverlayBoxMargin = 4;

        /// <summary>
        /// Calculates overlay box height based on content lines.
        /// </summary>
        public static int CalculateOverlayBoxHeight(string[] contentLines)
        {
            return OverlayBoxFixedLines + contentLines.Length;
        }

        /// <summary>
        /// Calculates overlay box width based on content, respecting min/max constraints.
        /// </summary>
        public static int CalculateOverlayBoxWidth(string[] contentLines, int maxWidth)
        {
            int contentWidth = contentLines.Max(l => l.Length);
            int boxWidth = Math.Min(contentWidth + OverlayBoxPadding, maxWidth - OverlayBoxMargin);
            return Math.Max(boxWidth, OverlayBoxMinWidth);
        }

        /// <summary>
        /// Calculates overlay box width for a 2-line overlay (convenience overload).
        /// </summary>
        public static int CalculateOverlayBoxWidth(string line1, string line2, int maxWidth)
        {
            return CalculateOverlayBoxWidth(new[] { line1, line2 }, maxWidth);
        }

        /// <summary>
        /// Gets a single line of a dynamic-height overlay box.
        /// Structure: margin, border, padding, [content lines], padding, border, margin.
        /// </summary>
        public static string GetOverlayBoxLine(int lineIndex, int boxWidth,
            string[] contentLines, string[] contentColors)
        {
            if (boxWidth < 4)
            {
                return "";
            }

            int totalHeight = CalculateOverlayBoxHeight(contentLines);
            string inner = new string(' ', boxWidth - 2);

            // Line indices: 0=margin, 1=border, 2=padding, 3..3+N-1=content, 3+N=padding, 3+N+1=border, 3+N+2=margin
            int contentStart = 3;
            int contentEnd = contentStart + contentLines.Length - 1;
            int bottomPadding = contentEnd + 1;
            int bottomBorder = bottomPadding + 1;
            int bottomMargin = bottomBorder + 1;

            if (lineIndex == 0 || lineIndex == bottomMargin)
            {
                return "";  // margins
            }
            if (lineIndex == 1)
            {
                return $"╔{new string('═', boxWidth - 2)}╗";
            }
            if (lineIndex == 2 || lineIndex == bottomPadding)
            {
                return $"║{inner}║";  // padding
            }
            if (lineIndex == bottomBorder)
            {
                return $"╚{new string('═', boxWidth - 2)}╝";
            }
            if (lineIndex >= contentStart && lineIndex <= contentEnd)
            {
                int contentIdx = lineIndex - contentStart;
                string color = contentIdx < contentColors.Length ? contentColors[contentIdx] : "";
                return $"║{color}{CenterText(contentLines[contentIdx], boxWidth - 2)}{AnsiReset}║";
            }
            return "";
        }

        /// <summary>
        /// Gets a single line of a 2-line overlay box (convenience overload).
        /// Used by MillMenu and MacroRunner for message+subtext overlays.
        /// </summary>
        public static string GetOverlayBoxLine(int lineIndex, int boxWidth,
            string line1Text, string line1Color,
            string line2Text, string line2Color)
        {
            return GetOverlayBoxLine(lineIndex, boxWidth,
                new[] { line1Text, line2Text },
                new[] { line1Color, line2Color });
        }

        /// <summary>
        /// Centers text within a given width.
        /// </summary>
        public static string CenterText(string text, int width)
        {
            if (text.Length >= width)
            {
                return text.Substring(0, width);
            }
            int pad = (width - text.Length) / 2;
            return text.PadLeft(pad + text.Length).PadRight(width);
        }

        /// <summary>
        /// Composites an overlay box on top of a background line.
        /// The background shows through on either side of the box, with a 1-char margin.
        /// Returns the background unchanged if overlay is empty (margin line).
        /// </summary>
        public static string CompositeOverlay(string background, string overlay, int overlayStart, int totalWidth)
        {
            // Margin lines are empty - return background
            if (string.IsNullOrEmpty(overlay))
            {
                return background;
            }

            const int margin = 1;

            // Truncate background to fit
            string bgTruncated = TruncateToDisplayWidth(background, totalWidth);

            // Build result: background up to margin before overlay, margin, overlay, margin
            var result = new StringBuilder();

            // Get background portion before the margin
            int marginStart = Math.Max(0, overlayStart - margin);
            string bgBefore = TruncateToDisplayWidth(bgTruncated, marginStart);
            result.Append(bgBefore);

            // Pad if background is shorter than margin start
            int bgBeforeLen = CalculateDisplayLength(bgBefore);
            if (bgBeforeLen < marginStart)
            {
                result.Append(new string(' ', marginStart - bgBeforeLen));
            }

            // Reset colors and add left margin
            result.Append(AnsiReset);
            result.Append(' '); // left margin

            // Add overlay
            result.Append(overlay);

            // Add right margin
            result.Append(' ');

            result.Append(AnsiReset);

            return result.ToString();
        }

        /// <summary>
        /// Draws a centered overlay box. Used internally by ShowOverlayTimed and ShowOverlayAndWait.
        /// Handles newlines in message/subtext to create multi-line overlays.
        /// </summary>
        private static void DrawCenteredOverlay(string message, string subtext, string messageColor)
        {
            var (winWidth, winHeight) = GetSafeWindowSize();

            // Build content lines from message and subtext, handling embedded newlines
            var lines = new List<string>();
            var colors = new List<string>();

            foreach (var line in message.Split('\n'))
            {
                lines.Add(line);
                colors.Add(messageColor);
            }
            if (!string.IsNullOrEmpty(subtext))
            {
                foreach (var line in subtext.Split('\n'))
                {
                    lines.Add(line);
                    colors.Add(AnsiDim);
                }
            }

            var contentLines = lines.ToArray();
            var contentColors = colors.ToArray();

            int boxHeight = CalculateOverlayBoxHeight(contentLines);
            int boxWidth = CalculateOverlayBoxWidth(contentLines, winWidth);
            int boxLeft = (winWidth - boxWidth) / 2;
            int boxTop = (winHeight - boxHeight) / 2;

            for (int i = 0; i < boxHeight; i++)
            {
                Console.SetCursorPosition(boxLeft, boxTop + i);
                Console.Write(GetOverlayBoxLine(i, boxWidth, contentLines, contentColors));
            }
        }

        /// <summary>
        /// Draws a centered overlay box for a specified duration.
        /// Use this for confirmations that auto-dismiss.
        /// </summary>
        /// <param name="message">Main message to display.</param>
        /// <param name="durationMs">How long to show the overlay.</param>
        /// <param name="subtext">Secondary text (optional).</param>
        /// <param name="messageColor">ANSI color for main message.</param>
        public static void ShowOverlayTimed(string message, int durationMs, string? subtext = null, string? messageColor = null)
        {
            DrawCenteredOverlay(message, subtext ?? "", messageColor ?? AnsiSuccess);
            Thread.Sleep(durationMs);
        }

        /// <summary>
        /// Draws a centered overlay box and waits for Enter key.
        /// Use this for alerts/errors in full-screen TUI modes.
        /// </summary>
        /// <param name="message">Main message to display.</param>
        /// <param name="subtext">Secondary text (e.g., "Press Enter to continue").</param>
        /// <param name="messageColor">ANSI color for main message.</param>
        public static void ShowOverlayAndWait(string message, string? subtext = null, string? messageColor = null)
        {
            DrawCenteredOverlay(message, subtext ?? "Press Enter", messageColor ?? AnsiError);

            while (true)
            {
                var key = Console.ReadKey(true);
                if (InputHelpers.IsEnterKey(key) || InputHelpers.IsEscapeKey(key))
                {
                    return;
                }
            }
        }
    }
}
