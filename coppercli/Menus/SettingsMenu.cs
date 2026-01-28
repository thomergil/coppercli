// Extracted from Program.cs

using Spectre.Console;
using coppercli.Helpers;
using static coppercli.CliConstants;

namespace coppercli.Menus
{
    /// <summary>
    /// Settings menu for editing configuration values.
    /// </summary>
    internal static class SettingsMenu
    {
        private enum SettingAction
        {
            SelectMachine,
            SetupToolSetter,
            JogFeed, JogDistance, JogFeedSlow, JogDistanceSlow,
            ProbeFeed, ProbeMaxDepth, ProbeSafeHeight,
            OutlineTraceHeight, OutlineTraceFeed,
            ToggleDebugLogging,
            Save, Back
        }

        private static readonly MenuDef<SettingAction> SettingsMenuDef = new(
            new MenuItem<SettingAction>("Select Machine", 'm', SettingAction.SelectMachine),
            new MenuItem<SettingAction>("Setup Tool Setter (override)", 't', SettingAction.SetupToolSetter),
            new MenuItem<SettingAction>("Jog Feed", 'f', SettingAction.JogFeed),
            new MenuItem<SettingAction>("Jog Distance", 'd', SettingAction.JogDistance),
            new MenuItem<SettingAction>("Jog Feed Slow", 'g', SettingAction.JogFeedSlow),
            new MenuItem<SettingAction>("Jog Distance Slow", 'e', SettingAction.JogDistanceSlow),
            new MenuItem<SettingAction>("Probe Feed", 'p', SettingAction.ProbeFeed),
            new MenuItem<SettingAction>("Probe Max Depth", 'm', SettingAction.ProbeMaxDepth),
            new MenuItem<SettingAction>("Probe Safe Height", 'h', SettingAction.ProbeSafeHeight),
            new MenuItem<SettingAction>("Outline Trace Height", 'o', SettingAction.OutlineTraceHeight),
            new MenuItem<SettingAction>("Outline Trace Feed", 'r', SettingAction.OutlineTraceFeed),
            new MenuItem<SettingAction>("Toggle Debug Logging", 'l', SettingAction.ToggleDebugLogging),
            new MenuItem<SettingAction>("Save Settings", 's', SettingAction.Save),
            new MenuItem<SettingAction>("Back", 'q', SettingAction.Back)
        );

        public static void Show(Action saveSettings)
        {
            var settings = AppState.Settings;

            while (true)
            {
                Console.Clear();
                AnsiConsole.Write(new Rule($"[{ColorBold} {ColorPrompt}]Settings[/]").RuleStyle(ColorPrompt));

                var table = new Table();
                table.AddColumn("Setting");
                table.AddColumn("Value");

                // Machine profile
                var profile = MachineProfiles.GetProfile(settings.MachineProfile);
                string machineDisplay = profile != null ? profile.Name ?? settings.MachineProfile : $"[{ColorDim}]Not selected[/]";
                table.AddRow("Machine", machineDisplay);

                table.AddRow("Serial Port", settings.SerialPortName);
                table.AddRow("Baud Rate", settings.SerialPortBaud.ToString());
                table.AddRow("Jog Feed", settings.JogFeed.ToString());
                table.AddRow("Jog Distance", settings.JogDistance.ToString());
                table.AddRow("Probe Feed", settings.ProbeFeed.ToString());
                table.AddRow("Probe Max Depth", settings.ProbeMaxDepth.ToString());
                table.AddRow("Probe Safe Height", settings.ProbeSafeHeight.ToString());
                table.AddRow("Outline Trace Height", settings.OutlineTraceHeight.ToString());
                table.AddRow("Outline Trace Feed", settings.OutlineTraceFeed.ToString());
                table.AddRow("Debug Logging", settings.EnableDebugLogging ? "On" : "Off");

                // Tool setter position
                if (settings.ToolSetterX != 0 || settings.ToolSetterY != 0)
                {
                    table.AddRow("Tool Setter Position", $"X{settings.ToolSetterX:F1} Y{settings.ToolSetterY:F1}");
                }
                else
                {
                    table.AddRow("Tool Setter Position", $"[{ColorDim}]Not configured[/]");
                }

                AnsiConsole.Write(table);
                AnsiConsole.WriteLine();

                var choice = MenuHelpers.ShowMenu("Edit setting:", SettingsMenuDef);

                switch (choice.Option)
                {
                    case SettingAction.SelectMachine:
                        SelectMachine(saveSettings);
                        break;
                    case SettingAction.SetupToolSetter:
                        SetupToolSetter(saveSettings);
                        break;
                    case SettingAction.JogFeed:
                        settings.JogFeed = MenuHelpers.Ask("Jog Feed:", settings.JogFeed);
                        break;
                    case SettingAction.JogDistance:
                        settings.JogDistance = MenuHelpers.Ask("Jog Distance:", settings.JogDistance);
                        break;
                    case SettingAction.JogFeedSlow:
                        settings.JogFeedSlow = MenuHelpers.Ask("Jog Feed (Slow):", settings.JogFeedSlow);
                        break;
                    case SettingAction.JogDistanceSlow:
                        settings.JogDistanceSlow = MenuHelpers.Ask("Jog Distance (Slow):", settings.JogDistanceSlow);
                        break;
                    case SettingAction.ProbeFeed:
                        settings.ProbeFeed = MenuHelpers.Ask("Probe Feed:", settings.ProbeFeed);
                        break;
                    case SettingAction.ProbeMaxDepth:
                        settings.ProbeMaxDepth = MenuHelpers.Ask("Probe Max Depth:", settings.ProbeMaxDepth);
                        break;
                    case SettingAction.ProbeSafeHeight:
                        settings.ProbeSafeHeight = MenuHelpers.Ask("Probe Safe Height:", settings.ProbeSafeHeight);
                        break;
                    case SettingAction.OutlineTraceHeight:
                        settings.OutlineTraceHeight = MenuHelpers.Ask("Outline Trace Height:", settings.OutlineTraceHeight);
                        break;
                    case SettingAction.OutlineTraceFeed:
                        settings.OutlineTraceFeed = MenuHelpers.Ask("Outline Trace Feed (mm/min):", settings.OutlineTraceFeed);
                        break;
                    case SettingAction.ToggleDebugLogging:
                        settings.EnableDebugLogging = !settings.EnableDebugLogging;
                        Logger.Enabled = settings.EnableDebugLogging;
                        break;
                    case SettingAction.Save:
                        saveSettings();
                        AnsiConsole.MarkupLine($"[{ColorSuccess}]Settings saved[/]");
                        Thread.Sleep(ResetWaitMs);
                        break;
                    case SettingAction.Back:
                        return;
                }
            }
        }

