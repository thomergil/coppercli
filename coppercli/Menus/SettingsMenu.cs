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
            JogFeed, JogDistance, JogFeedSlow, JogDistanceSlow,
            ProbeFeed, ProbeMaxDepth, ProbeSafeHeight,
            OutlineTraceHeight, OutlineTraceFeed,
            ToggleDebugLogging,
            Save, Back
        }

        private static readonly MenuDef<SettingAction> SettingsMenuDef = new(
            new MenuItem<SettingAction>("Jog Feed", 'f', SettingAction.JogFeed),
            new MenuItem<SettingAction>("Jog Distance", 'd', SettingAction.JogDistance),
            new MenuItem<SettingAction>("Jog Feed Slow", 'g', SettingAction.JogFeedSlow),
            new MenuItem<SettingAction>("Jog Distance Slow", 'e', SettingAction.JogDistanceSlow),
            new MenuItem<SettingAction>("Probe Feed", 'p', SettingAction.ProbeFeed),
            new MenuItem<SettingAction>("Probe Max Depth", 'm', SettingAction.ProbeMaxDepth),
            new MenuItem<SettingAction>("Probe Safe Height", 'h', SettingAction.ProbeSafeHeight),
            new MenuItem<SettingAction>("Outline Trace Height", 't', SettingAction.OutlineTraceHeight),
            new MenuItem<SettingAction>("Outline Trace Feed", 'o', SettingAction.OutlineTraceFeed),
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
                AnsiConsole.Write(new Rule("[bold blue]Settings[/]").RuleStyle("blue"));

                var table = new Table();
                table.AddColumn("Setting");
                table.AddColumn("Value");

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

                AnsiConsole.Write(table);
                AnsiConsole.WriteLine();

                var choice = MenuHelpers.ShowMenu("Edit setting:", SettingsMenuDef);

                switch (choice.Option)
                {
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
                        AnsiConsole.MarkupLine("[green]Settings saved[/]");
                        Thread.Sleep(ResetWaitMs);
                        break;
                    case SettingAction.Back:
                        return;
                }
            }
        }
    }
}
