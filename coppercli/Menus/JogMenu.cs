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
    /// Vi-style: press digit (1-5 or 1-9 depending on mode) then direction.
    /// </summary>
    internal static class JogMenu
    {
        // Pending multiplier for vi-style digit prefix (1-9, default 1)
        private static int _pendingMultiplier = 1;

        public static void Show()
        {
            if (!MenuHelpers.RequireConnection())
            {
                return;
            }

            var machine = AppState.Machine;
            var settings = AppState.Settings;

            // Reset multiplier on entry
            _pendingMultiplier = 1;

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
                    var mode = JogModes[AppState.JogPresetIndex];

                    // Reset cursor to top-left for flicker-free redraw
                    Console.SetCursorPosition(0, 0);

                    // Draw header and status
                    DisplayHelpers.WriteLineTruncated($"{DisplayHelpers.AnsiBoldBlue}Move{DisplayHelpers.AnsiReset}", winWidth);
                    DisplayHelpers.WriteLineTruncated("", winWidth);

                    var statusColor = StatusHelpers.IsProblematicState(machine) ? DisplayHelpers.AnsiRed : DisplayHelpers.AnsiGreen;
                    DisplayHelpers.WriteLineTruncated($"Status: {statusColor}{machine.Status}{DisplayHelpers.AnsiReset}", winWidth);
                    DisplayHelpers.WriteLineTruncated($"Work:    X:{DisplayHelpers.AnsiYellow}{machine.WorkPosition.X,9:F3}{DisplayHelpers.AnsiReset}  Y:{DisplayHelpers.AnsiYellow}{machine.WorkPosition.Y,9:F3}{DisplayHelpers.AnsiReset}  Z:{DisplayHelpers.AnsiYellow}{machine.WorkPosition.Z,9:F3}{DisplayHelpers.AnsiReset}", winWidth);
                    DisplayHelpers.WriteLineTruncated($"Machine: X:{DisplayHelpers.AnsiDim}{machine.MachinePosition.X,9:F3}{DisplayHelpers.AnsiReset}  Y:{DisplayHelpers.AnsiDim}{machine.MachinePosition.Y,9:F3}{DisplayHelpers.AnsiReset}  Z:{DisplayHelpers.AnsiDim}{machine.MachinePosition.Z,9:F3}{DisplayHelpers.AnsiReset}", winWidth);

                    DisplayHelpers.WriteLineTruncated("", winWidth);
                    DisplayHelpers.WriteLineTruncated($"{DisplayHelpers.AnsiBoldCyan}Jog:{DisplayHelpers.AnsiReset} {DisplayHelpers.AnsiGreen}{mode.Name}{DisplayHelpers.AnsiReset} {mode.Feed}mm/min", winWidth);

                    // Show multiplier and resulting distance
                    var distanceStr = mode.FormatDistance(_pendingMultiplier);
                    var multiplierDisplay = _pendingMultiplier > 1
                        ? $"{DisplayHelpers.AnsiBoldGreen}{_pendingMultiplier}{DisplayHelpers.AnsiReset} x {mode.BaseDistance}mm = {DisplayHelpers.AnsiBoldGreen}{distanceStr}{DisplayHelpers.AnsiReset}"
                        : $"{DisplayHelpers.AnsiGreen}{distanceStr}{DisplayHelpers.AnsiReset}";
                    DisplayHelpers.WriteLineTruncated($"  Distance: {multiplierDisplay}", winWidth);

                    DisplayHelpers.WriteLineTruncated($"  [{DisplayHelpers.AnsiCyan}1-{mode.MaxMultiplier}{DisplayHelpers.AnsiReset}] {DisplayHelpers.AnsiCyan}Arrows/HJKL{DisplayHelpers.AnsiReset} X/Y    [{DisplayHelpers.AnsiCyan}1-{mode.MaxMultiplier}{DisplayHelpers.AnsiReset}] {DisplayHelpers.AnsiCyan}W/S{DisplayHelpers.AnsiReset} or {DisplayHelpers.AnsiCyan}PgUp/PgDn{DisplayHelpers.AnsiReset} Z", winWidth);
                    DisplayHelpers.WriteLineTruncated($"  {DisplayHelpers.AnsiCyan}Tab{DisplayHelpers.AnsiReset} - Cycle mode", winWidth);

                    DisplayHelpers.WriteLineTruncated("", winWidth);
                    DisplayHelpers.WriteLineTruncated($"{DisplayHelpers.AnsiBoldCyan}Commands:{DisplayHelpers.AnsiReset}", winWidth);
                    DisplayHelpers.WriteLineTruncated($"  {DisplayHelpers.AnsiCyan}M{DisplayHelpers.AnsiReset} - Home    {DisplayHelpers.AnsiCyan}U{DisplayHelpers.AnsiReset} - Unlock    {DisplayHelpers.AnsiCyan}R{DisplayHelpers.AnsiReset} - Reset    {DisplayHelpers.AnsiCyan}Esc/Q{DisplayHelpers.AnsiReset} - Exit", winWidth);

                    DisplayHelpers.WriteLineTruncated("", winWidth);
                    DisplayHelpers.WriteLineTruncated($"{DisplayHelpers.AnsiBoldCyan}Set Work Zero:{DisplayHelpers.AnsiReset}", winWidth);
                    DisplayHelpers.WriteLineTruncated($"  {DisplayHelpers.AnsiCyan}0{DisplayHelpers.AnsiReset} - Zero All (XYZ)    {DisplayHelpers.AnsiCyan}Z{DisplayHelpers.AnsiReset} - Zero Z only", winWidth);

                    DisplayHelpers.WriteLineTruncated("", winWidth);
                    DisplayHelpers.WriteLineTruncated($"{DisplayHelpers.AnsiBoldCyan}Go to Position:{DisplayHelpers.AnsiReset}", winWidth);
                    DisplayHelpers.WriteLineTruncated($"  {DisplayHelpers.AnsiCyan}N{DisplayHelpers.AnsiReset} - X0 Y0    {DisplayHelpers.AnsiCyan}C{DisplayHelpers.AnsiReset} - Center    {DisplayHelpers.AnsiCyan}T{DisplayHelpers.AnsiReset} - Z+6mm    {DisplayHelpers.AnsiCyan}B{DisplayHelpers.AnsiReset} - Z+1mm    {DisplayHelpers.AnsiCyan}G{DisplayHelpers.AnsiReset} - Z0", winWidth);

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

                    // Tab cycles through jog modes
                    if (key.Key == ConsoleKey.Tab)
                    {
                        AppState.JogPresetIndex = (AppState.JogPresetIndex + 1) % JogModes.Length;
                        _pendingMultiplier = 1; // Reset multiplier on mode change
                        continue;
                    }

                    // Handle digit keys for vi-style multiplier (1-9, but respect MaxMultiplier)
                    int? digit = key.Key switch
                    {
                        ConsoleKey.D1 => 1,
                        ConsoleKey.D2 => 2,
                        ConsoleKey.D3 => 3,
                        ConsoleKey.D4 => 4,
                        ConsoleKey.D5 => 5,
                        ConsoleKey.D6 => 6,
                        ConsoleKey.D7 => 7,
                        ConsoleKey.D8 => 8,
                        ConsoleKey.D9 => 9,
                        _ => null
                    };

                    if (digit.HasValue && digit.Value <= mode.MaxMultiplier)
                    {
                        _pendingMultiplier = digit.Value;
                        continue;
                    }

                    // Handle command keys
                    if (InputHelpers.IsKey(key, ConsoleKey.M, 'm'))
                    {
                        machine.SoftReset();
                        machine.SendLine(CmdHome);
                        AnsiConsole.MarkupLine("[green]Homing...[/]");
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
                        AnsiConsole.MarkupLine("[green]Unlocked[/]");
                        Thread.Sleep(ConfirmationDisplayMs);
                        continue;
                    }
                    if (InputHelpers.IsKey(key, ConsoleKey.R, 'r'))
                    {
                        machine.SoftReset();
                        AnsiConsole.MarkupLine("[yellow]Reset[/]");
                        Thread.Sleep(ConfirmationDisplayMs);
                        continue;
                    }
                    if (InputHelpers.IsKey(key, ConsoleKey.Z, 'z'))
                    {
                        machine.SendLine($"{CmdZeroWorkOffset} Z0");
                        AppState.IsWorkZeroSet = true;
                        AnsiConsole.MarkupLine("[green]Z zeroed[/]");
                        Thread.Sleep(ConfirmationDisplayMs);
                        machine.SendLine($"{CmdRapidMove} Z{RetractZMm}");
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

                        AnsiConsole.MarkupLine("[green]All axes zeroed[/]");
                        Thread.Sleep(ConfirmationDisplayMs);
                        machine.SendLine($"{CmdRapidMove} Z{RetractZMm}");
                        machine.EnableAutoStateClear = false;
                        return;
                    }
                    if (InputHelpers.IsKey(key, ConsoleKey.T, 't'))
                    {
                        machine.SendLine($"{CmdRapidMove} Z{RetractZMm}");
                        continue;
                    }
                    if (InputHelpers.IsKey(key, ConsoleKey.B, 'b'))
                    {
                        machine.SendLine($"{CmdRapidMove} Z{ReferenceZHeightMm}");
                        continue;
                    }
                    if (InputHelpers.IsKey(key, ConsoleKey.G, 'g'))
                    {
                        machine.SendLine($"{CmdRapidMove} Z0");
                        continue;
                    }
                    if (InputHelpers.IsKey(key, ConsoleKey.N, 'n'))
                    {
                        machine.SendLine($"{CmdRapidMove} X0 Y0");
                        continue;
                    }
                    if (InputHelpers.IsKey(key, ConsoleKey.C, 'c'))
                    {
                        var currentFile = AppState.CurrentFile;
                        if (currentFile != null)
                        {
                            double centerX = (currentFile.Min.X + currentFile.Max.X) / 2;
                            double centerY = (currentFile.Min.Y + currentFile.Max.Y) / 2;
                            machine.SendLine($"{CmdRapidMove} X{centerX:F3} Y{centerY:F3}");
                        }
                        continue;
                    }
                    if (InputHelpers.IsKey(key, ConsoleKey.P, 'p'))
                    {
                        ProbeZ();
                        continue;
                    }

                    // Handle jog keys with vi-style multiplier
                    double distance = mode.BaseDistance * _pendingMultiplier;
                    bool jogged = JogHelpers.HandleJogKey(key, machine, mode.Feed, distance);
                    if (jogged)
                    {
                        _pendingMultiplier = 1; // Reset after jog
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
