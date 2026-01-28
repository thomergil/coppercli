// Macro menu for browsing and running macros

using coppercli.Helpers;
using coppercli.Menus;
using Spectre.Console;
using static coppercli.CliConstants;

namespace coppercli.Macro
{
    /// <summary>
    /// Menu for browsing and running macro files.
    /// </summary>
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
                        EnabledWhen: () => hasLoadedMacro,
                        DisabledReason: () => hasLoadedMacro ? null : "load macro first"),
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

        /// <summary>
        /// Browse for and select a macro file (does not run it).
        /// </summary>
        private static void LoadMacro()
        {
            var path = BrowseForMacro();
            if (path != null)
            {
                var session = AppState.Session;
                session.LastMacroFile = path;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    session.LastMacroBrowseDirectory = dir;
                }
                Persistence.SaveSession();
                AnsiConsole.MarkupLine($"[{ColorSuccess}]Loaded: {Markup.Escape(Path.GetFileName(path))}[/]");
                Thread.Sleep(ConfirmationDisplayMs);
            }
        }

        /// <summary>
        /// Browse for a macro file, starting from the last macro directory.
        /// </summary>
        private static string? BrowseForMacro()
        {
            var session = AppState.Session;
            return FileMenu.BrowseForFile(new[] { MacroExtension }, startDirectory: session.LastMacroBrowseDirectory);
        }

        /// <summary>
        /// Run a macro from a file path, prompting for any placeholders.
        /// </summary>
        public static void RunMacroFromPath(string path)
        {
            RunMacroFromPath(path, new Dictionary<string, string>());
        }

        /// <summary>
        /// Run a macro from a file path with pre-provided placeholder values.
        /// Missing placeholders will be prompted interactively.
        /// </summary>
        public static void RunMacroFromPath(string path, Dictionary<string, string> providedArgs)
        {
            try
            {
                AnsiConsole.MarkupLine($"[{ColorDim}]Loading macro: {Markup.Escape(path)}[/]");

                var commands = MacroParser.Parse(path);
                var macroName = Path.GetFileName(path);

                // Extract placeholders and prompt for missing values
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

                        // Prompt for this placeholder
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

                // Save as last macro file
                var session = AppState.Session;
                session.LastMacroFile = path;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    session.LastMacroBrowseDirectory = dir;
                }
                Persistence.SaveSession();

                AnsiConsole.MarkupLine($"[{ColorDim}]Parsed {commands.Count} commands[/]");
                Thread.Sleep(MacroParseDisplayMs);

                var runner = new MacroRunner(commands, macroName);
                runner.Run();
            }
            catch (MacroParseException ex)
            {
                AnsiConsole.MarkupLine($"[{ColorError}]Macro parse error: {Markup.Escape(ex.Message)}[/]");
                MenuHelpers.ShowPrompt("");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[{ColorError}]Error running macro: {Markup.Escape(ex.Message)}[/]");
                MenuHelpers.ShowPrompt("");
            }
        }
    }
}