        /// <summary>
        /// Select machine profile from available options.
        /// </summary>
        private static void SelectMachine(Action saveSettings)
        {
            var settings = AppState.Settings;
            var profileIds = MachineProfiles.GetProfileIds();

            if (profileIds.Count == 0)
            {
                MenuHelpers.ShowError("No machine profiles found");
                return;
            }

            Console.Clear();
            AnsiConsole.Write(new Rule($"[{ColorBold} {ColorPrompt}]Select Machine[/]").RuleStyle(ColorPrompt));
            AnsiConsole.MarkupLine($"[{ColorDim}]Select your CNC machine to load tool setter configuration.[/]");
            AnsiConsole.WriteLine();

            // Build menu options from profiles
            var options = new List<string>();
            for (int i = 0; i < profileIds.Count; i++)
            {
                var profile = MachineProfiles.GetProfile(profileIds[i]);
                string name = profile?.Name ?? profileIds[i];
                string toolSetter = profile?.ToolSetter != null ? "(has tool setter)" : "(no tool setter)";
                // Use index+1 as menu number, first letter of name as mnemonic
                char mnemonic = char.ToLower(name[0]);
                options.Add($"{i + 1}. {name} {toolSetter} ({mnemonic})");
            }
            options.Add($"{profileIds.Count + 1}. Clear selection (c)");
            options.Add($"0. Cancel (q)");

            // Find current selection index
            int currentIndex = profileIds.IndexOf(settings.MachineProfile);
            if (currentIndex < 0)
            {
                currentIndex = 0;
            }

            int selectedIndex = MenuHelpers.ShowMenu("Select machine:", options.ToArray(), currentIndex);

            // Cancel (last option, or Escape which also returns last)
            if (selectedIndex == options.Count - 1)
            {
                return;
            }

            // Clear selection
            if (selectedIndex == profileIds.Count)
            {
                settings.MachineProfile = "";
                saveSettings();
                AnsiConsole.MarkupLine($"[{ColorWarning}]Machine selection cleared[/]");
                MenuHelpers.WaitEnter();
                return;
            }

            // Machine selected
            if (selectedIndex >= 0 && selectedIndex < profileIds.Count)
            {
                settings.MachineProfile = profileIds[selectedIndex];
                var profile = MachineProfiles.GetProfile(settings.MachineProfile);
                saveSettings();
                AnsiConsole.MarkupLine($"[{ColorSuccess}]Selected: {profile?.Name ?? settings.MachineProfile}[/]");

                if (profile?.ToolSetter != null)
                {
                    AnsiConsole.MarkupLine($"Tool setter at X{profile.ToolSetter.X:F1} Y{profile.ToolSetter.Y:F1}");
                    AnsiConsole.MarkupLine($"[{ColorWarning}]Use 'Setup Tool Setter' to verify/override if needed.[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[{ColorWarning}]This machine has no tool setter configured.[/]");
                    AnsiConsole.MarkupLine($"[{ColorDim}]Tool changes will require re-probing the PCB surface.[/]");
                }
                MenuHelpers.WaitEnter();
            }
        }

