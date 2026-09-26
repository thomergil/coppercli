using System.Collections.Generic;
using System.Text;
using coppercli.Core.Controllers;
using coppercli.Core.Util;
using static coppercli.CliConstants;

namespace coppercli.Helpers
{
    /// <remarks>
    /// Two color systems run side by side. Menus, dialogs and static output use Spectre.Console
    /// markup such as ColorError and ColorSuccess; the live screens - JogMenu, MillMenu,
    /// MacroRunner, SetupToolSetter - use the raw ANSI codes below, because redrawing from
    /// Console.SetCursorPosition(0, 0) needs WriteLineTruncated to pad each line over the last
    /// frame, and Spectre.Console's rendering does not offer that cursor control. The two sets
    /// name the same colors: AnsiError is the red of ColorError, and so on down the list.
    /// </remarks>
    internal static class DisplayHelpers
    {
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

        public const string AnsiError = AnsiCodeRed;

        public const string AnsiSuccess = AnsiCodeGreen;

        public const string AnsiWarning = AnsiCodeYellow;

        public const string AnsiPrompt = AnsiCodeBoldBlue;

        public const string AnsiInfo = AnsiCodeCyan;

        public const string AnsiDim = AnsiCodeDim;

        public const string AnsiClearToEol = "\u001b[K";

        public const string AnsiSuccessBold = AnsiCodeBoldGreen;

        public const string AnsiAlert = AnsiCodeBoldRed;

        /// <summary>
        /// The words every terminal screen shows for what the machine is doing; GRBL's own
        /// status word is shown where there is no phrase for the activity.
        /// </summary>
        public static string GetActivityText(MachineActivity activity, string rawStatus) => activity switch
        {
            MachineActivity.Disconnected => GrblProtocol.StatusDisconnected,
            MachineActivity.Alarm => MillAlarmStatus,
            MachineActivity.DoorOpen => DoorOpenMessage,
            MachineActivity.DoorRetracting => DoorRetractingStatus,
            MachineActivity.DoorHolding => DoorClosedMessage,
            MachineActivity.DoorResuming => DoorResumingStatus,
            MachineActivity.Sleep => MillSleepStatus,
            _ => rawStatus
        };

