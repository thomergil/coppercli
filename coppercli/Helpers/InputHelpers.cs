// Extracted from Program.cs - Input helper methods

using Spectre.Console;
using static coppercli.CliConstants;

namespace coppercli.Helpers
{
    /// <summary>
    /// Helper methods for user input and keyboard handling.
    /// </summary>
    internal static class InputHelpers
    {
        public const string InvalidNumberMessage = "[red]Invalid number, using default[/]";

        /// <summary>
        /// Reads a key while polling for machine status changes.
        /// Returns the key pressed, or null if status changed (caller should redraw).
        /// </summary>
        public static ConsoleKeyInfo? ReadKeyPolling()
        {
            var lastStatus = AppState.Machine?.Status;
            while (!Console.KeyAvailable)
            {
                Thread.Sleep(StatusPollIntervalMs);
                if (AppState.Machine?.Status != lastStatus)
                {
                    return null; // Status changed, caller should redraw
                }
            }
            return Console.ReadKey(true);
        }

        /// <summary>
        /// Waits for any key while polling for machine status changes.
        /// Returns true if a key was pressed, false if status changed (caller should redraw).
        /// </summary>
        public static bool WaitForKeyPolling()
        {
            return ReadKeyPolling() != null;
        }

        /// <summary>
        /// Flushes any buffered keyboard input to prevent keypresses from bleeding into subsequent prompts.
        /// </summary>
        public static void FlushKeyboard()
        {
            while (Console.KeyAvailable)
            {
                Console.ReadKey(true);
            }
        }

        /// <summary>
        /// Checks if a key press matches a given ConsoleKey or character.
        /// Handles cross-platform compatibility where key.Key or key.KeyChar may work differently.
        /// For non-QWERTY keyboards (e.g., Dvorak), the character check is prioritized since
        /// ConsoleKey values may map to physical key positions rather than logical characters.
        /// </summary>
        public static bool IsKey(ConsoleKeyInfo key, ConsoleKey consoleKey, char c)
        {
            // Check character first (more reliable for non-QWERTY layouts)
            if (char.ToLower(key.KeyChar) == char.ToLower(c))
            {
                return true;
            }
            // Fall back to ConsoleKey check
            return key.Key == consoleKey;
        }

        /// <summary>
        /// Checks if a key press is Escape.
        /// </summary>
        public static bool IsEscapeKey(ConsoleKeyInfo key)
        {
            return key.Key == ConsoleKey.Escape;
        }

        /// <summary>
        /// Checks if a key press is Enter.
        /// </summary>
        public static bool IsEnterKey(ConsoleKeyInfo key)
        {
            return key.Key == ConsoleKey.Enter;
        }

        /// <summary>
        /// Checks if a key press is Backspace.
        /// </summary>
        public static bool IsBackspaceKey(ConsoleKeyInfo key)
        {
            return key.Key == ConsoleKey.Backspace;
        }

        /// <summary>
        /// Checks if a key press is an exit key (Escape or Q).
        /// </summary>
        public static bool IsExitKey(ConsoleKeyInfo key)
        {
            return IsEscapeKey(key) || IsKey(key, ConsoleKey.Q, 'q');
        }

        /// <summary>
        /// Returns the menu key character for a given index, or null if beyond limit.
        /// 0-9 use digits '1'-'9' then '0', indices 10-35 use 'A'-'Z'.
        /// Returns null for indices >= 36.
        /// </summary>
        public static char? GetMenuKey(int index)
        {
            if (index < 9)
            {
                return (char)('1' + index);
            }
            else if (index == 9)
            {
                return '0';
            }
            else if (index < 36)
            {
                return (char)('A' + index - 10);
            }
            else
            {
                return null; // No shortcut for items beyond 36
            }
        }
    }
}
