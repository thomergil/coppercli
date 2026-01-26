// Extracted from Program.cs - Menu display helpers

using Spectre.Console;
using System.Text.RegularExpressions;

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
        /// Displays an error message and waits for a keypress.
        /// </summary>
        /// <param name="message">The error message to display (without markup).</param>
        public static void ShowError(string message)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
            Console.ReadKey();
        }

        /// <summary>
        /// Displays a menu and returns the selected index. Supports arrow navigation, number keys, and mnemonic keys.
        /// Options format: "1. Label (x)" where x is the mnemonic key.
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

                // Check for leading number/letter (e.g., "0. " or "A. ")
                var leadingMatch = Regex.Match(options[i], @"^(\w)\.");
                if (leadingMatch.Success)
                {
                    leadingKeys[char.ToUpper(leadingMatch.Groups[1].Value[0])] = i;
                }
            }

            while (true)
            {
                // Draw menu
                Console.SetCursorPosition(0, Console.CursorTop);
                AnsiConsole.MarkupLine($"[bold]{title}[/]");
                for (int i = 0; i < options.Length; i++)
                {
                    bool isEnabled = enabledStates?[i] ?? true;

                    if (i == selected)
                    {
                        AnsiConsole.MarkupLine($"[green]> {options[i]}[/]");
                    }
                    else if (!isEnabled)
                    {
                        AnsiConsole.MarkupLine($"[dim]  {options[i]}[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"  {options[i]}");
                    }
                }
                AnsiConsole.MarkupLine("[dim]Arrows + Enter, number, letter, or Esc to go back[/]");

                // Poll for key input, allowing status refresh
                var lastStatus = AppState.Machine?.Status;
                while (!Console.KeyAvailable)
                {
                    Thread.Sleep(100);
                    var currentStatus = AppState.Machine?.Status;
                    if (currentStatus != lastStatus)
                    {
                        // Status changed, signal caller to redraw by returning -1
                        return -1;
                    }
                }
                var key = Console.ReadKey(true);

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
                Console.SetCursorPosition(0, Console.CursorTop - options.Length - 2);
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
            int index = ShowMenu(title, menu.Labels, initialSelection, menu.GetEnabledStates());
            return menu[Math.Max(0, index)];
        }

        /// <summary>
        /// Displays a confirmation dialog. Returns true for yes, false for no.
        /// </summary>
        public static bool Confirm(string message, bool defaultYes = false)
        {
            return AnsiConsole.Confirm(message, defaultYes);
        }

        /// <summary>
        /// Prompts for a numeric value with quit option.
        /// Returns the value, or null if user pressed q/Escape.
        /// </summary>
        public static double? AskDoubleOrQuit(string prompt, double defaultValue)
        {
            AnsiConsole.Markup($"{prompt} [blue][[{defaultValue:G}]][/] [dim](q to cancel)[/]: ");

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
                    AnsiConsole.MarkupLine($"[red]Invalid number. Try again.[/]");
                    AnsiConsole.Markup($"{prompt} [blue][[{defaultValue:G}]][/] [dim](q to cancel)[/]: ");
                    input.Clear();
                    continue;
                }

                if (key.Key == ConsoleKey.Escape || (input.Length == 0 && char.ToLower(key.KeyChar) == 'q'))
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

                if (char.IsDigit(key.KeyChar) || key.KeyChar == '.' || key.KeyChar == '-')
                {
                    input.Append(key.KeyChar);
                    Console.Write(key.KeyChar);
                }
            }
        }

        /// <summary>
        /// Displays a confirmation dialog with quit option.
        /// Returns true for yes, false for no, null for quit.
        /// </summary>
        public static bool? ConfirmOrQuit(string message, bool defaultYes = false)
        {
            string defaultHint = defaultYes ? "Y/n/q" : "y/N/q";
            AnsiConsole.Markup($"{message} [blue][[{defaultHint}]][/] ");

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
                    AnsiConsole.WriteLine("q");
                    return null;
                }
            }
        }
    }
}
