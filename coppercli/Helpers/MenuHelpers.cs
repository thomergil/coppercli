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
    /// <summary>
    /// What stops a mill job from starting; GetMillBlockerReason holds the words for each case.
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

    /// <param name="Warnings">Shown to the operator, but none of them stops the job.</param>
    /// <param name="ProbeProgress">Set only for ProbeIncomplete, in the form "5/20".</param>
    /// <param name="DangerousWarnings">Set only when Warnings holds DangerousCommands.</param>
    public record MillStartCheck(
        MillBlocker Error,
        List<MillWarning> Warnings,
        string? ProbeProgress = null,
        List<string>? DangerousWarnings = null)
    {
        public bool CanStart => Error == MillBlocker.None;
    }

    /// <param name="Blocker">
    /// Why this item cannot be chosen, or null when it can. One definition settles both
    /// whether the item is selectable and the words shown beside it.
    /// </param>
    public record MenuItem<T>(string Label, char Mnemonic, T Option, int Data = 0, Func<string?>? Blocker = null)
    {
        public bool IsEnabled => CurrentDisabledReason == null;

        public string? CurrentDisabledReason => Blocker?.Invoke();
    }

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
        /// ShowMenu needs GetEnabledStates alongside these, or a disabled item is drawn like
        /// any other and can be chosen.
        /// </summary>
        public string[] Labels => _items.Select((item, i) =>
        {
            var reason = item.CurrentDisabledReason;
            return reason == null
                ? $"{i + 1}. {item.Label} [{item.Mnemonic}]"
                : $"{i + 1}. {item.Label} [{item.Mnemonic}] ({reason})";
        }).ToArray();

        /// <summary>
        /// Each Blocker runs again on every call, so the states follow the machine.
        /// </summary>
        public bool[] GetEnabledStates() => _items.Select(item => item.IsEnabled).ToArray();

        public char[] Mnemonics => _items.Select(item => item.Mnemonic).ToArray();

        public int IndexOf(T option) => _items.FindIndex(item => EqualityComparer<T>.Default.Equals(item.Option, option));

        public MenuItem<T> this[int index] => _items[index];
    }

    internal static class MenuHelpers
    {
        /// <summary>
        /// Probing and milling both call this, so neither is offered while the other is
        /// refused. A door hold is not counted, because the run prompts about the enclosure
        /// and releases the hold itself.
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

            // Loading, applying and discarding a height map all rewrite the file a run is
            // streaming, so the menu asks the same predicate a refusal would and shows a
            // shorter reason.
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
        /// One check for both front ends: GetMillDisabledReason in the terminal and
        /// HandleMillCanStart in the web server.
        /// </summary>
        /// <param name="probeGrid">
        /// The map to judge against, which the caller has already read. Required, because null
        /// here means the job has no map, which a default would make indistinguishable from a
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

            if (AppState.Machine.File.Count == 0)
            {
                return new MillStartCheck(MillBlocker.NoFile, warnings);
            }

            // Collected before the early returns below, so every outcome carries them.
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
                    dangerousWarnings = null;
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

            // An applied map has added its corrections to every cutting move, so if the setup
            // has moved since it was measured the whole job cuts at the wrong depth.
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

            // A warning rather than a blocker, because the run homes first.
            if (!AppState.Machine.IsHomed)
            {
                warnings.Add(MillWarning.NotHomed);
            }

            if (MachineProfiles.GetProfile(AppState.Settings.MachineProfile) == null)
            {
                warnings.Add(MillWarning.NoMachineProfile);
            }

            return new MillStartCheck(MillBlocker.None, warnings, null, dangerousWarnings);
        }

        /// <returns>The chosen rate, or <paramref name="current"/> if it was kept.</returns>
        public static int AskBaudRate(int current)
        {
            var options = CommonBaudRates.Select((rate, i) => $"{i + 1}. {rate}")
                .Append(BaudMenuKeepCurrent)
                .ToArray();

            int choice = ShowMenu(BaudMenuTitle, options);
            return choice < CommonBaudRates.Length ? CommonBaudRates[choice] : current;
        }

        public static MillStartCheck CheckMillCanStart() =>
            CheckMillCanStart(AppState.CurrentProbeGrid);

        public static string? GetMillDisabledReason(ProbeGrid? probeGrid) =>
            GetMillBlockerReason(CheckMillCanStart(probeGrid));

        public static string? GetMillDisabledReason() =>
            GetMillBlockerReason(CheckMillCanStart());

        /// <summary>
        /// One mapping from blocker to words, so the menu entry and the reason beside it
        /// cannot disagree.
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
        /// Shows the reason and waits for a keypress when it is not: an open port GRBL has not
        /// replied on is not a machine to send commands to.
        /// </summary>
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
        /// Waits for Enter, so the message survives the caller's next redraw.
        /// </summary>
        /// <param name="message">Plain text: it is escaped, so Spectre markup in it is not
        /// rendered.</param>
        public static void ShowError(string message)
        {
            AnsiConsole.MarkupLine($"[{ColorError}]{Markup.Escape(message)}[/]");
            WaitEnter();
        }

        public static T Ask<T>(string prompt, T defaultValue)
        {
            return AnsiConsole.Ask<T>(prompt, defaultValue);
        }

        public static T Ask<T>(string prompt)
        {
            return AnsiConsole.Ask<T>(prompt);
        }

        private const int MenuChromeLines = 4; // title, help, top and bottom scroll indicators

        /// <summary>
        /// Clears to end of line, so a shorter line does not leave the last frame's text
        /// standing beyond it.
        /// </summary>
        internal static void MarkupLineClear(string markup)
        {
            AnsiConsole.Markup(markup);
            Console.WriteLine(DisplayHelpers.AnsiClearToEol);
        }

        /// <summary>
        /// Returns the chosen index, or -1 when the machine status changed and the caller has
        /// to redraw. The list scrolls once it is taller than the terminal.
        /// </summary>
        /// <param name="enabledStates">Null makes every item selectable; a false entry is drawn
        /// dim and skipped.</param>
        /// <param name="mnemonicKeys">Null falls back to the bracketed letter in the option
        /// text.</param>
        public static int ShowMenu(string title, string[] options, int initialSelection = 0, bool[]? enabledStates = null, char[]? mnemonicKeys = null)
        {
            int selected = Math.Clamp(initialSelection, 0, options.Length - 1);

            if (enabledStates != null && !enabledStates[selected])
            {
                selected = FindNextEnabled(selected, 1, options.Length, enabledStates);
            }

            var mnemonics = new Dictionary<char, int>();
            var leadingKeys = new Dictionary<char, int>();
            for (int i = 0; i < options.Length; i++)
            {
                if (mnemonicKeys != null && i < mnemonicKeys.Length && mnemonicKeys[i] != '\0')
                {
                    mnemonics[char.ToLower(mnemonicKeys[i])] = i;
                }
                else
                {
                    var match = Regex.Match(options[i], @"\[(\w)\]");
                    if (match.Success)
                    {
                        mnemonics[char.ToLower(match.Groups[1].Value[0])] = i;
                    }
                }

                // A leading "0. ", "A. " or "10. " in the label.
                var leadingMatch = Regex.Match(options[i], @"^(\w+)\.");
                if (leadingMatch.Success)
                {
                    string prefix = leadingMatch.Groups[1].Value;
                    if (prefix.Length == 1)
                    {
                        leadingKeys[char.ToUpper(prefix[0])] = i;
                    }
                }
            }

            // Right-aligned, so "9." and "10." line up in a list long enough to need both.
            int maxPrefixWidth = 0;
            foreach (var opt in options)
            {
                var prefixMatch = Regex.Match(opt, @"^(\d+)\.");
                if (prefixMatch.Success)
                {
                    maxPrefixWidth = Math.Max(maxPrefixWidth, prefixMatch.Groups[1].Value.Length);
                }
            }

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

            var (_, termHeight) = DisplayHelpers.GetSafeWindowSize();
            int maxVisibleItems = Math.Max(3, termHeight - MenuChromeLines - Console.CursorTop);
            bool needsScrolling = options.Length > maxVisibleItems;
            int viewStart = 0;

            if (needsScrolling && selected >= maxVisibleItems)
            {
                viewStart = Math.Min(selected - maxVisibleItems + 1, options.Length - maxVisibleItems);
            }

            // Every redraw starts here, instead of stacking another copy down the screen.
            int startTop = Console.CursorTop;

            while (true)
            {
                Console.SetCursorPosition(0, startTop);

                // Recomputed each pass, because the terminal can be resized while the menu is up.
                (_, termHeight) = DisplayHelpers.GetSafeWindowSize();
                maxVisibleItems = Math.Max(3, termHeight - MenuChromeLines - startTop);
                needsScrolling = options.Length > maxVisibleItems;

                if (!needsScrolling)
                {
                    viewStart = 0;
                }
                else
                {
                    viewStart = Math.Clamp(viewStart, 0, options.Length - maxVisibleItems);
                }

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

                int linesDrawn = 1; // the title line

                MarkupLineClear($"[{ColorBold}]{Markup.Escape(title)}[/]");

                if (hasMoreAbove)
                {
                    MarkupLineClear($"[{ColorDim}]  ▲ {viewStart} more above[/]");
                    linesDrawn++;
                }

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
                    return -1;
                }
                var key = keyOrNull.Value;

                char pressedKey = char.ToUpper(key.KeyChar);

                // Leading keys are matched first, so "0. Back" answers to '0' even when another
                // item carries '0' as its mnemonic.
                if (leadingKeys.TryGetValue(pressedKey, out int leadingIdx))
                {
                    if (enabledStates == null || enabledStates[leadingIdx])
                    {
                        return leadingIdx;
                    }
                }

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
                        for (int i = 0; i < maxVisibleItems && selected > 0; i++)
                        {
                            selected = FindNextEnabled(selected, -1, options.Length, enabledStates);
                        }
                        break;
                    case ConsoleKey.PageDown:
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
                        // Cancel, or keep-what-you-have - so Escape picks it. A blocked last
                        // entry is left alone, because returning it would run a refused action.
                        if (enabledStates == null || enabledStates[options.Length - 1])
                        {
                            return options.Length - 1;
                        }
                        break;
                }

                int newTop = Math.Max(0, Console.CursorTop - linesDrawn);
                Console.SetCursorPosition(0, newTop);
            }
        }

        /// <summary>
        /// Wraps around the ends, and returns <paramref name="current"/> when no item is
        /// enabled.
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
            return current;
        }

        /// <summary>
        /// Returns null when the machine status changed and the caller has to redraw.
        /// </summary>
        public static MenuItem<T>? ShowMenuWithRefresh<T>(string title, MenuDef<T> menu, int initialSelection = 0) where T : notnull
        {
            int index = ShowMenu(title, menu.Labels, initialSelection, menu.GetEnabledStates(), menu.Mnemonics);
            if (index < 0)
            {
                return null;
            }
            return menu[index];
        }

        public static MenuItem<T> ShowMenu<T>(string title, MenuDef<T> menu, int initialSelection = 0) where T : notnull
        {
            int startTop = Console.CursorTop;

            while (true)
            {
                Console.SetCursorPosition(0, startTop);

                int index = ShowMenu(title, menu.Labels, initialSelection, menu.GetEnabledStates(), menu.Mnemonics);
                if (index >= 0)
                {
                    return menu[index];
                }
                // An index of -1 means the status changed: redraw and ask again.
            }
        }

        /// <summary>
        /// MachineWait.ClearDoorHoldAsync does the work, the same sequence a run follows; this
        /// supplies the overlay it prompts and announces through. A screen with a run behind it
        /// draws what the run publishes and never calls this.
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
                    // Keys typed while an earlier message was up are still buffered, and one of
                    // them would answer this prompt before the operator has read it.
                    InputHelpers.FlushKeyboard();

                    // Defaults to yes: this prompt only appears once GRBL reports the door
                    // closed, which is what it asks about.
                    return Task.FromResult(
                        DisplayHelpers.ShowOverlayConfirm(message, defaultYes: true) == true);
                },
                announce: message => DisplayHelpers.ShowOverlay(
                    message, messageColor: DisplayHelpers.AnsiWarning),
                // Escape is the way out while waiting on an open door or a park restore.
                onPoll: EscapePressed)
                .GetAwaiter().GetResult();

            if (outcome == DoorClearOutcome.WillNotRelease)
            {
                DisplayHelpers.ShowOverlayAndWait(ControllerConstants.ErrorDoorWillNotRelease);
            }

            return outcome == DoorClearOutcome.Cleared;
        }

        /// <summary>
        /// An exception's text names files, offsets and types the operator cannot act on, so it
        /// goes to the log and the screen gets a sentence. This is the one path a caught
        /// exception takes to the terminal, as WriteFailure is for the browser.
        /// </summary>
        /// <param name="what">What was being done, named the way the operator asked for it.</param>
        public static void ShowFailure(string what, System.Exception ex)
        {
            Logger.Log("{0} failed: {1}", what, ex);
            AnsiConsole.MarkupLine(
                $"[{ColorError}]{Markup.Escape(string.Format(ErrorSomethingFailed, what))}[/]");
        }

        /// <summary>
        /// ControllerError carries the run's own wording, written for the operator, so it goes
        /// to the screen as it stands.
        /// </summary>
        public static void ShowRunError(ControllerError fromTheRun)
        {
            DisplayHelpers.ShowOverlay(fromTheRun.Message, messageColor: DisplayHelpers.AnsiError);
        }

        /// <summary>
        /// For a screen that redraws straight afterwards and would otherwise wipe the message
        /// before it is read.
        /// </summary>
        /// <inheritdoc cref="ShowFailure" path="/param"/>
        public static void ShowFailureAndWait(string what, System.Exception ex)
        {
            ShowFailure(what, ex);
            WaitEnter();
        }

        /// <summary>
        /// Does not wait for a key, so a screen polling a wait can offer a way out of it. Any
        /// other key waiting is read and dropped, so a screen that also reads keys must not
        /// call this.
        /// </summary>
        internal static bool EscapePressed() =>
            Console.KeyAvailable && InputHelpers.IsEscapeKey(Console.ReadKey(true));

        /// <summary>
        /// A prompt with no title is just its message, so no empty heading is drawn above it.
        /// </summary>
        public static string FormatPrompt(string title, string message) =>
            string.IsNullOrEmpty(title) ? message : $"{title}\n\n{message}";

        /// <summary>
        /// The door prompt defaults to yes, because it only appears once GRBL reports the door
        /// closed; every other prompt defaults to no, so a reflex Enter cannot resume motion.
        /// The caller redraws its own content afterwards.
        /// </summary>
        /// <returns>True if the operator chose to continue.</returns>
        public static bool ShowPromptOverlay(UserInputRequest request)
        {
            string text = FormatPrompt(request.Title, request.Message);

            // A run can publish its next prompt from inside the call that answers this one, so
            // a keystroke still in the buffer would answer a prompt nobody has read.
            InputHelpers.FlushKeyboard();

            bool? proceed = DisplayHelpers.ShowOverlayConfirm(text, defaultYes: request.IsDoorPrompt);
            string response = proceed == true
                ? ControllerConstants.OptionContinue
                : ControllerConstants.OptionAbort;
            request.OnResponse(response);

            return response == ControllerConstants.OptionContinue;
        }

        /// <summary>
        /// Escape returns the default, and the first keypress answers, with no Enter needed.
        /// </summary>
        public static bool Confirm(string message, bool defaultYes = false) =>
            AskYesNo(message, defaultYes, offerQuit: false) ?? defaultYes;

        /// <summary>
        /// Null for quit or Escape, and the first keypress answers, with no Enter needed.
        /// </summary>
        public static bool? ConfirmOrQuit(string message, bool defaultYes = false) =>
            AskYesNo(message, defaultYes, offerQuit: true);

        /// <summary>
        /// The hint and the keys accepted are built from the same <paramref name="offerQuit"/>,
        /// so the prompt cannot offer a key it then refuses.
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
        /// Returns null when Escape was pressed. <paramref name="acceptChar"/> decides which
        /// characters are echoed and kept.
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
        /// Waits for Enter, and is what the macro prompt command puts on the screen.
        /// </summary>
        public static void ShowPrompt(string message)
        {
            AnsiConsole.MarkupLine($"[{ColorWarning}]{Markup.Escape(message)}[/]");
            WaitEnter();
        }

        /// <summary>
        /// Escape and Q continue as well, not only Enter.
        /// </summary>
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
