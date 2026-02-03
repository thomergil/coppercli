// Extracted from Program.cs - Menu display helpers

using System.Linq;
using System.Text.RegularExpressions;
using coppercli.Core.Controllers;
using Spectre.Console;
using static coppercli.CliConstants;
using static coppercli.Core.Util.Constants;

namespace coppercli.Helpers
{
    // =========================================================================
    // Preflight validation (shared by TUI and WebServer)
    // =========================================================================

    /// <summary>
    /// Error codes for mill preflight validation.
    /// Used to decouple validation logic from UI-specific error messages.
    /// </summary>
    public enum MillPreflightError
    {
        None,
        NotConnected,
        NoFile,
        ProbeNotApplied,
        ProbeIncomplete,
        AlarmState
    }

    /// <summary>
    /// Warning codes for mill preflight validation.
    /// </summary>
    public enum MillPreflightWarning
    {
        NotHomed,
        DangerousCommands,
        NoMachineProfile
    }

    /// <summary>
    /// Result of mill preflight validation.
    /// </summary>
    /// <param name="CanStart">True if milling can start.</param>
    /// <param name="Error">Primary error preventing start, or None.</param>
    /// <param name="Warnings">List of warnings (non-blocking).</param>
    /// <param name="ProbeProgress">Probe progress if ProbeIncomplete (e.g., "5/20").</param>
    /// <param name="DangerousWarnings">List of dangerous file warnings if DangerousCommands warning.</param>
    public record MillPreflightResult(
        bool CanStart,
        MillPreflightError Error,
        List<MillPreflightWarning> Warnings,
        string? ProbeProgress = null,
        List<string>? DangerousWarnings = null);

    /// <summary>
    /// A menu item with a label, mnemonic key, option type, and optional enabled condition.
    /// </summary>
    public record MenuItem<T>(string Label, char Mnemonic, T Option, int Data = 0, Func<bool>? EnabledWhen = null, Func<string?>? DisabledReason = null)
    {
        /// <summary>
        /// Returns true if this item is currently enabled (selectable).
        /// </summary>
        public bool IsEnabled => EnabledWhen?.Invoke() ?? true;

        /// <summary>
        /// Gets the disabled reason string, or null if enabled or no reason provided.
        /// </summary>
        public string? CurrentDisabledReason => IsEnabled ? null : DisabledReason?.Invoke();
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
        /// Gets labels for all items, including disabled reasons where applicable.
        /// Format: "1. Label [m] (reason)" where reason only appears for disabled items.
        /// Use GetEnabledStates() in conjunction for rendering disabled items dimmed.
        /// </summary>
        public string[] Labels => _items.Select((item, i) =>
        {
            var reason = item.CurrentDisabledReason;
            return reason == null
                ? $"{i + 1}. {item.Label} [{item.Mnemonic}]"
                : $"{i + 1}. {item.Label} [{item.Mnemonic}] ({reason})";
        }).ToArray();

        /// <summary>
        /// Gets the enabled state for each item at the current moment.
        /// </summary>
        public bool[] GetEnabledStates() => _items.Select(item => item.IsEnabled).ToArray();

        /// <summary>
        /// Gets the mnemonic keys for all items.
        /// </summary>
        public char[] Mnemonics => _items.Select(item => item.Mnemonic).ToArray();

        public int IndexOf(T option) => _items.FindIndex(item => EqualityComparer<T>.Default.Equals(item.Option, option));

        public MenuItem<T> this[int index] => _items[index];
    }

    /// <summary>
    /// Helper methods for displaying menus.
    /// </summary>
    internal static class MenuHelpers
    {
        /// <summary>
        /// Returns the reason probing is disabled, or null if probing is allowed.
        /// Checks: connection, file loaded, work zero set.
        /// </summary>
        public static string? GetProbeDisabledReason()
        {
            if (!AppState.Machine.Connected)
            {
                return DisabledConnect;
            }
            if (AppState.CurrentFile == null)
            {
                return DisabledNoFile;
            }
            if (!AppState.IsWorkZeroSet)
            {
                return DisabledNoZero;
            }
            return null;
        }

