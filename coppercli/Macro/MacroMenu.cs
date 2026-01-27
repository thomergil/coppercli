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
        public static void Show()
        {
            if (!MenuHelpers.RequireConnection())
            {
                return;
            }

            var session = AppState.Session;
            var hasLastMacro = !string.IsNullOrEmpty(session.LastMacroFile) && File.Exists(session.LastMacroFile);

            while (true)
            {
                Console.Clear();
                AnsiConsole.Write(new Rule("[bold blue]Macro[/]").RuleStyle("blue"));
                AnsiConsole.WriteLine();

                var options = new List<string>();
                if (hasLastMacro)
                {
                    var fileName = Path.GetFileName(session.LastMacroFile);
                    options.Add($"1. Run {fileName} (r)");
                    options.Add("2. Run Other Macro... (o)");
                }
                else
                {
                    options.Add("1. Run Macro... (r)");
                }
                options.Add("0. Back (q)");

                int choice = MenuHelpers.ShowMenu("Select an option:", options.ToArray());

                if (hasLastMacro)
                {
                    switch (choice)
                    {
                        case 0: // Run last macro
                            RunMacroFromPath(session.LastMacroFile);
                            break;
                        case 1: // Run other macro
                            RunMacro();
                            hasLastMacro = !string.IsNullOrEmpty(session.LastMacroFile) && File.Exists(session.LastMacroFile);
                            break;
                        default: // Back
                            return;
                    }
                }
                else
                {
                    switch (choice)
                    {
                        case 0: // Run Macro
                            RunMacro();
                            hasLastMacro = !string.IsNullOrEmpty(session.LastMacroFile) && File.Exists(session.LastMacroFile);
                            break;
                        default: // Back
                            return;
                    }
                }
            }
        }

        /// <summary>
        /// Browse for and run a macro file.
        /// </summary>
        private static void RunMacro()
        {
            var path = BrowseForMacro();
            if (path != null)
            {
                RunMacroFromPath(path);
            }
        }

        /// <summary>
        /// Browse for a macro file.
        /// </summary>
        private static string? BrowseForMacro()
        {
            return FileMenu.BrowseForFile(new[] { MacroExtension });
        }

        /// <summary>
        /// Run a macro from a file path.
        /// </summary>
        public static void RunMacroFromPath(string path)
        {
            try
            {
                AnsiConsole.MarkupLine($"[dim]Loading macro: {Markup.Escape(path)}[/]");

                var commands = MacroParser.Parse(path);
                var macroName = Path.GetFileName(path);

                // Save as last macro file
                var session = AppState.Session;
                session.LastMacroFile = path;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    session.LastMacroBrowseDirectory = dir;
                }
                Persistence.SaveSession();

                AnsiConsole.MarkupLine($"[dim]Parsed {commands.Count} commands[/]");
                Thread.Sleep(500);

                var runner = new MacroRunner(commands, macroName);
                runner.Run();
            }
            catch (MacroParseException ex)
            {
                AnsiConsole.MarkupLine($"[red]Macro parse error: {Markup.Escape(ex.Message)}[/]");
                MenuHelpers.PromptEnter("");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error running macro: {Markup.Escape(ex.Message)}[/]");
                MenuHelpers.PromptEnter("");
            }
        }
    }
}
