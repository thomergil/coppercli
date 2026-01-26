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
            AnsiConsole.Write(new Rule($"[bold blue]About {AppTitle}[/]").RuleStyle("blue"));
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[bold]{AppTitle} {AppVersion}[/] - A CLI tool for PCB milling with GRBL");
            AnsiConsole.MarkupLine("By Thomer Gil");
            AnsiConsole.MarkupLine("[link]https://github.com/thomergil/coppercli[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold red]!! EXTREMELY EXPERIMENTAL !![/]");
            AnsiConsole.MarkupLine("[red]This software may damage your CNC machine.[/]");
            AnsiConsole.MarkupLine("[red]Use at your own risk.[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Based on [cyan]OpenCNCPilot[/]");
            AnsiConsole.MarkupLine("by martin2250: [link]https://github.com/martin2250/OpenCNCPilot[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Features:");
            AnsiConsole.MarkupLine("  - Platform agnostic: runs on Linux, macOS, and Windows");
            AnsiConsole.MarkupLine("  - Keyboard-driven: single-key navigation, no mouse required");
            AnsiConsole.MarkupLine("  - Smart menu defaults: highlights logical next step");
            AnsiConsole.MarkupLine("  - Session persistence: resume interrupted probing sessions");
            AnsiConsole.MarkupLine("  - Auto-detect serial port and baud rate");
            AnsiConsole.MarkupLine("  - Pause/Resume/Emergency Stop during milling (P/R/X)");
            AnsiConsole.MarkupLine("  - 2D position grid visualization while milling");
            AnsiConsole.MarkupLine("  - Surface probing for PCB auto-leveling");
            AnsiConsole.MarkupLine("  - G-code height map compensation");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Report issues: [link]https://github.com/thomergil/coppercli/issues[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[dim]Press any key to return...[/]");
            Console.ReadKey(true);
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
            AnsiConsole.Write(new Rule("[bold red]WARNING[/]").RuleStyle("red"));
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold red]!! EXTREMELY EXPERIMENTAL !![/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[red]This software may damage your CNC machine.[/]");
            AnsiConsole.MarkupLine("[red]Use at your own risk.[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]By continuing, you accept full responsibility for any damage[/]");
            AnsiConsole.MarkupLine("[yellow]that may occur to your machine, workpiece, or surroundings.[/]");
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
