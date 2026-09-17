// Extracted from Program.cs - Menu display helpers

using System.Linq;
using System.Text.RegularExpressions;
using coppercli.Core.Communication;
using coppercli.Core.Controllers;
using coppercli.Core.GCode;
using Spectre.Console;
using static coppercli.CliConstants;
using static coppercli.Core.Util.Constants;

namespace coppercli.Helpers
{
    // =========================================================================
    // Whether a mill job can start, shared by the terminal and the web server
    // =========================================================================

    /// <summary>
    /// What stops a mill job from starting.
    /// Used to decouple validation logic from UI-specific error messages.
    /// </summary>
    public enum MillBlocker
    {
        None,
        NotConnected,
        NoFile,
        ProbeNotApplied,

        /// <summary>The applied height map no longer describes this file or origin.</summary>
        ProbeSetupChanged,
        ProbeIncomplete,
        AlarmState,

        /// <summary>The machine is in $SLP and accepts nothing until it is reset.</summary>
        Asleep
    }

    /// <summary>
    /// What the operator is warned about before a mill job starts.
    /// </summary>
    public enum MillWarning
    {
        NotHomed,
        DangerousCommands,
        NoMachineProfile
    }

    /// <summary>
    /// Whether a mill job can start, and why not.
    /// </summary>
    /// <param name="Error">Primary error preventing start, or None.</param>
    /// <param name="Warnings">List of warnings (non-blocking).</param>
    /// <param name="ProbeProgress">Probe progress if ProbeIncomplete (e.g., "5/20").</param>
    /// <param name="DangerousWarnings">List of dangerous file warnings if DangerousCommands warning.</param>
    public record MillStartCheck(
        MillBlocker Error,
        List<MillWarning> Warnings,
        string? ProbeProgress = null,
        List<string>? DangerousWarnings = null)
    {
        /// <summary>Whether a mill job can start, derived from the blocker.</summary>
        public bool CanStart => Error == MillBlocker.None;
    }

    /// <summary>One line of a menu.</summary>
    /// <param name="Blocker">
    /// Why this item cannot be chosen, or null when it can. One definition answers both
    /// whether the item is selectable and what to say about it.
    /// </param>
    public record MenuItem<T>(string Label, char Mnemonic, T Option, int Data = 0, Func<string?>? Blocker = null)
    {
        /// <summary>Whether this item can be chosen.</summary>
        public bool IsEnabled => CurrentDisabledReason == null;

        /// <summary>Why it cannot be chosen, or null when it can.</summary>
        public string? CurrentDisabledReason => Blocker?.Invoke();
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
        /// What about the machine stops a job being started, or None. Probing and milling
        /// both call this, so neither can be offered while the other is refused. The door is
        /// excluded: the run handles the enclosure and can release the hold.
        /// </summary>
        public static MillBlocker GetMachineBlocker()
        {
            var activity = MachineWait.GetActivity(AppState.Machine);

            if (!MachineWait.IsResponding(activity))
            {
                return MillBlocker.NotConnected;
            }

            if (!MachineWait.BlocksJobStart(activity))
            {
                return MillBlocker.None;
            }

            return activity == MachineActivity.Alarm ? MillBlocker.AlarmState : MillBlocker.Asleep;
        }

        /// <summary>
        /// Why a menu entry that needs the machine is unavailable, or null. Jog and Macro
        /// need no file or height map, so this is their whole check.
        /// </summary>
        public static string? GetMachineDisabledReason() =>
            GetMillBlockerReason(new MillStartCheck(
                GetMachineBlocker(), new List<MillWarning>()));

