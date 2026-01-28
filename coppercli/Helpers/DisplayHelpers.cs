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

        // =========================================================================
        // Legacy ANSI names (for existing code - use semantic names in new code)
        // =========================================================================
        public const string AnsiCyan = AnsiCodeCyan;
        public const string AnsiBoldCyan = AnsiCodeBoldCyan;
        public const string AnsiYellow = AnsiCodeYellow;
        public const string AnsiGreen = AnsiCodeGreen;
        public const string AnsiBoldGreen = AnsiCodeBoldGreen;
        public const string AnsiBoldBlue = AnsiCodeBoldBlue;
        public const string AnsiRed = AnsiCodeRed;
        public const string AnsiBoldRed = AnsiCodeBoldRed;

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
                return (80, 24);
            }
        }

        /// <summary>
        /// Writes a line to the console, truncated or padded to exactly maxWidth display characters.
        /// Handles ANSI escape codes correctly (they don't count toward display width).
        /// This enables flicker-free updates when used with Console.SetCursorPosition(0, 0).
        /// </summary>
        public static void WriteLineTruncated(string text, int maxWidth)
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

            Console.WriteLine(text);
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
        /// Total height of an overlay box including margin.
        /// Structure: margin, border, padding, line1, line2, padding, border, margin = 8 lines
        /// </summary>
        public const int OverlayBoxHeight = 8;

        /// <summary>
        /// Gets a single line of an overlay box.
        /// Lines 0 and 7 are margin (empty). Lines 1-6 are the box with padding.
        /// </summary>
        public static string GetOverlayBoxLine(int lineIndex, int boxWidth,
            string line1Text, string line1Color,
            string line2Text, string line2Color)
        {
            // Guard against invalid box width
            if (boxWidth < 4)
            {
                return "";
            }
            string inner = new string(' ', boxWidth - 2);
            return lineIndex switch
            {
                0 => "",  // top margin
                1 => $"╔{new string('═', boxWidth - 2)}╗",
                2 => $"║{inner}║",  // top padding
                3 => $"║{line1Color}{CenterText(line1Text, boxWidth - 2)}{AnsiReset}║",
                4 => $"║{line2Color}{CenterText(line2Text, boxWidth - 2)}{AnsiReset}║",
                5 => $"║{inner}║",  // bottom padding
                6 => $"╚{new string('═', boxWidth - 2)}╝",
                7 => "",  // bottom margin
                _ => ""
            };
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
    }
}