        /// <summary>
        /// Interactive setup for tool setter position.
        /// User jogs to the tool setter and presses S to save.
        /// </summary>
        private static void SetupToolSetter(Action saveSettings)
        {
            var machine = AppState.Machine;
            var settings = AppState.Settings;

            if (!machine.Connected)
            {
                MenuHelpers.ShowError("Connect to machine first");
                return;
            }

            Console.Clear();
            Console.CursorVisible = false;

            try
            {
                while (true)
                {
                    var (winWidth, _) = DisplayHelpers.GetSafeWindowSize();

                    Console.SetCursorPosition(0, 0);
                    DisplayHelpers.WriteLineTruncated($"{DisplayHelpers.AnsiPrompt}Setup Tool Setter{DisplayHelpers.AnsiReset}", winWidth);
                    DisplayHelpers.WriteLineTruncated("", winWidth);
                    DisplayHelpers.WriteLineTruncated("Jog the spindle directly above your tool setter probe.", winWidth);
                    DisplayHelpers.WriteLineTruncated("The tool tip should be centered over the probe button/plate.", winWidth);
                    DisplayHelpers.WriteLineTruncated("", winWidth);

                    var mpos = machine.MachinePosition;
                    DisplayHelpers.WriteLineTruncated($"Machine Position: X:{DisplayHelpers.AnsiWarning}{mpos.X,9:F3}{DisplayHelpers.AnsiReset}  Y:{DisplayHelpers.AnsiWarning}{mpos.Y,9:F3}{DisplayHelpers.AnsiReset}  Z:{DisplayHelpers.AnsiWarning}{mpos.Z,9:F3}{DisplayHelpers.AnsiReset}", winWidth);
                    DisplayHelpers.WriteLineTruncated("", winWidth);

                    if (settings.ToolSetterX != 0 || settings.ToolSetterY != 0)
                    {
                        DisplayHelpers.WriteLineTruncated($"Current saved: X{settings.ToolSetterX:F1} Y{settings.ToolSetterY:F1}", winWidth);
                    }
                    else
                    {
                        DisplayHelpers.WriteLineTruncated($"{DisplayHelpers.AnsiDim}No tool setter position saved{DisplayHelpers.AnsiReset}", winWidth);
                    }
                    DisplayHelpers.WriteLineTruncated("", winWidth);

                    var mode = JogModes[AppState.JogPresetIndex];
                    DisplayHelpers.WriteLineTruncated($"{DisplayHelpers.AnsiInfo}Jog:{DisplayHelpers.AnsiReset} {DisplayHelpers.AnsiSuccess}{mode.Name}{DisplayHelpers.AnsiReset} {mode.Feed}mm/min {mode.BaseDistance}mm", winWidth);
                    DisplayHelpers.WriteLineTruncated($"  {DisplayHelpers.AnsiInfo}Arrows{DisplayHelpers.AnsiReset} or {DisplayHelpers.AnsiInfo}HJKL{DisplayHelpers.AnsiReset} - X/Y    {DisplayHelpers.AnsiInfo}W/S{DisplayHelpers.AnsiReset} or {DisplayHelpers.AnsiInfo}PgUp/PgDn{DisplayHelpers.AnsiReset} - Z", winWidth);
                    DisplayHelpers.WriteLineTruncated($"  {DisplayHelpers.AnsiInfo}Tab{DisplayHelpers.AnsiReset} - Cycle speed", winWidth);
                    DisplayHelpers.WriteLineTruncated("", winWidth);
                    DisplayHelpers.WriteLineTruncated($"  {DisplayHelpers.AnsiBoldGreen}Enter{DisplayHelpers.AnsiReset} - Save this position    {DisplayHelpers.AnsiInfo}C{DisplayHelpers.AnsiReset} - Clear saved    {DisplayHelpers.AnsiInfo}Esc/Q{DisplayHelpers.AnsiReset} - Cancel", winWidth);
                    DisplayHelpers.WriteLineTruncated("", winWidth);

                    var keyOrNull = InputHelpers.ReadKeyPolling();
                    if (keyOrNull == null)
                    {
                        continue;
                    }
                    var key = keyOrNull.Value;

                    if (InputHelpers.IsExitKey(key))
                    {
                        return;
                    }

                    if (key.Key == ConsoleKey.Enter)
                    {
                        // Save current machine position as tool setter
                        settings.ToolSetterX = mpos.X;
                        settings.ToolSetterY = mpos.Y;
                        saveSettings();
                        AnsiConsole.MarkupLine($"[{ColorSuccess}]Tool setter position saved: X{mpos.X:F1} Y{mpos.Y:F1}[/]");
                        Thread.Sleep(ConfirmationDisplayMs);
                        return;
                    }

                    if (InputHelpers.IsKey(key, ConsoleKey.C, 'c'))
                    {
                        // Clear tool setter position
                        settings.ToolSetterX = 0;
                        settings.ToolSetterY = 0;
                        saveSettings();
                        AnsiConsole.MarkupLine($"[{ColorWarning}]Tool setter position cleared[/]");
                        Thread.Sleep(ConfirmationDisplayMs);
                        continue;
                    }

                    if (key.Key == ConsoleKey.Tab)
                    {
                        AppState.JogPresetIndex = (AppState.JogPresetIndex + 1) % JogModes.Length;
                        continue;
                    }

                    // Handle jog keys
                    JogHelpers.HandleJogKey(key, machine, mode.Feed, mode.BaseDistance);
                }
            }
            finally
            {
                Console.CursorVisible = true;
            }
        }
    }
}
