using System.Text;

namespace coppercli.Helpers
{
    /// <summary>
    /// Helper functions for console display, especially for flicker-free full-screen modes.
    /// </summary>
    internal static class DisplayHelpers
    {
        // =========================================================================
        // ANSI escape codes for colored output
        // Using raw ANSI instead of Spectre.Console for flicker-free updates
        // =========================================================================
        public const string AnsiReset = "\u001b[0m";
        public const string AnsiCyan = "\u001b[36m";
        public const string AnsiBoldCyan = "\u001b[1;36m";
        public const string AnsiYellow = "\u001b[93m";
        public const string AnsiGreen = "\u001b[32m";
        public const string AnsiBoldGreen = "\u001b[1;32m";
        public const string AnsiBoldBlue = "\u001b[1;34m";
        public const string AnsiRed = "\u001b[31m";
        public const string AnsiBoldRed = "\u001b[1;31m";
        public const string AnsiDim = "\u001b[2m";

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
