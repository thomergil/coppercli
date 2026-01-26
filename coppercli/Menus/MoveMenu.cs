// Extracted from Program.cs

using coppercli.Core.Util;
using coppercli.Helpers;
using Spectre.Console;
using static coppercli.CliConstants;
using static coppercli.Core.Util.GrblProtocol;

namespace coppercli.Menus
{
    /// <summary>
    /// Move menu for jogging and zeroing the machine.
    /// </summary>
    internal static class MoveMenu
    {
        public static void Show()
        {
            if (!MenuHelpers.RequireConnection())
            {
                return;
            }

            var machine = AppState.Machine;
            var settings = AppState.Settings;

            while (true)
            {
                Console.Clear();
                AnsiConsole.Write(new Rule("[bold blue]Move[/]").RuleStyle("blue"));
                var statusColor = (machine.Status.StartsWith(StatusAlarm) || machine.Status.StartsWith(StatusDoor)) ? "red" : "green";
                AnsiConsole.MarkupLine($"Status: [{statusColor}]{machine.Status}[/]");
                AnsiConsole.MarkupLine($"Position: X:[yellow]{machine.WorkPosition.X:F3}[/] Y:[yellow]{machine.WorkPosition.Y:F3}[/] Z:[yellow]{machine.WorkPosition.Z:F3}[/]");

                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[bold]Jog:[/]");
                AnsiConsole.MarkupLine("  [cyan]Arrow keys[/] - X/Y    [cyan]W/S[/] or [cyan]PgUp/PgDn[/] - Z");
                AnsiConsole.MarkupLine($"  [cyan]Tab[/] - Cycle speed    [green]{JogPresets[AppState.JogPresetIndex].Label}[/]");

                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[bold]Commands:[/]");
                AnsiConsole.MarkupLine("  [cyan]H[/] - Home    [cyan]U[/] - Unlock    [cyan]R[/] - Reset    [cyan]Esc/Q[/] - Exit");

                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[bold]Set Work Zero:[/]");
                AnsiConsole.MarkupLine("  [cyan]0[/] - Zero All (XYZ)    [cyan]Z[/] - Zero Z only");

                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[bold]Go to Position:[/]");
                AnsiConsole.MarkupLine("  [cyan]X[/] - X0 Y0    [cyan]C[/] - Center of G-code    [cyan]6[/] - Z+6mm    [cyan]1[/] - Z+1mm    [cyan]G[/] - Z0");

                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[bold]Probe:[/]");
                AnsiConsole.MarkupLine("  [cyan]P[/] - Find Z (probe down until contact)");
                AnsiConsole.WriteLine();

                var key = Console.ReadKey(true);

                if (InputHelpers.IsExitKey(key))
                {
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
                    if (machine.Status.StartsWith(StatusDoor) || machine.Status.StartsWith(StatusHold))
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
                    AppState.WorkZeroSet = true;
                    AnsiConsole.MarkupLine("[green]Z zeroed[/]");
                    Thread.Sleep(ConfirmationDisplayMs);
                    machine.SendLine($"{CmdRapidMove} Z{SafeZHeightMm}");
                    AnsiConsole.MarkupLine($"[green]Moving to Z+{SafeZHeightMm}mm[/]");
                    Thread.Sleep(CommandDelayMs);
                    return;
                }
                if (InputHelpers.IsKey(key, ConsoleKey.D0, '0'))
                {
                    machine.SendLine($"{CmdZeroWorkOffset} X0 Y0 Z0");
                    AppState.WorkZeroSet = true;

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

        /// <summary>
        /// Performs a single Z probe at current XY position.
        /// </summary>
        private static void ProbeZ()
        {
            var machine = AppState.Machine;
            var settings = AppState.Settings;

            AnsiConsole.MarkupLine($"[yellow]Probing Z at current XY (max depth: {settings.ProbeMaxDepth}mm, feed: {settings.ProbeFeed}mm/min)[/]");
            AnsiConsole.MarkupLine("[dim]Probing...[/]");

            bool completed = false;
            bool success = false;
            double foundZ = 0;

            AppState.SingleProbeCallback = (pos, probeSuccess) =>
            {
                success = probeSuccess;
                foundZ = pos.Z;
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
                    AnsiConsole.MarkupLine("[yellow]Probe cancelled[/]");
                    return;
                }
                Thread.Sleep(StatusPollIntervalMs);
            }

            if (success)
            {
                AnsiConsole.MarkupLine($"[green]Found Z at {foundZ:F3}mm[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[red]Probe failed - no contact[/]");
            }
        }
    }
}