        /// <summary>
        /// Why probing is unavailable, or null when it can start. Shares the machine check
        /// with CheckMillCanStart, and adds the file and work zero.
        /// </summary>
        public static string? GetProbeDisabledReason()
        {
            string? machineReason = GetMachineDisabledReason();
            if (machineReason != null)
            {
                return machineReason;
            }

            // The probe screen loads, applies and discards the height map, all of which
            // rewrite the file a run is streaming. The menu says it in fewer words than a
            // refusal does, but it asks the one predicate.
            if (AppState.WhyTheFileCannotChange() != null)
            {
                return DisabledRunInProgress;
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
        /// Whether milling can start. One check, shared by the terminal and the web server.
        /// Used by both TUI (GetMillDisabledReason) and WebServer (HandleMillCanStart).
        /// </summary>
        /// <param name="probeGrid">
        /// The map to judge against, which the caller has already read. Required: null here
        /// means the job has no map, and a default would make that indistinguishable from a
        /// caller that did not look.
        /// </param>
        public static MillStartCheck CheckMillCanStart(ProbeGrid? probeGrid)
        {
            var warnings = new List<MillWarning>();
            List<string>? dangerousWarnings = null;
            string? probeProgress = null;

            // An open port GRBL has not answered is not a machine to start a job on.
            var machineBlocker = GetMachineBlocker();
            if (machineBlocker == MillBlocker.NotConnected)
            {
                return new MillStartCheck(machineBlocker, warnings);
            }

            // Check file loaded
            if (AppState.Machine.File.Count == 0)
            {
                return new MillStartCheck(MillBlocker.NoFile, warnings);
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
                    warnings.Add(MillWarning.DangerousCommands);
                }
                else
                {
                    dangerousWarnings = null;  // Empty list → null
                }
            }

            // A complete map in the autosave still has to be applied, or the cuts carry no
            // height correction.
            if (probeGrid != null && !AppState.AreProbePointsApplied)
            {
                if (!probeGrid.HasCompleteData)
                {
                    probeProgress = $"{probeGrid.Progress}/{probeGrid.TotalPoints}";
                    return new MillStartCheck(MillBlocker.ProbeIncomplete, warnings, probeProgress, dangerousWarnings);
                }
                return new MillStartCheck(MillBlocker.ProbeNotApplied, warnings, null, dangerousWarnings);
            }

            // A map already baked into the toolpath carries its corrections in every cutting
            // move, so if the setup has moved since it was measured the whole job cuts at the
            // wrong depth.
            if (probeGrid != null && AppState.AreProbePointsApplied)
            {
                if (!AppState.GetProbeApplicability().IsUsable())
                {
                    return new MillStartCheck(MillBlocker.ProbeSetupChanged, warnings, null, dangerousWarnings);
                }
            }

            if (machineBlocker != MillBlocker.None)
            {
                return new MillStartCheck(machineBlocker, warnings, null, dangerousWarnings);
            }

            // Check if homed (warning only - will home before milling)
            if (!AppState.Machine.IsHomed)
            {
                warnings.Add(MillWarning.NotHomed);
            }

            // Check if machine profile is selected (warning only)
            if (MachineProfiles.GetProfile(AppState.Settings.MachineProfile) == null)
            {
                warnings.Add(MillWarning.NoMachineProfile);
            }

            return new MillStartCheck(MillBlocker.None, warnings, null, dangerousWarnings);
        }

        /// <summary>
        /// Asks for a baud rate, offering the rates this project supports and an entry that
        /// keeps the one already set.
        /// </summary>
        /// <returns>The chosen rate, or <paramref name="current"/> if it was kept.</returns>
        public static int AskBaudRate(int current)
        {
            var options = CommonBaudRates.Select((rate, i) => $"{i + 1}. {rate}")
                .Append(BaudMenuKeepCurrent)
                .ToArray();

            int choice = ShowMenu(BaudMenuTitle, options);
            return choice < CommonBaudRates.Length ? CommonBaudRates[choice] : current;
        }

        /// <summary>Reads the map for a caller that has not, then asks the check above.</summary>
        public static MillStartCheck CheckMillCanStart() =>
            CheckMillCanStart(AppState.CurrentProbeGrid);

        /// <summary>
        /// Returns the reason milling is disabled, or null if milling is allowed.
        /// Wrapper around CheckMillCanStart() for simple menu disabled state.
        /// </summary>
        public static string? GetMillDisabledReason(ProbeGrid? probeGrid) =>
            GetMillBlockerReason(CheckMillCanStart(probeGrid));

        /// <inheritdoc cref="GetMillDisabledReason(ProbeGrid?)"/>
        public static string? GetMillDisabledReason() =>
            GetMillBlockerReason(CheckMillCanStart());

        /// <summary>
        /// The reason milling is blocked, or null. One mapping, so the menu entry and the
        /// reason beside it cannot list different cases.
        /// </summary>
        public static string? GetMillBlockerReason(MillStartCheck result) => result.Error switch
        {
            MillBlocker.None => null,
            MillBlocker.NotConnected => DisabledConnect,
            MillBlocker.NoFile => DisabledNoFile,
            MillBlocker.ProbeNotApplied => DisabledProbeNotApplied,
            MillBlocker.ProbeSetupChanged => DisabledProbeSetupChanged,
            MillBlocker.ProbeIncomplete => string.Format(DisabledProbeIncomplete, result.ProbeProgress),
            MillBlocker.AlarmState => DisabledAlarm,
            MillBlocker.Asleep => DisabledAsleep,
            _ => DisabledUnknown
        };

        /// <summary>
        /// Whether the machine is answering. Shows the reason and waits for a keypress if not:
        /// an open port GRBL has not replied on is not a machine to send a command to.
        /// </summary>
        /// <returns>True if connected, false otherwise.</returns>
        public static bool RequireConnection()
        {
            if (!MachineWait.IsResponding(AppState.Machine))
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
                        // Every caller's last entry is the one that changes nothing - Back,
                        // Cancel, or keep-what-you-have - so Escape picks it, if it is
                        // enabled. Returning a blocked entry would run a refused action.
                        if (enabledStates == null || enabledStates[options.Length - 1])
                        {
                            return options.Length - 1;
                        }
                        break;
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
        /// Disabled items are shown dimmed and not selectable, with the reason beside them.
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
        /// Block until the door is closed and the hold released, drawing the enclosure
        /// message over the current screen.
        ///
        /// The policy is MachineWait.ClearDoorHoldAsync, the same one a run follows; this
        /// supplies the overlay it asks and announces through. A screen with a run behind it
        /// draws what that run publishes and never calls this.
        /// </summary>
        /// <returns>
        /// True once the machine is out of Door. False if the operator backed out, or if the
        /// hold was still there after MachineClearAttempts releases.
        /// </returns>
        public static bool WaitForDoorClear(Machine machine)
        {
            var outcome = MachineWait.ClearDoorHoldAsync(
                machine,
                ask: message =>
                {
                    // Keys typed while a message was up are still buffered, and one of them
                    // would answer this prompt before the operator has read it.
                    InputHelpers.FlushKeyboard();

                    // Defaults to yes: this prompt only appears once GRBL reports the door
                    // closed, which is what it asks about.
                    return Task.FromResult(
                        DisplayHelpers.ShowOverlayConfirm(message, defaultYes: true) == true);
                },
                announce: message => DisplayHelpers.ShowOverlay(
                    message, messageColor: DisplayHelpers.AnsiWarning),
                // Escape while waiting out an open door or a park restore is the way out.
                onPoll: EscapePressed)
                .GetAwaiter().GetResult();

            if (outcome == DoorClearOutcome.WillNotRelease)
            {
                DisplayHelpers.ShowOverlayAndWait(ControllerConstants.ErrorDoorWillNotRelease);
            }

            return outcome == DoorClearOutcome.Cleared;
        }

        /// <summary>
        /// Report a caught exception to the operator. Its text names files, offsets and
        /// types they cannot act on, so that goes to the log and the screen gets a sentence.
        /// The one path a caught exception takes to the terminal, as WriteFailure is for the
        /// browser.
        /// </summary>
        /// <param name="what">What was being done, named the way the operator asked for it.</param>
        public static void ShowFailure(string what, System.Exception ex)
        {
            Logger.Log("{0} failed: {1}", what, ex);
            AnsiConsole.MarkupLine(
                $"[{ColorError}]{Markup.Escape(string.Format(ErrorSomethingFailed, what))}[/]");
        }

        /// <summary>
        /// Show what a run reported. ControllerError carries the run's own wording, written
        /// for the operator, so it is shown as it stands.
        /// </summary>
        public static void ShowRunError(ControllerError fromTheRun)
        {
            DisplayHelpers.ShowOverlay(fromTheRun.Message, messageColor: DisplayHelpers.AnsiError);
        }

        /// <summary>
        /// ShowFailure, then waits for a keypress. For a screen that redraws straight
        /// afterwards and would otherwise wipe the message before it is read.
        /// </summary>
        /// <inheritdoc cref="ShowFailure" path="/param"/>
        public static void ShowFailureAndWait(string what, System.Exception ex)
        {
            ShowFailure(what, ex);
            WaitEnter();
        }

        /// <summary>
        /// Whether the operator has pressed Escape. Does not wait for a key, so a screen can
        /// offer a way out of a wait it is polling. Any other key waiting is read and
        /// dropped, so a screen that also reads keys must not call this.
        /// </summary>
        internal static bool EscapePressed() =>
            Console.KeyAvailable && InputHelpers.IsEscapeKey(Console.ReadKey(true));

        /// <summary>
        /// A prompt as one block of text: title, then message. A prompt with no title is
        /// just its message, so no empty heading is drawn.
        /// </summary>
        public static string FormatPrompt(string title, string message) =>
            string.IsNullOrEmpty(title) ? message : $"{title}\n\n{message}";

        /// <summary>
        /// Draw a run's prompt over the current screen and answer it. The caller redraws
        /// its own content afterwards.
        ///
        /// The door prompt defaults to yes, because it only appears once GRBL reports the
        /// door closed. Every other prompt defaults to no, so a reflex Enter cannot resume
        /// motion. ShowOverlayConfirm renders [Y/n] or [y/N] to match.
        /// </summary>
        /// <returns>True if the operator chose to continue.</returns>
        public static bool ShowPromptOverlay(UserInputRequest request)
        {
            string text = FormatPrompt(request.Title, request.Message);

            // A run answers one prompt and can publish the next from inside that call, so a
            // keystroke still in the buffer would answer a prompt nobody has read.
            InputHelpers.FlushKeyboard();

            bool? proceed = DisplayHelpers.ShowOverlayConfirm(text, defaultYes: request.IsDoorPrompt);
            string response = proceed == true
                ? ControllerConstants.OptionContinue
                : ControllerConstants.OptionAbort;
            request.OnResponse(response);

            return response == ControllerConstants.OptionContinue;
        }

        /// <summary>
        /// Displays a confirmation dialog. Returns true for yes, false for no.
        /// Escape returns the default value.
        /// Responds immediately on keypress (no Enter required).
        /// </summary>
        public static bool Confirm(string message, bool defaultYes = false) =>
            AskYesNo(message, defaultYes, offerQuit: false) ?? defaultYes;

        /// <summary>
        /// Displays a confirmation dialog with quit option.
        /// Returns true for yes, false for no, null for quit/Escape.
        /// Responds immediately on keypress (no Enter required).
        /// </summary>
        public static bool? ConfirmOrQuit(string message, bool defaultYes = false) =>
            AskYesNo(message, defaultYes, offerQuit: true);

        /// <summary>
        /// Prompts yes/no and returns on the first keypress. The hint lists which key does
        /// what, including quit when the caller offers it, so what is drawn and what is
        /// accepted are set together.
        /// </summary>
        /// <returns>Null where the operator quit, which only <paramref name="offerQuit"/>
        /// allows.</returns>
        private static bool? AskYesNo(string message, bool defaultYes, bool offerQuit)
        {
            string hint = (defaultYes ? "Y/n" : "y/N") + (offerQuit ? "/q" : "");
            AnsiConsole.Markup($"{message} [{ColorPrompt}][[{hint}]][/] ");

            while (true)
            {
                var key = Console.ReadKey(true);

                if (InputHelpers.IsExitKey(key))
                {
                    if (offerQuit)
                    {
                        AnsiConsole.WriteLine();
                        return null;
                    }

                    // No quit option, so Escape returns the default.
                    AnsiConsole.WriteLine(defaultYes ? "y" : "n");
                    return defaultYes;
                }
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
            }
        }

        /// <summary>
        /// Prompts for a number, offering <paramref name="defaultValue"/>.
        /// </summary>
        /// <returns>
        /// The number typed, the default for an empty line, or null if Escape was pressed.
        /// </returns>
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
        /// Prompts for text, offering <paramref name="defaultValue"/>.
        /// </summary>
        /// <returns>
        /// The text typed, the default for an empty line, or null if Escape was pressed.
        /// </returns>
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

                if (InputHelpers.IsKey(key, ConsoleKey.Backspace))
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
