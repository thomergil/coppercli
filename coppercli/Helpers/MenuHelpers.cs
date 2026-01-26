// Extracted from Program.cs - Menu display helpers

using Spectre.Console;
using System.Text.RegularExpressions;

namespace coppercli.Helpers
{
    /// <summary>
    /// A menu item with a label, mnemonic key, option type, and optional data.
    /// </summary>
    public record MenuItem<T>(string Label, char Mnemonic, T Option, int Data = 0);

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

        public string[] Labels => _items.Select((item, i) => $"{i + 1}. {item.Label} ({item.Mnemonic})").ToArray();

        public int IndexOf(T option) => _items.FindIndex(item => EqualityComparer<T>.Default.Equals(item.Option, option));

        public MenuItem<T> this[int index] => _items[index];
    }

    /// <summary>
    /// Helper methods for displaying menus.
    /// </summary>
    internal static class MenuHelpers
    {
        /// <summary>
        /// Displays a menu and returns the selected index. Supports arrow navigation, number keys, and mnemonic keys.
        /// Options format: "1. Label (x)" where x is the mnemonic key.
        /// </summary>
        public static int ShowMenu(string title, string[] options, int initialSelection = 0)
        {
            int selected = Math.Clamp(initialSelection, 0, options.Length - 1);

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
                    if (i == selected)
                    {
                        AnsiConsole.MarkupLine($"[green]> {options[i]}[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"  {options[i]}");
                    }
                }
                AnsiConsole.MarkupLine("[dim]Arrows + Enter, number, letter, or Esc to go back[/]");

                var key = Console.ReadKey(true);

                char pressedKey = char.ToUpper(key.KeyChar);

                // Check leading keys first (e.g., "0. Back" responds to '0')
                if (leadingKeys.TryGetValue(pressedKey, out int leadingIdx))
                {
                    return leadingIdx;
                }

                // Mnemonic keys (from parentheses at end of option)
                if (mnemonics.TryGetValue(char.ToLower(key.KeyChar), out int idx))
                {
                    return idx;
                }

                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                        selected = (selected - 1 + options.Length) % options.Length;
                        break;
                    case ConsoleKey.DownArrow:
                        selected = (selected + 1) % options.Length;
                        break;
                    case ConsoleKey.Enter:
                        return selected;
                    case ConsoleKey.Escape:
                        return options.Length - 1; // Assume last option is Back/Exit
                }

                // Move cursor back up to redraw
                Console.SetCursorPosition(0, Console.CursorTop - options.Length - 2);
            }
        }

        /// <summary>
        /// Displays a menu from a MenuDef and returns the selected MenuItem.
        /// </summary>
        public static MenuItem<T> ShowMenu<T>(string title, MenuDef<T> menu, int initialSelection = 0) where T : notnull
        {
            int index = ShowMenu(title, menu.Labels, initialSelection);
            return menu[index];
        }

        /// <summary>
        /// Displays a confirmation dialog. Returns true for yes, false for no.
        /// </summary>
        public static bool Confirm(string message, bool defaultYes = false)
        {
            return AnsiConsole.Confirm(message, defaultYes);
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
