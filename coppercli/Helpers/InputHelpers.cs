using Spectre.Console;
using static coppercli.CliConstants;
using static coppercli.Core.Util.Constants;

namespace coppercli.Helpers
{
    internal static class InputHelpers
    {
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
        /// Returns null when the machine status or the terminal size changed, which means the
        /// caller redraws instead of acting on a key.
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
                    return null;
                }
                var (width, height) = DisplayHelpers.GetSafeWindowSize();
                if (width != lastWidth || height != lastHeight)
                {
                    return null;
                }
            }
            return Console.ReadKey(true);
        }

        public static bool WaitForKeyPolling()
        {
            return ReadKeyPolling() != null;
        }

        /// <summary>
        /// Call before drawing a prompt that replaced another in the same place: the keystroke
        /// that answered the first would otherwise answer the second, which the operator has
        /// not read.
        /// </summary>
        public static void FlushKeyboard()
        {
            while (Console.KeyAvailable)
            {
                Console.ReadKey(true);
            }
        }

        /// <summary>
        /// On a non-QWERTY layout such as Dvorak, ConsoleKey follows the physical key position
        /// rather than the character printed on it, so KeyChar is compared first.
        /// </summary>
        public static bool IsKey(ConsoleKeyInfo key, ConsoleKey consoleKey)
        {
            if (KeyToChar.TryGetValue(consoleKey, out char c))
            {
                if (char.ToLower(key.KeyChar) == c)
                {
                    return true;
                }
            }
            return key.Key == consoleKey;
        }

        public static bool IsEscapeKey(ConsoleKeyInfo key)
        {
            return key.Key == ConsoleKey.Escape;
        }

        public static bool IsEnterKey(ConsoleKeyInfo key)
        {
            return key.Key == ConsoleKey.Enter;
        }

        public static bool IsExitKey(ConsoleKeyInfo key)
        {
            return IsEscapeKey(key) || IsKey(key, ConsoleKey.Q);
        }

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
