using coppercli.Helpers;
using coppercli.Menus;
using Spectre.Console;
using static coppercli.CliConstants;

namespace coppercli.Macro
{
    internal static class MacroMenu
    {
        private enum MacroAction { Load, Run, Back }

        public static void Show()
        {
            if (!MenuHelpers.RequireConnection())
            {
                return;
            }

            var session = AppState.Session;

            while (true)
            {
                var hasLoadedMacro = !string.IsNullOrEmpty(session.LastMacroFile) && File.Exists(session.LastMacroFile);
                var macroFileName = hasLoadedMacro ? Path.GetFileName(session.LastMacroFile) : "(none)";

                Console.Clear();
                AnsiConsole.Write(new Rule($"[{ColorBold} {ColorPrompt}]Macro[/]").RuleStyle(ColorPrompt));
                AnsiConsole.WriteLine();

                var menu = new MenuDef<MacroAction>(
                    new MenuItem<MacroAction>("Load Macro...", 'l', MacroAction.Load),
                    new MenuItem<MacroAction>($"Run {macroFileName}", 'r', MacroAction.Run,
                        Blocker: () => hasLoadedMacro ? null : "load macro first"),
                    new MenuItem<MacroAction>("Back", 'q', MacroAction.Back)
                );

                var selected = MenuHelpers.ShowMenu("Select an option:", menu);

                switch (selected.Option)
                {
                    case MacroAction.Load:
                        LoadMacro();
                        break;
                    case MacroAction.Run:
                        RunMacroFromPath(session.LastMacroFile!);
                        break;
                    case MacroAction.Back:
                        return;
                }
            }
        }

        private static void LoadMacro()
        {
            var path = BrowseForMacro();
            if (path != null)
            {
                SaveMacroSession(path);
                AnsiConsole.MarkupLine($"[{ColorSuccess}]Loaded: {Markup.Escape(Path.GetFileName(path))}[/]");
                Thread.Sleep(ConfirmationDisplayMs);
            }
        }

        private static string? BrowseForMacro()
        {
            var session = AppState.Session;
            return FileMenu.BrowseForFile(new[] { MacroExtension }, startDirectory: session.LastMacroBrowseDirectory);
        }

        private static void SaveMacroSession(string path)
        {
            var session = AppState.Session;
            session.LastMacroFile = path;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                session.LastMacroBrowseDirectory = dir;
            }
            Persistence.SaveSession();
        }

        public static void RunMacroFromPath(string path)
        {
            RunMacroFromPath(path, new Dictionary<string, string>());
        }

        /// <summary>
        /// A placeholder absent from `providedArgs` is browsed for before the run starts.
        /// </summary>
        public static void RunMacroFromPath(string path, Dictionary<string, string> providedArgs)
        {
            try
            {
                AnsiConsole.MarkupLine($"[{ColorDim}]Loading macro: {Markup.Escape(path)}[/]");

                var commands = MacroParser.Parse(path);
                var macroName = Path.GetFileName(path);

                var placeholders = MacroParser.ExtractPlaceholders(commands);
                if (placeholders.Count > 0)
                {
                    var values = new Dictionary<string, string>(providedArgs);

                    foreach (var ph in placeholders)
                    {
                        if (values.ContainsKey(ph.Name))
                        {
                            continue;
                        }

                        var displayName = ph.Name.Replace('_', ' ');
                        displayName = char.ToUpper(displayName[0]) + displayName[1..];
                        AnsiConsole.MarkupLine($"[{ColorWarning}]Select {Markup.Escape(displayName)}:[/]");

                        var filePath = FileMenu.BrowseForFile(GCodeExtensions);
                        if (filePath == null)
                        {
                            AnsiConsole.MarkupLine($"[{ColorWarning}]Macro cancelled.[/]");
                            return;
                        }

                        values[ph.Name] = filePath;
                    }

                    commands = MacroParser.SubstitutePlaceholders(commands, values);
                }

                SaveMacroSession(path);

                AnsiConsole.MarkupLine($"[{ColorDim}]Parsed {commands.Count} commands[/]");
                Thread.Sleep(MacroParseDisplayMs);

                var runner = new MacroRunner(commands, macroName);
                runner.Run();
            }
            catch (MacroParseException ex)
            {
                MenuHelpers.ShowFailure(CliConstants.FailedReadingTheMacro, ex);
                MenuHelpers.ShowPrompt("");
            }
            catch (Exception ex)
            {
                MenuHelpers.ShowFailure(CliConstants.FailedRunningTheMacro, ex);
                MenuHelpers.ShowPrompt("");
            }
        }
    }
}
