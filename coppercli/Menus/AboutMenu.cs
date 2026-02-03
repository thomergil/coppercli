// Extracted from Program.cs

using coppercli.Helpers;
using Spectre.Console;
using static coppercli.CliConstants;

namespace coppercli.Menus
{
    /// <summary>
    /// About menu showing version info and experimental warning.
    /// </summary>
    internal static class AboutMenu
    {
        public static void Show()
        {
            Console.Clear();
            AnsiConsole.Write(new Rule($"[{ColorBold} {ColorPrompt}]About {AppTitle}[/]").RuleStyle(ColorPrompt));
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[{ColorBold}]{AppTitle} {AppVersion}[/] - Cross-platform PCB milling for GRBL");
            AnsiConsole.MarkupLine($"By Thomer Gil  [{ColorDim}]Based on OpenCNCPilot by martin2250[/]");
            AnsiConsole.MarkupLine("[link]https://github.com/thomergil/coppercli[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[{ColorBold} {ColorError}]!! EXTREMELY EXPERIMENTAL - May damage your CNC machine !![/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[{ColorBold}]Connectivity:[/]  Mac, Linux, Windows, web browser");
            AnsiConsole.MarkupLine($"[{ColorBold}]Modes:[/]         USB/Serial, network proxy, HTTP server");
            AnsiConsole.MarkupLine($"[{ColorBold}]Interface:[/]     Keyboard-driven, WASD jogging, vim-style multipliers");
            AnsiConsole.MarkupLine($"[{ColorBold}]Probing:[/]       Auto-leveling grid, outline traversal, session recovery");
            AnsiConsole.MarkupLine($"[{ColorBold}]Milling:[/]       Feed override, depth adjust, real-time visualization");
            AnsiConsole.MarkupLine($"[{ColorBold}]Tools:[/]         M6 tool change with tool setter, machine profiles");
            AnsiConsole.MarkupLine($"[{ColorBold}]Automation:[/]    Macros, file placeholders, safety-first design");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"Issues: [link]https://github.com/thomergil/coppercli/issues[/]");
            AnsiConsole.WriteLine();
            MenuHelpers.WaitEnter("Press Enter to return");
        }

        /// <summary>
        /// Shows experimental warning on first startup. Offers to silence for future runs.
        /// </summary>
        public static void ShowExperimentalWarning(Action saveSettings)
        {
            if (AppState.Settings.SilenceExperimentalWarning)
            {
                return;
            }

            Console.Clear();
            AnsiConsole.Write(new Rule($"[{ColorBold} {ColorError}]WARNING[/]").RuleStyle(ColorError));
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[{ColorBold} {ColorError}]!! EXTREMELY EXPERIMENTAL !![/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[{ColorError}]This software may damage your CNC machine.[/]");
            AnsiConsole.MarkupLine($"[{ColorError}]Use at your own risk.[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[{ColorWarning}]By continuing, you accept full responsibility for any damage[/]");
            AnsiConsole.MarkupLine($"[{ColorWarning}]that may occur to your machine, workpiece, or surroundings.[/]");
            AnsiConsole.WriteLine();

            var result = MenuHelpers.ConfirmOrQuit("Silence this warning next time?", true);
            if (result == null)
            {
                Environment.Exit(0);
            }
            if (result == true)
            {
                AppState.Settings.SilenceExperimentalWarning = true;
                saveSettings();
            }
        }
    }
}
