// Extracted from Program.cs - Menu display helpers

using Spectre.Console;
using System.Text.RegularExpressions;
using static coppercli.CliConstants;

namespace coppercli.Helpers
{
    /// <summary>
    /// A menu item with a label, mnemonic key, option type, and optional enabled condition.
    /// </summary>
    public record MenuItem<T>(string Label, char Mnemonic, T Option, int Data = 0, Func<bool>? EnabledWhen = null)
    {
        /// <summary>
        /// Returns true if this item is currently enabled (selectable).
        /// </summary>
        public bool IsEnabled => EnabledWhen?.Invoke() ?? true;
    }

    /// <summary>
    /// A menu definition that builds labels and provides lookup by option type.
    /// </summary>
    public class MenuDef<T> where T : notnull
    {
        private readonly List<MenuItem<T>> _items = new();

        public MenuDef(params MenuItem<T>[] items)
        {
            _items.AddRange(items);
        }

        public void Add(MenuItem<T> item) => _items.Add(item);

        public int Count => _items.Count;

        /// <summary>
        /// Gets labels for all items. Disabled items are not visually marked here;
        /// use GetEnabledStates() in conjunction for rendering.
        /// </summary>
        public string[] Labels => _items.Select((item, i) => $"{i + 1}. {item.Label} ({item.Mnemonic})").ToArray();

        /// <summary>
        /// Gets the enabled state for each item at the current moment.
        /// </summary>
        public bool[] GetEnabledStates() => _items.Select(item => item.IsEnabled).ToArray();

        public int IndexOf(T option) => _items.FindIndex(item => EqualityComparer<T>.Default.Equals(item.Option, option));

        public MenuItem<T> this[int index] => _items[index];
    }