        /// <summary>
        /// Validates whether milling can start. Single source of truth for preflight checks.
        /// Used by both TUI (GetMillDisabledReason) and WebServer (HandleMillPreflight).
        /// </summary>
        public static MillPreflightResult ValidateMillPreflight()
        {
            var warnings = new List<MillPreflightWarning>();
            List<string>? dangerousWarnings = null;
            string? probeProgress = null;

            // Check connection
            if (!AppState.Machine.Connected)
            {
                return new MillPreflightResult(false, MillPreflightError.NotConnected, warnings);
            }

            // Check file loaded
            if (AppState.Machine.File.Count == 0)
            {
                return new MillPreflightResult(false, MillPreflightError.NoFile, warnings);
            }

            // Check for dangerous warnings in file (collect early, always returned)
            var currentFile = AppState.CurrentFile;
            if (currentFile?.Warnings.Count > 0)
            {
                dangerousWarnings = currentFile.Warnings
                    .Where(w => w.Contains(WarningPrefixDanger) || w.Contains(WarningPrefixInches))
                    .ToList();
                if (dangerousWarnings.Count > 0)
                {
                    warnings.Add(MillPreflightWarning.DangerousCommands);
                }
                else
                {
                    dangerousWarnings = null;  // Empty list → null
                }
            }

            // Check probe data applied (if probe points exist)
            if (AppState.ProbePoints != null && !AppState.AreProbePointsApplied)
            {
                if (AppState.ProbePoints.NotProbed.Count > 0)
                {
                    probeProgress = $"{AppState.ProbePoints.Progress}/{AppState.ProbePoints.TotalPoints}";
                    return new MillPreflightResult(false, MillPreflightError.ProbeIncomplete, warnings, probeProgress, dangerousWarnings);
                }
                return new MillPreflightResult(false, MillPreflightError.ProbeNotApplied, warnings, null, dangerousWarnings);
            }

            // Check machine state (alarm)
            if (MachineWait.IsAlarm(AppState.Machine))
            {
                return new MillPreflightResult(false, MillPreflightError.AlarmState, warnings, null, dangerousWarnings);
            }

            // Check if homed (warning only - will home before milling)
            if (!AppState.Machine.IsHomed)
            {
                warnings.Add(MillPreflightWarning.NotHomed);
            }

            // Check if machine profile is selected (warning only)
            if (MachineProfiles.GetProfile(AppState.Settings.MachineProfile) == null)
            {
                warnings.Add(MillPreflightWarning.NoMachineProfile);
            }

            return new MillPreflightResult(true, MillPreflightError.None, warnings, null, dangerousWarnings);
        }

        /// <summary>
        /// Returns the reason milling is disabled, or null if milling is allowed.
        /// Wrapper around ValidateMillPreflight() for simple menu disabled state.
        /// Note: AlarmState is NOT checked here (handled by EnsureMachineReady in MillMenu).
        /// </summary>
        public static string? GetMillDisabledReason()
        {
            var result = ValidateMillPreflight();

            // Map errors to disabled reasons (AlarmState handled separately in MillMenu)
            return result.Error switch
            {
                MillPreflightError.None => null,
                MillPreflightError.NotConnected => DisabledConnect,
                MillPreflightError.NoFile => DisabledNoFile,
                MillPreflightError.ProbeNotApplied => DisabledProbeNotApplied,
                MillPreflightError.ProbeIncomplete => string.Format(DisabledProbeIncomplete, result.ProbeProgress),
                MillPreflightError.AlarmState => null,  // Not a menu blocker (handled in MillMenu)
                _ => DisabledUnknown
            };
        }

