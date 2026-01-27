// Extracted from Program.cs

using coppercli.Core.Util;
using coppercli.Helpers;
using Spectre.Console;
using static coppercli.CliConstants;
using static coppercli.Core.Util.GrblProtocol;

namespace coppercli.Menus
{
    /// <summary>
    /// Jog menu for jogging and zeroing the machine.
    /// </summary>
    internal static class JogMenu
    {
        public static void Show()
        {
            if (!MenuHelpers.RequireConnection())
            {
                return;
            }

            var machine = AppState.Machine;
            var settings = AppState.Settings;

            // Enable auto-clear while in jog menu (user can see status updates)
            machine.EnableAutoStateClear = true;

            // Clear once, then use flicker-free redraws
            Console.Clear();
            Console.CursorVisible = false;

            try
            {
                while (true)
                {
                    var (winWidth, _) = DisplayHelpers.GetSafeWindowSize();

                    // Reset cursor to top-left for flicker-free redraw
                    Console.SetCursorPosition(0, 0);

                    // Draw header and status
                    DisplayHelpers.WriteLineTruncated($"{DisplayHelpers.AnsiBoldBlue}Move{DisplayHelpers.AnsiReset}", winWidth);
                    DisplayHelpers.WriteLineTruncated("", winWidth);

                    var statusColor = StatusHelpers.IsProblematicState(machine) ? DisplayHelpers.AnsiRed : DisplayHelpers.AnsiGreen;
                    DisplayHelpers.WriteLineTruncated($"Status: {statusColor}{machine.Status}{DisplayHelpers.AnsiReset}", winWidth);
                    DisplayHelpers.WriteLineTruncated($"Position: X:{DisplayHelpers.AnsiYellow}{machine.WorkPosition.X:F3}{DisplayHelpers.AnsiReset} Y:{DisplayHelpers.AnsiYellow}{machine.WorkPosition.Y:F3}{DisplayHelpers.AnsiReset} Z:{DisplayHelpers.AnsiYellow}{machine.WorkPosition.Z:F3}{DisplayHelpers.AnsiReset}", winWidth);

                    DisplayHelpers.WriteLineTruncated("", winWidth);
                    DisplayHelpers.WriteLineTruncated($"{DisplayHelpers.AnsiBoldCyan}Jog:{DisplayHelpers.AnsiReset}", winWidth);
                    DisplayHelpers.WriteLineTruncated($"  {DisplayHelpers.AnsiCyan}Arrow keys{DisplayHelpers.AnsiReset} - X/Y    {DisplayHelpers.AnsiCyan}W/S{DisplayHelpers.AnsiReset} or {DisplayHelpers.AnsiCyan}PgUp/PgDn{DisplayHelpers.AnsiReset} - Z", winWidth);
                    DisplayHelpers.WriteLineTruncated($"  {DisplayHelpers.AnsiCyan}Tab{DisplayHelpers.AnsiReset} - Cycle speed    {DisplayHelpers.AnsiGreen}{JogPresets[AppState.JogPresetIndex].Label}{DisplayHelpers.AnsiReset}", winWidth);

                    DisplayHelpers.WriteLineTruncated("", winWidth);
                    DisplayHelpers.WriteLineTruncated($"{DisplayHelpers.AnsiBoldCyan}Commands:{DisplayHelpers.AnsiReset}", winWidth);
                    DisplayHelpers.WriteLineTruncated($"  {DisplayHelpers.AnsiCyan}H{DisplayHelpers.AnsiReset} - Home    {DisplayHelpers.AnsiCyan}U{DisplayHelpers.AnsiReset} - Unlock    {DisplayHelpers.AnsiCyan}R{DisplayHelpers.AnsiReset} - Reset    {DisplayHelpers.AnsiCyan}Esc/Q{DisplayHelpers.AnsiReset} - Exit", winWidth);

                    DisplayHelpers.WriteLineTruncated("", winWidth);
                    DisplayHelpers.WriteLineTruncated($"{DisplayHelpers.AnsiBoldCyan}Set Work Zero:{DisplayHelpers.AnsiReset}", winWidth);
                    DisplayHelpers.WriteLineTruncated($"  {DisplayHelpers.AnsiCyan}0{DisplayHelpers.AnsiReset} - Zero All (XYZ)    {DisplayHelpers.AnsiCyan}Z{DisplayHelpers.AnsiReset} - Zero Z only", winWidth);

                    DisplayHelpers.WriteLineTruncated("", winWidth);
                    DisplayHelpers.WriteLineTruncated($"{DisplayHelpers.AnsiBoldCyan}Go to Position:{DisplayHelpers.AnsiReset}", winWidth);
                    DisplayHelpers.WriteLineTruncated($"  {DisplayHelpers.AnsiCyan}X{DisplayHelpers.AnsiReset} - X0 Y0    {DisplayHelpers.AnsiCyan}C{DisplayHelpers.AnsiReset} - Center of G-code    {DisplayHelpers.AnsiCyan}6{DisplayHelpers.AnsiReset} - Z+6mm    {DisplayHelpers.AnsiCyan}1{DisplayHelpers.AnsiReset} - Z+1mm    {DisplayHelpers.AnsiCyan}G{DisplayHelpers.AnsiReset} - Z0", winWidth);

                    DisplayHelpers.WriteLineTruncated("", winWidth);
                    DisplayHelpers.WriteLineTruncated($"{DisplayHelpers.AnsiBoldCyan}Probe:{DisplayHelpers.AnsiReset}", winWidth);
                    DisplayHelpers.WriteLineTruncated($"  {DisplayHelpers.AnsiCyan}P{DisplayHelpers.AnsiReset} - Find Z (probe down until contact)", winWidth);
                    DisplayHelpers.WriteLineTruncated("", winWidth);

                    var keyOrNull = InputHelpers.ReadKeyPolling();
                    if (keyOrNull == null)
                    {
                        continue; // Status changed, redraw screen
                    }
                    var key = keyOrNull.Value;

                    if (InputHelpers.IsExitKey(key))
                    {
                        machine.EnableAutoStateClear = false;
                        return;
                    }

                    // Tab cycles through jog speeds
                    if (key.Key == ConsoleKey.Tab)
                    {
                        AppState.JogPresetIndex = (AppState.JogPresetIndex + 1) % JogPresets.Length;
                        continue;
                    }

                    // Handle command keys
                    if (InputHelpers.IsKey(key, ConsoleKey.H, 'h'))
                    {
                        machine.SoftReset();
                        machine.SendLine(CmdHome);
                        AnsiConsole.MarkupLine("[green]Home All command sent[/]");
                        Thread.Sleep(ConfirmationDisplayMs);
                        continue;
                    }
                    if (InputHelpers.IsKey(key, ConsoleKey.U, 'u'))
                    {
                        machine.SendLine(CmdUnlock);
                        if (StatusHelpers.IsDoor(machine) || StatusHelpers.IsHold(machine))
                        {
                            machine.SendLine(CycleStart.ToString());
                        }
                        AnsiConsole.MarkupLine("[green]Unlock command sent[/]");
                        Thread.Sleep(CommandDelayMs);
                        continue;
                    }
                    if (InputHelpers.IsKey(key, ConsoleKey.R, 'r'))
                    {
                        machine.SoftReset();
                        AnsiConsole.MarkupLine("[yellow]Soft reset sent[/]");
                        Thread.Sleep(CommandDelayMs);
                        continue;
                    }
                    if (InputHelpers.IsKey(key, ConsoleKey.Z, 'z'))
                    {
                        machine.SendLine($"{CmdZeroWorkOffset} Z0");
                        AppState.IsWorkZeroSet = true;
                        AnsiConsole.MarkupLine("[green]Z zeroed[/]");
                        Thread.Sleep(ConfirmationDisplayMs);
                        machine.SendLine($"{CmdRapidMove} Z{SafeZHeightMm}");
                        AnsiConsole.MarkupLine($"[green]Moving to Z+{SafeZHeightMm}mm[/]");
                        Thread.Sleep(CommandDelayMs);
                        machine.EnableAutoStateClear = false;
                        return;
                    }
                    if (InputHelpers.IsKey(key, ConsoleKey.D0, '0'))
                    {
                        machine.SendLine($"{CmdZeroWorkOffset} X0 Y0 Z0");
                        AppState.IsWorkZeroSet = true;

                        // Store work zero in session
                        var session = AppState.Session;
                        var pos = machine.WorkPosition;
                        session.WorkZeroX = pos.X;
                        session.WorkZeroY = pos.Y;
                        session.WorkZeroZ = pos.Z;
                        session.HasStoredWorkZero = true;
                        Persistence.SaveSession();

                        AnsiConsole.MarkupLine("[green]All axes zeroed (work zero set)[/]");
                        Thread.Sleep(ConfirmationDisplayMs);
                        machine.SendLine($"{CmdRapidMove} Z{SafeZHeightMm}");
                        AnsiConsole.MarkupLine($"[green]Moving to Z+{SafeZHeightMm}mm[/]");
                        Thread.Sleep(CommandDelayMs);
                        machine.EnableAutoStateClear = false;
                        return;
                    }
                    if (InputHelpers.IsKey(key, ConsoleKey.D6, '6'))
                    {
                        machine.SendLine($"{CmdRapidMove} Z{SafeZHeightMm}");
                        AnsiConsole.MarkupLine($"[green]Moving to Z+{SafeZHeightMm}mm[/]");
                        Thread.Sleep(CommandDelayMs);
                        continue;
                    }
                    if (InputHelpers.IsKey(key, ConsoleKey.D1, '1'))
                    {
                        machine.SendLine($"{CmdRapidMove} Z{ReferenceZHeightMm}");
                        AnsiConsole.MarkupLine($"[green]Moving to Z+{ReferenceZHeightMm}mm[/]");
                        Thread.Sleep(CommandDelayMs);
                        continue;
                    }
                    if (InputHelpers.IsKey(key, ConsoleKey.G, 'g'))
                    {
                        machine.SendLine($"{CmdRapidMove} Z0");
                        AnsiConsole.MarkupLine("[green]Moving to Z0[/]");
                        Thread.Sleep(CommandDelayMs);
                        continue;
                    }
                    if (InputHelpers.IsKey(key, ConsoleKey.X, 'x'))
                    {
                        machine.SendLine($"{CmdRapidMove} X0 Y0");
                        AnsiConsole.MarkupLine("[green]Moving to X0 Y0[/]");
                        Thread.Sleep(CommandDelayMs);
                        continue;
                    }
                    if (InputHelpers.IsKey(key, ConsoleKey.C, 'c'))
                    {
                        var currentFile = AppState.CurrentFile;
                        if (currentFile == null)
                        {
                            AnsiConsole.MarkupLine("[red]No G-code file loaded[/]");
                            Thread.Sleep(ConfirmationDisplayMs);
                        }
                        else
                        {
                            double centerX = (currentFile.Min.X + currentFile.Max.X) / 2;
                            double centerY = (currentFile.Min.Y + currentFile.Max.Y) / 2;
                            machine.SendLine($"{CmdRapidMove} X{centerX:F3} Y{centerY:F3}");
                            AnsiConsole.MarkupLine($"[green]Moving to center X{centerX:F3} Y{centerY:F3}[/]");
                            Thread.Sleep(CommandDelayMs);
                        }
                        continue;
                    }
                    if (InputHelpers.IsKey(key, ConsoleKey.P, 'p'))
                    {
                        ProbeZ();
                        Thread.Sleep(ConfirmationDisplayMs);
                        continue;
                    }

                    // Get current jog preset
                    var (feed, distance, _) = JogPresets[AppState.JogPresetIndex];

                    bool jogged = false;
                    switch (key.Key)
                    {
                        case ConsoleKey.UpArrow: machine.Jog('Y', distance, feed); jogged = true; break;
                        case ConsoleKey.DownArrow: machine.Jog('Y', -distance, feed); jogged = true; break;
                        case ConsoleKey.LeftArrow: machine.Jog('X', -distance, feed); jogged = true; break;
                        case ConsoleKey.RightArrow: machine.Jog('X', distance, feed); jogged = true; break;
                        case ConsoleKey.PageUp: machine.Jog('Z', distance, feed); jogged = true; break;
                        case ConsoleKey.PageDown: machine.Jog('Z', -distance, feed); jogged = true; break;
                    }
                    // Handle W/S for Z jog
                    if (!jogged && InputHelpers.IsKey(key, ConsoleKey.W, 'w'))
                    {
                        machine.Jog('Z', distance, feed);
                        jogged = true;
                    }
                    if (!jogged && InputHelpers.IsKey(key, ConsoleKey.S, 's'))
                    {
                        machine.Jog('Z', -distance, feed);
                        jogged = true;
                    }

                    if (jogged)
                    {
                        Thread.Sleep(JogPollIntervalMs);
                        InputHelpers.FlushKeyboard();
                    }
                }
            }
            finally
            {
                Console.CursorVisible = true;
            }
        }

        /// <summary>
        /// Performs a single Z probe at current XY position.
        /// </summary>
        private static void ProbeZ()
        {
            var machine = AppState.Machine;
            var settings = AppState.Settings;

            bool completed = false;

            AppState.SingleProbeCallback = (pos, probeSuccess) =>
            {
                completed = true;
            };

            AppState.SingleProbing = true;
            machine.ProbeStart();
            machine.SendLine(CmdAbsolute);
            machine.SendLine($"{CmdProbeToward} Z-{settings.ProbeMaxDepth:F3} F{settings.ProbeFeed:F1}");

            while (!completed)
            {
                if (Console.KeyAvailable && InputHelpers.IsExitKey(Console.ReadKey(true)))
                {
                    AppState.SingleProbing = false;
                    machine.ProbeStop();
                    machine.FeedHold();
                    return;
                }
                Thread.Sleep(StatusPollIntervalMs);
            }
        }
    }
}
