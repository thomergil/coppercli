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
            AnsiConsole.MarkupLine($"[{ColorBold}]{AppTitle} {AppVersion}[/] - A CLI tool for PCB milling with GRBL");
            AnsiConsole.MarkupLine("By Thomer Gil");
            AnsiConsole.MarkupLine("[link]https://github.com/thomergil/coppercli[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[{ColorBold} {ColorError}]!! EXTREMELY EXPERIMENTAL !![/]");
            AnsiConsole.MarkupLine($"[{ColorError}]This software may damage your CNC machine.[/]");
            AnsiConsole.MarkupLine($"[{ColorError}]Use at your own risk.[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"Based on [{ColorInfo}]OpenCNCPilot[/]");
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
            AnsiConsole.MarkupLine("  - Tool change support (M6) with semi-automatic flow");
            AnsiConsole.MarkupLine("  - Macro system for custom automation scripts");
            AnsiConsole.MarkupLine("  - Proxy mode: serial-to-TCP bridge for remote access");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Report issues: [link]https://github.com/thomergil/coppercli/issues[/]");
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
