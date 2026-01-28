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
        /// <summary>
        /// Maps ConsoleKey values to their expected character equivalents.
        /// Used for non-QWERTY keyboard compatibility.
        /// </summary>
        private static readonly Dictionary<ConsoleKey, char> KeyToChar = new()
        {
            { ConsoleKey.A, 'a' }, { ConsoleKey.B, 'b' }, { ConsoleKey.C, 'c' },
            { ConsoleKey.D, 'd' }, { ConsoleKey.E, 'e' }, { ConsoleKey.F, 'f' },
            { ConsoleKey.G, 'g' }, { ConsoleKey.H, 'h' }, { ConsoleKey.I, 'i' },
            { ConsoleKey.J, 'j' }, { ConsoleKey.K, 'k' }, { ConsoleKey.L, 'l' },
            { ConsoleKey.M, 'm' }, { ConsoleKey.N, 'n' }, { ConsoleKey.O, 'o' },
            { ConsoleKey.P, 'p' }, { ConsoleKey.Q, 'q' }, { ConsoleKey.R, 'r' },
            { ConsoleKey.S, 's' }, { ConsoleKey.T, 't' }, { ConsoleKey.U, 'u' },
            { ConsoleKey.V, 'v' }, { ConsoleKey.W, 'w' }, { ConsoleKey.X, 'x' },
            { ConsoleKey.Y, 'y' }, { ConsoleKey.Z, 'z' },
            { ConsoleKey.D0, '0' }, { ConsoleKey.D1, '1' }, { ConsoleKey.D2, '2' },
            { ConsoleKey.D3, '3' }, { ConsoleKey.D4, '4' }, { ConsoleKey.D5, '5' },
            { ConsoleKey.D6, '6' }, { ConsoleKey.D7, '7' }, { ConsoleKey.D8, '8' },
            { ConsoleKey.D9, '9' },
        };
        /// <summary>
        /// Reads a key while polling for machine status changes and terminal resize.
        /// Returns the key pressed, or null if status/size changed (caller should redraw).
        /// </summary>
        public static ConsoleKeyInfo? ReadKeyPolling()
        {
            var lastStatus = AppState.Machine?.Status;
            var (lastWidth, lastHeight) = DisplayHelpers.GetSafeWindowSize();
            while (!Console.KeyAvailable)
            {
                Thread.Sleep(StatusPollIntervalMs);
                if (AppState.Machine?.Status != lastStatus)
                {
                    return null; // Status changed, caller should redraw
                }
                var (width, height) = DisplayHelpers.GetSafeWindowSize();
                if (width != lastWidth || height != lastHeight)
                {
                    return null; // Terminal resized, caller should redraw
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
        /// Checks if a key press matches a given ConsoleKey.
        /// Handles cross-platform compatibility where key.Key or key.KeyChar may work differently.
        /// For non-QWERTY keyboards (e.g., Dvorak), the character check is prioritized since
        /// ConsoleKey values may map to physical key positions rather than logical characters.
        /// </summary>
        public static bool IsKey(ConsoleKeyInfo key, ConsoleKey consoleKey)
        {
            // Check character first (more reliable for non-QWERTY layouts)
            if (KeyToChar.TryGetValue(consoleKey, out char c))
            {
                if (char.ToLower(key.KeyChar) == c)
                {
                    return true;
                }
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
            return IsEscapeKey(key) || IsKey(key, ConsoleKey.Q);
        }

        /// <summary>
        /// Returns the menu key character for a given index, or null if beyond limit.
        /// 0-8 use digits '1'-'9', index 9 uses '0', indices 10-35 use 'A'-'Z'.
        /// Returns null for indices >= MaxMenuShortcuts (36).
        /// </summary>
        public static char? GetMenuKey(int index)
        {
            if (index < MenuShortcutZeroIndex)
            {
                return (char)('1' + index);
            }
            else if (index == MenuShortcutZeroIndex)
            {
                return '0';
            }
            else if (index < MaxMenuShortcuts)
            {
                return (char)('A' + index - MenuShortcutAlphaStart);
            }
            else
            {
                return null;
            }
        }
    }
}
