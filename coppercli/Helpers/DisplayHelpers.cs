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
    }
}