    /// <summary>
    /// Helper methods for displaying menus.
    /// </summary>
    internal static class MenuHelpers
    {
        /// <summary>
        /// Checks if the machine is connected. Shows error and waits for keypress if not.
        /// </summary>
        /// <returns>True if connected, false otherwise.</returns>
        public static bool RequireConnection()
        {
            if (!AppState.Machine.Connected)
            {
                ShowError("Not connected!");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Displays an error message and waits for Enter.
        /// </summary>
        /// <param name="message">The error message to display (without markup).</param>
        public static void ShowError(string message)
        {
            AnsiConsole.MarkupLine($"[{ColorError}]{Markup.Escape(message)}[/]");
            WaitEnter();
        }

        /// <summary>
        /// Prompts the user for input.
        /// </summary>
        public static T Ask<T>(string prompt, T defaultValue)
        {
            return AnsiConsole.Ask<T>(prompt, defaultValue);
        }

        /// <summary>
        /// Prompts the user for input (no default value).
        /// </summary>
        public static T Ask<T>(string prompt)
        {
            return AnsiConsole.Ask<T>(prompt);
        }

        // Lines used by menu chrome (title + help text + scroll indicators)
        private const int MenuChromeLines = 4; // title, help, possible top/bottom indicators

        // ANSI escape sequence to clear from cursor to end of line
        private const string AnsiClearToEol = "\u001b[K";

        /// <summary>
        /// Writes a markup line and clears to end of line (prevents ghost text when redrawing).
        /// </summary>
        private static void MarkupLineClear(string markup)
        {
            AnsiConsole.Markup(markup);
            Console.WriteLine(AnsiClearToEol);
        }

        /// <summary>
        /// Displays a menu and returns the selected index. Supports arrow navigation, number keys, and mnemonic keys.
        /// Options format: "1. Label (x)" where x is the mnemonic key.
        /// Automatically scrolls when there are more options than fit in the terminal.
        /// </summary>
        /// <param name="enabledStates">Optional array indicating which items are enabled. Disabled items shown dim and not selectable.</param>
        public static int ShowMenu(string title, string[] options, int initialSelection = 0, bool[]? enabledStates = null)
        {
            int selected = Math.Clamp(initialSelection, 0, options.Length - 1);

            // If initial selection is disabled, find first enabled item
            if (enabledStates != null && !enabledStates[selected])
            {
                selected = FindNextEnabled(selected, 1, options.Length, enabledStates);
            }

            // Extract mnemonic keys from options (look for (x) at end)
            // Also extract leading number/letter keys (e.g., "0. Back" or "A. Option")
            var mnemonics = new Dictionary<char, int>();
            var leadingKeys = new Dictionary<char, int>();
            for (int i = 0; i < options.Length; i++)
            {
                // Check for mnemonic in parentheses at end
                var match = Regex.Match(options[i], @"\((\w)\)$");
                if (match.Success)
                {
                    mnemonics[char.ToLower(match.Groups[1].Value[0])] = i;
                }

                // Check for leading number/letter (e.g., "0. " or "A. " or "10. ")
                var leadingMatch = Regex.Match(options[i], @"^(\w+)\.");
                if (leadingMatch.Success)
                {
                    // For single char, use as key; for multi-char numbers, use last digit
                    string prefix = leadingMatch.Groups[1].Value;
                    if (prefix.Length == 1)
                    {
                        leadingKeys[char.ToUpper(prefix[0])] = i;
                    }
                }
            }

            // Right-align numeric prefixes for cleaner display
            // Find max prefix width (e.g., "10." is wider than "1.")
            int maxPrefixWidth = 0;
            foreach (var opt in options)
            {
                var prefixMatch = Regex.Match(opt, @"^(\d+)\.");
                if (prefixMatch.Success)
                {
                    maxPrefixWidth = Math.Max(maxPrefixWidth, prefixMatch.Groups[1].Value.Length);
                }
            }

            // Create display versions with right-aligned numbers
            var displayOptions = new string[options.Length];
            for (int i = 0; i < options.Length; i++)
            {
                var prefixMatch = Regex.Match(options[i], @"^(\d+)\.(.*)$");
                if (prefixMatch.Success && maxPrefixWidth > 1)
                {
                    string num = prefixMatch.Groups[1].Value;
                    string rest = prefixMatch.Groups[2].Value;
                    displayOptions[i] = num.PadLeft(maxPrefixWidth) + "." + rest;
                }
                else
                {
                    displayOptions[i] = options[i];
                }
            }

            // Calculate viewport size based on terminal height
            var (_, termHeight) = DisplayHelpers.GetSafeWindowSize();
            int maxVisibleItems = Math.Max(3, termHeight - MenuChromeLines - Console.CursorTop);
            bool needsScrolling = options.Length > maxVisibleItems;
            int viewStart = 0;

            // Adjust view to show initial selection
            if (needsScrolling && selected >= maxVisibleItems)
            {
                viewStart = Math.Min(selected - maxVisibleItems + 1, options.Length - maxVisibleItems);
            }

            while (true)
            {
                // Recalculate in case terminal was resized
                (_, termHeight) = DisplayHelpers.GetSafeWindowSize();
                maxVisibleItems = Math.Max(3, termHeight - MenuChromeLines - Console.CursorTop);
                needsScrolling = options.Length > maxVisibleItems;

                // Ensure viewStart is valid after resize
                if (!needsScrolling)
                {
                    viewStart = 0;
                }
                else
                {
                    viewStart = Math.Clamp(viewStart, 0, options.Length - maxVisibleItems);
                }

                // Adjust view to keep selection visible
                if (needsScrolling)
                {
                    if (selected < viewStart)
                    {
                        viewStart = selected;
                    }
                    else if (selected >= viewStart + maxVisibleItems)
                    {
                        viewStart = selected - maxVisibleItems + 1;
                    }
                }

                int viewEnd = needsScrolling ? Math.Min(viewStart + maxVisibleItems, options.Length) : options.Length;
                bool hasMoreAbove = viewStart > 0;
                bool hasMoreBelow = viewEnd < options.Length;

                // Calculate actual lines we'll draw (for cursor repositioning)
                int linesDrawn = 1; // title

                // Draw menu
                Console.SetCursorPosition(0, Console.CursorTop);
                MarkupLineClear($"[{ColorBold}]{Markup.Escape(title)}[/]");

                // Show "more above" indicator
                if (hasMoreAbove)
                {
                    MarkupLineClear($"[{ColorDim}]  ▲ {viewStart} more above[/]");
                    linesDrawn++;
                }

                // Draw visible options (use displayOptions for right-aligned numbers)
                for (int i = viewStart; i < viewEnd; i++)
                {
                    bool isEnabled = enabledStates?[i] ?? true;
                    string escapedOption = Markup.Escape(displayOptions[i]);

                    if (i == selected)
                    {
                        MarkupLineClear($"[{ColorSuccess}]> {escapedOption}[/]");
                    }
                    else if (!isEnabled)
                    {
                        MarkupLineClear($"[{ColorDim}]  {escapedOption}[/]");
                    }
                    else
                    {
                        MarkupLineClear($"  {escapedOption}");
                    }
                    linesDrawn++;
                }

                // Show "more below" indicator
                if (hasMoreBelow)
                {
                    MarkupLineClear($"[{ColorDim}]  ▼ {options.Length - viewEnd} more below[/]");
                    linesDrawn++;
                }

                MarkupLineClear($"[{ColorDim}]Arrows + Enter, number, letter, or Esc to go back[/]");
                linesDrawn++;

                var keyOrNull = InputHelpers.ReadKeyPolling();
                if (keyOrNull == null)
                {
                    return -1; // Status changed, signal caller to redraw
                }
                var key = keyOrNull.Value;

                char pressedKey = char.ToUpper(key.KeyChar);

                // Check leading keys first (e.g., "0. Back" responds to '0') - only if enabled
                if (leadingKeys.TryGetValue(pressedKey, out int leadingIdx))
                {
                    if (enabledStates == null || enabledStates[leadingIdx])
                    {
                        return leadingIdx;
                    }
                }

                // Mnemonic keys (from parentheses at end of option) - only if enabled
                if (mnemonics.TryGetValue(char.ToLower(key.KeyChar), out int idx))
                {
                    if (enabledStates == null || enabledStates[idx])
                    {
                        return idx;
                    }
                }

                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                        selected = FindNextEnabled(selected, -1, options.Length, enabledStates);
                        break;
                    case ConsoleKey.DownArrow:
                        selected = FindNextEnabled(selected, 1, options.Length, enabledStates);
                        break;
                    case ConsoleKey.PageUp:
                        // Move up by a page
                        for (int i = 0; i < maxVisibleItems && selected > 0; i++)
                        {
                            selected = FindNextEnabled(selected, -1, options.Length, enabledStates);
                        }
                        break;
                    case ConsoleKey.PageDown:
                        // Move down by a page
                        for (int i = 0; i < maxVisibleItems && selected < options.Length - 1; i++)
                        {
                            selected = FindNextEnabled(selected, 1, options.Length, enabledStates);
                        }
                        break;
                    case ConsoleKey.Home:
                        selected = FindNextEnabled(-1, 1, options.Length, enabledStates);
                        break;
                    case ConsoleKey.End:
                        selected = FindNextEnabled(options.Length, -1, options.Length, enabledStates);
                        break;
                    case ConsoleKey.Enter:
                        if (enabledStates == null || enabledStates[selected])
                        {
                            return selected;
                        }
                        break;
                    case ConsoleKey.Escape:
                        return options.Length - 1; // Assume last option is Back/Exit
                }

                // Move cursor back up to redraw
                int newTop = Math.Max(0, Console.CursorTop - linesDrawn);
                Console.SetCursorPosition(0, newTop);
            }
        }

        /// <summary>
        /// Finds the next enabled item in the given direction, wrapping around.
        /// </summary>
        private static int FindNextEnabled(int current, int direction, int count, bool[]? enabledStates)
        {
            if (enabledStates == null)
            {
                return (current + direction + count) % count;
            }

            int next = current;
            for (int i = 0; i < count; i++)
            {
                next = (next + direction + count) % count;
                if (enabledStates[next])
                {
                    return next;
                }
            }
            return current; // No enabled items found, stay put
        }

        /// <summary>
        /// Displays a menu from a MenuDef and returns the selected MenuItem.
        /// Disabled items (based on EnabledWhen) are shown dimmed and not selectable.
        /// </summary>
        public static MenuItem<T>? ShowMenuWithRefresh<T>(string title, MenuDef<T> menu, int initialSelection = 0) where T : notnull
        {
            int index = ShowMenu(title, menu.Labels, initialSelection, menu.GetEnabledStates());
            if (index < 0)
            {
                return null; // Status changed, caller should redraw
            }
            return menu[index];
        }

        public static MenuItem<T> ShowMenu<T>(string title, MenuDef<T> menu, int initialSelection = 0) where T : notnull
        {
            while (true)
            {
                int index = ShowMenu(title, menu.Labels, initialSelection, menu.GetEnabledStates());
                if (index >= 0)
                {
                    return menu[index];
                }
                // Status changed (index == -1), redraw and try again
            }
        }

        /// <summary>
        /// Displays a confirmation dialog. Returns true for yes, false for no.
        /// Escape returns the default value.
        /// Responds immediately on keypress (no Enter required).
        /// </summary>
        public static bool Confirm(string message, bool defaultYes = false)
        {
            var result = ConfirmOrQuit(message, defaultYes);
            return result ?? defaultYes; // Escape/quit returns default
        }

        /// <summary>
        /// Displays a confirmation dialog with quit option.
        /// Returns true for yes, false for no, null for quit/Escape.
        /// Responds immediately on keypress (no Enter required).
        /// </summary>
        public static bool? ConfirmOrQuit(string message, bool defaultYes = false)
        {
            string defaultHint = defaultYes ? "Y/n/q" : "y/N/q";
            AnsiConsole.Markup($"{message} [{ColorPrompt}][[{defaultHint}]][/] ");

            while (true)
            {
                var key = Console.ReadKey(true);
                char c = char.ToLower(key.KeyChar);

                if (key.Key == ConsoleKey.Enter)
                {
                    AnsiConsole.WriteLine(defaultYes ? "y" : "n");
                    return defaultYes;
                }
                if (key.Key == ConsoleKey.Y || c == 'y')
                {
                    AnsiConsole.WriteLine("y");
                    return true;
                }
                if (key.Key == ConsoleKey.N || c == 'n')
                {
                    AnsiConsole.WriteLine("n");
                    return false;
                }
                if (key.Key == ConsoleKey.Q || key.Key == ConsoleKey.Escape || c == 'q')
                {
                    AnsiConsole.WriteLine();
                    return null;
                }
            }
        }

        /// <summary>
        /// Prompts for a numeric value with quit option.
        /// Returns the value, or null if user pressed q/Escape.
        /// </summary>
        public static double? AskDouble(string prompt, double defaultValue)
        {
            AnsiConsole.Markup($"{prompt} [{ColorPrompt}][[{defaultValue:G}]][/]: ");

            var input = new System.Text.StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(true);

                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    if (input.Length == 0)
                    {
                        return defaultValue;
                    }
                    if (double.TryParse(input.ToString(), out double result))
                    {
                        return result;
                    }
                    AnsiConsole.MarkupLine($"[{ColorError}]Invalid number. Try again.[/]");
                    AnsiConsole.Markup($"{prompt} [{ColorPrompt}][[{defaultValue:G}]][/]: ");
                    input.Clear();
                    continue;
                }

                if (key.Key == ConsoleKey.Escape)
                {
                    Console.WriteLine();
                    return null;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (input.Length > 0)
                    {
                        input.Remove(input.Length - 1, 1);
                        Console.Write("\b \b");
                    }
                    continue;
                }

                if (char.IsDigit(key.KeyChar) || key.KeyChar == '.' || key.KeyChar == '-' || key.KeyChar == '+')
                {
                    input.Append(key.KeyChar);
                    Console.Write(key.KeyChar);
                }
            }
        }

        /// <summary>
        /// Displays a message and waits for Enter to continue.
        /// Used by macro system for user prompts.
        /// </summary>
        public static void ShowPrompt(string message)
        {
            AnsiConsole.MarkupLine($"[{ColorWarning}]{Markup.Escape(message)}[/]");
            WaitEnter();
        }

        /// <summary>
        /// Waits for Enter key to be pressed.
        /// </summary>
        /// <param name="message">Optional custom message. Default: "Press Enter to continue"</param>
        public static void WaitEnter(string? message = null)
        {
            AnsiConsole.MarkupLine($"[{ColorDim}]{message ?? CliConstants.PromptEnter}[/]");
            Console.ReadLine();
        }
    }
}