        /// <summary>
        /// Console.WindowWidth throws when output is redirected or no console is attached, so
        /// the fallback size stands in.
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
        /// Pads or truncates to exactly maxWidth display characters, counting ANSI escape codes
        /// as zero width. The padding overwrites what the last frame left on that line, which is
        /// what makes a redraw from Console.SetCursorPosition(0, 0) flicker-free.
        /// </summary>
        /// <param name="addNewline">False on the last line of a full-screen layout, where the
        /// newline scrolls the screen once the content is as tall as the terminal.</param>
        public static void WriteLineTruncated(string text, int maxWidth, bool addNewline = true)
        {
            int displayLen = CalculateDisplayLength(text);

            if (displayLen > maxWidth)
            {
                text = TruncateToDisplayWidth(text, maxWidth);
            }
            else if (displayLen < maxWidth)
            {
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

        public static int CalculateDisplayLength(string text)
        {
            int displayLen = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\u001b')
                {
                    // An ANSI color sequence runs to a terminating 'm' and prints nothing.
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

        public static string TruncateToDisplayWidth(string text, int maxWidth)
        {
            var result = new StringBuilder();
            int displayed = 0;

            for (int i = 0; i < text.Length && displayed < maxWidth; i++)
            {
                if (text[i] == '\u001b')
                {
                    while (i < text.Length && text[i] != 'm')
                    {
                        result.Append(text[i]);
                        i++;
                    }
                    if (i < text.Length)
                    {
                        result.Append(text[i]);
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

        public static string FormatTimeSpan(TimeSpan ts)
        {
            return ts.ToString(@"hh\:mm\:ss");
        }

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

        /// <summary>Two margins, two borders and two padding rows, none of them content.</summary>
        public const int OverlayBoxFixedLines = 6;

        /// <summary>Border and inner padding on each side of the content.</summary>
        public const int OverlayBoxPadding = 6;

        public const int OverlayBoxMinWidth = 20;

        public const int OverlayBoxMargin = 4;

        public static int CalculateOverlayBoxHeight(string[] contentLines)
        {
            return OverlayBoxFixedLines + contentLines.Length;
        }

        public static int CalculateOverlayBoxWidth(string[] contentLines, int maxWidth)
        {
            int contentWidth = contentLines.Max(l => l.Length);
            int boxWidth = Math.Min(contentWidth + OverlayBoxPadding, maxWidth - OverlayBoxMargin);
            return Math.Max(boxWidth, OverlayBoxMinWidth);
        }

        /// <summary>
        /// The message lines come first in <paramref name="messageColor"/>, then the subtext in
        /// AnsiDim. Both are wrapped to what a box of <paramref name="maxWidth"/> fits, because
        /// GetOverlayBoxLine cuts a content line wider than the box.
        /// </summary>
        public static (string[] Lines, string[] Colors) BuildOverlayContent(
            string message, string? subtext, string messageColor, int maxWidth)
        {
            int textWidth = maxWidth - OverlayBoxMargin - OverlayBoxPadding;

            var lines = new List<string>();
            var colors = new List<string>();

            foreach (string line in WrapToWidth(message, textWidth))
            {
                lines.Add(line);
                colors.Add(messageColor);
            }

            if (!string.IsNullOrEmpty(subtext))
            {
                foreach (string line in WrapToWidth(subtext, textWidth))
                {
                    lines.Add(line);
                    colors.Add(AnsiDim);
                }
            }

            return (lines.ToArray(), colors.ToArray());
        }

        /// <summary>
        /// The line order is margin, border, padding, the content lines, padding, border,
        /// margin.
        /// </summary>
        public static string GetOverlayBoxLine(int lineIndex, int boxWidth,
            string[] contentLines, string[] contentColors)
        {
            if (boxWidth < 4)
            {
                return "";
            }

            string inner = new string(' ', boxWidth - 2);

            int contentStart = 3;
            int contentEnd = contentStart + contentLines.Length - 1;
            int bottomPadding = contentEnd + 1;
            int bottomBorder = bottomPadding + 1;
            int bottomMargin = bottomBorder + 1;

            if (lineIndex == 0 || lineIndex == bottomMargin)
            {
                return "";
            }
            if (lineIndex == 1)
            {
                return $"╔{new string('═', boxWidth - 2)}╗";
            }
            if (lineIndex == 2 || lineIndex == bottomPadding)
            {
                return $"║{inner}║";
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
        /// The background shows through on either side of the box, with a one-character margin.
        /// An empty overlay line is one of the box's margin rows, and the background comes back
        /// unchanged.
        /// </summary>
        public static string CompositeOverlay(string background, string overlay, int overlayStart, int totalWidth)
        {
            if (string.IsNullOrEmpty(overlay))
            {
                return background;
            }

            const int margin = 1;

            string bgTruncated = TruncateToDisplayWidth(background, totalWidth);

            var result = new StringBuilder();

            int marginStart = Math.Max(0, overlayStart - margin);
            string bgBefore = TruncateToDisplayWidth(bgTruncated, marginStart);
            result.Append(bgBefore);

            int bgBeforeLen = CalculateDisplayLength(bgBefore);
            if (bgBeforeLen < marginStart)
            {
                result.Append(new string(' ', marginStart - bgBeforeLen));
            }

            // Reset first, or the background line's color runs on into the box.
            result.Append(AnsiReset);
            result.Append(' ');

            result.Append(overlay);

            result.Append(' ');

            result.Append(AnsiReset);

            return result.ToString();
        }

        /// <summary>
        /// Splits text into lines that fit the given width, breaking at spaces and keeping
        /// the line breaks already in the text. A word longer than the width is left whole.
        /// </summary>
        public static List<string> WrapToWidth(string text, int width)
        {
            var wrapped = new List<string>();
            if (width < 1)
            {
                width = 1;
            }

            foreach (string paragraph in text.Split('\n'))
            {
                if (paragraph.Length <= width)
                {
                    wrapped.Add(paragraph);
                    continue;
                }

                var line = new StringBuilder();
                foreach (string word in paragraph.Split(' '))
                {
                    if (line.Length > 0 && line.Length + 1 + word.Length > width)
                    {
                        wrapped.Add(line.ToString());
                        line.Clear();
                    }
                    if (line.Length > 0)
                    {
                        line.Append(' ');
                    }
                    line.Append(word);
                }
                wrapped.Add(line.ToString());
            }

            return wrapped;
        }

        /// <summary>
        /// A newline in the message or the subtext makes a multi-line overlay.
        /// </summary>
        private static void DrawCenteredOverlay(string message, string subtext, string messageColor)
        {
            var (winWidth, winHeight) = GetSafeWindowSize();

            var (contentLines, contentColors) =
                BuildOverlayContent(message, subtext, messageColor, winWidth);

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
        /// For a caller that does its own waiting: the box stays up until something else
        /// redraws the screen.
        /// </summary>
        public static void ShowOverlay(string message, string? subtext = null, string? messageColor = null)
        {
            DrawCenteredOverlay(message, subtext ?? "", messageColor ?? AnsiSuccess);
        }

        /// <summary>
        /// For a confirmation that clears itself; the calling thread blocks for durationMs.
        /// </summary>
        public static void ShowOverlayTimed(string message, int durationMs, string? subtext = null, string? messageColor = null)
        {
            DrawCenteredOverlay(message, subtext ?? "", messageColor ?? AnsiSuccess);
            Thread.Sleep(durationMs);
        }

        /// <summary>
        /// Shows an alert until the operator acknowledges it.
        /// </summary>
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

        /// <param name="defaultYes">What Enter returns, and which letter the hint capitalizes.</param>
        /// <returns>true for Yes, false for No, null when the operator pressed Escape or Q.</returns>
        public static bool? ShowOverlayConfirm(string message, bool defaultYes = false, string? messageColor = null)
        {
            string hint = defaultYes ? "[Y/n]" : "[y/N]";
            DrawCenteredOverlay(message, hint, messageColor ?? AnsiPrompt);

            while (true)
            {
                var key = Console.ReadKey(true);
                if (InputHelpers.IsEnterKey(key))
                {
                    return defaultYes;
                }
                if (InputHelpers.IsKey(key, ConsoleKey.Y))
                {
                    return true;
                }
                if (InputHelpers.IsKey(key, ConsoleKey.N))
                {
                    return false;
                }
                if (InputHelpers.IsExitKey(key))
                {
                    return null;
                }
            }
        }
    }
}