        /// <summary>
        /// Checks if the machine is connected. Shows error and waits for keypress if not.
        /// </summary>
        /// <returns>True if connected, false otherwise.</returns>
        public static bool RequireConnection()
        {
            if (!AppState.Machine.Connected)
            {
                ShowError(ErrorNotConnected);
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

        /// <summary>
        /// Writes a markup line and clears to end of line (prevents ghost text when redrawing).
        /// </summary>
        internal static void MarkupLineClear(string markup)
        {
            AnsiConsole.Markup(markup);
            Console.WriteLine(DisplayHelpers.AnsiClearToEol);
        }

        /// <summary>
        /// Displays a menu and returns the selected index. Supports arrow navigation, number keys, and mnemonic keys.
        /// Automatically scrolls when there are more options than fit in the terminal.
        /// </summary>
        /// <param name="enabledStates">Optional array indicating which items are enabled. Disabled items shown dim and not selectable.</param>
        /// <param name="mnemonicKeys">Optional array of mnemonic characters for each option. If null, extracts from option text.</param>
        public static int ShowMenu(string title, string[] options, int initialSelection = 0, bool[]? enabledStates = null, char[]? mnemonicKeys = null)
        {
            int selected = Math.Clamp(initialSelection, 0, options.Length - 1);

            // If initial selection is disabled, find first enabled item
            if (enabledStates != null && !enabledStates[selected])
            {
                selected = FindNextEnabled(selected, 1, options.Length, enabledStates);
            }

            // Build mnemonic dictionary - use provided keys or extract from text
            var mnemonics = new Dictionary<char, int>();
            var leadingKeys = new Dictionary<char, int>();
            for (int i = 0; i < options.Length; i++)
            {
                // Use provided mnemonic if available
                if (mnemonicKeys != null && i < mnemonicKeys.Length && mnemonicKeys[i] != '\0')
                {
                    mnemonics[char.ToLower(mnemonicKeys[i])] = i;
                }
                else
                {
                    // Fall back to extracting from brackets (e.g., "[c]")
                    var match = Regex.Match(options[i], @"\[(\w)\]");
                    if (match.Success)
                    {
                        mnemonics[char.ToLower(match.Groups[1].Value[0])] = i;
                    }
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

            // Remember starting position for redraw after status change
            int startTop = Console.CursorTop;

            while (true)
            {
                // Reset cursor to start position for clean redraw
                Console.SetCursorPosition(0, startTop);

                // Recalculate in case terminal was resized
                (_, termHeight) = DisplayHelpers.GetSafeWindowSize();
                maxVisibleItems = Math.Max(3, termHeight - MenuChromeLines - startTop);
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

                // Draw menu (cursor already positioned at start of menu area)
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
            int index = ShowMenu(title, menu.Labels, initialSelection, menu.GetEnabledStates(), menu.Mnemonics);
            if (index < 0)
            {
                return null; // Status changed, caller should redraw
            }
            return menu[index];
        }

        public static MenuItem<T> ShowMenu<T>(string title, MenuDef<T> menu, int initialSelection = 0) where T : notnull
        {
            // Capture start position so redraws don't stack
            int startTop = Console.CursorTop;

            while (true)
            {
                // Reset to start position before each draw
                Console.SetCursorPosition(0, startTop);

                int index = ShowMenu(title, menu.Labels, initialSelection, menu.GetEnabledStates(), menu.Mnemonics);
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
            string hint = defaultYes ? "Y/n" : "y/N";
            AnsiConsole.Markup($"{message} [{ColorPrompt}][[{hint}]][/] ");

            while (true)
            {
                var key = Console.ReadKey(true);

                if (InputHelpers.IsEnterKey(key) || InputHelpers.IsExitKey(key))
                {
                    AnsiConsole.WriteLine(defaultYes ? "y" : "n");
                    return defaultYes;
                }
                if (InputHelpers.IsKey(key, ConsoleKey.Y))
                {
                    AnsiConsole.WriteLine("y");
                    return true;
                }
                if (InputHelpers.IsKey(key, ConsoleKey.N))
                {
                    AnsiConsole.WriteLine("n");
                    return false;
                }
            }
        }

        /// <summary>
        /// Displays a confirmation dialog with quit option.
        /// Returns true for yes, false for no, null for quit/Escape.
        /// Responds immediately on keypress (no Enter required).
        /// </summary>
        public static bool? ConfirmOrQuit(string message, bool defaultYes = false)
        {
            string hint = defaultYes ? "Y/n/q" : "y/N/q";
            AnsiConsole.Markup($"{message} [{ColorPrompt}][[{hint}]][/] ");

            while (true)
            {
                var key = Console.ReadKey(true);

                if (InputHelpers.IsEnterKey(key))
                {
                    AnsiConsole.WriteLine(defaultYes ? "y" : "n");
                    return defaultYes;
                }
                if (InputHelpers.IsKey(key, ConsoleKey.Y))
                {
                    AnsiConsole.WriteLine("y");
                    return true;
                }
                if (InputHelpers.IsKey(key, ConsoleKey.N))
                {
                    AnsiConsole.WriteLine("n");
                    return false;
                }
                if (InputHelpers.IsExitKey(key))
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
            while (true)
            {
                AnsiConsole.Markup($"{prompt} [{ColorPrompt}][[{defaultValue:G}]][/]: ");
                var input = ReadLineWithEscape(c => char.IsDigit(c) || c == '.' || c == '-' || c == '+');

                if (input == null)
                {
                    return null;
                }
                if (input.Length == 0)
                {
                    return defaultValue;
                }
                if (double.TryParse(input, out double result))
                {
                    return result;
                }
                AnsiConsole.MarkupLine($"[{ColorError}]Invalid number. Try again.[/]");
            }
        }

        /// <summary>
        /// Prompts for a string value with quit option.
        /// Returns the value, or null if user pressed Escape.
        /// </summary>
        public static string? AskString(string prompt, string defaultValue)
        {
            AnsiConsole.Markup($"{prompt} [{ColorPrompt}][[{Markup.Escape(defaultValue)}]][/]: ");
            var input = ReadLineWithEscape(c => !char.IsControl(c));
            return input == null ? null : (input.Length == 0 ? defaultValue : input);
        }

        /// <summary>
        /// Reads a line of input with Escape to cancel and Backspace support.
        /// Returns the input string, or null if Escape was pressed.
        /// </summary>
        private static string? ReadLineWithEscape(Func<char, bool> acceptChar)
        {
            var input = new System.Text.StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(true);

                if (InputHelpers.IsEnterKey(key))
                {
                    Console.WriteLine();
                    return input.ToString();
                }

                if (InputHelpers.IsEscapeKey(key))
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

                if (acceptChar(key.KeyChar))
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
        /// Waits for Enter, Escape, or Q key to be pressed.
        /// </summary>
        /// <param name="message">Optional custom message. Default: "Press Enter to continue"</param>
        public static void WaitEnter(string? message = null)
        {
            AnsiConsole.MarkupLine($"[{ColorDim}]{message ?? CliConstants.PromptEnter}[/]");
            while (true)
            {
                var key = Console.ReadKey(true);
                if (InputHelpers.IsEnterKey(key) || InputHelpers.IsExitKey(key))
                {
                    return;
                }
            }
        }
    }
}
