// Macro execution engine

using coppercli.Core.GCode;
using coppercli.Helpers;
using coppercli.Menus;
using Spectre.Console;
using static coppercli.CliConstants;

namespace coppercli.Macro
{
    /// <summary>
    /// Executes a list of macro commands with TUI display.
    /// </summary>
    internal class MacroRunner
    {
        private readonly List<MacroCommand> _commands;
        private readonly string _macroName;
        private int _currentStep;
        private bool _aborted;

        public MacroRunner(List<MacroCommand> commands, string macroName)
        {
            _commands = commands;
            _macroName = macroName;
            _currentStep = 0;
            _aborted = false;
        }

        /// <summary>
        /// Runs the macro. Returns true if completed successfully, false if aborted.
        /// </summary>
        public bool Run()
        {
            if (_commands.Count == 0)
            {
                AnsiConsole.MarkupLine($"[{ColorWarning}]Macro is empty.[/]");
                return true;
            }

            Console.Clear();
            Console.CursorVisible = false;

            try
            {
                while (_currentStep < _commands.Count && !_aborted)
                {
                    DrawProgress();

                    var command = _commands[_currentStep];
                    bool success = ExecuteCommand(command);

                    if (!success)
                    {
                        if (_aborted)
                        {
                            AnsiConsole.MarkupLine($"\n[{ColorWarning}]Macro aborted by user.[/]");
                        }
                        else
                        {
                            AnsiConsole.MarkupLine($"\n[{ColorError}]Macro failed at step {_currentStep + 1}: {command.DisplayText}[/]");
                        }
                        Console.CursorVisible = true;
                        if (!AppState.MacroMode)
                        {
                            MenuHelpers.ShowPrompt("Macro stopped.");
                        }
                        return false;
                    }

                    _currentStep++;
                }

                // Draw final state
                DrawProgress();
                Console.CursorVisible = true;
                AnsiConsole.MarkupLine($"\n[{ColorSuccess}]Macro completed successfully![/]");
                if (!AppState.MacroMode)
                {
                    MenuHelpers.ShowPrompt("");
                }
                return true;
            }
            finally
            {
                Console.CursorVisible = true;
            }
        }

        /// <summary>
        /// Draws the macro progress display with optional overlay message.
        /// </summary>
        private void DrawProgress(string? overlayMessage = null, string? overlaySubtext = null)
        {
            var (winWidth, winHeight) = DisplayHelpers.GetSafeWindowSize();
            var machine = AppState.Machine;

            Console.SetCursorPosition(0, 0);

            // Header
            DisplayHelpers.WriteLineTruncated($"{DisplayHelpers.AnsiPrompt}Macro: {_macroName}{DisplayHelpers.AnsiReset}", winWidth);
            DisplayHelpers.WriteLineTruncated("", winWidth);

            // Status line
            var statusColor = StatusHelpers.IsProblematicState(machine)
                ? DisplayHelpers.AnsiError
                : DisplayHelpers.AnsiSuccess;
            DisplayHelpers.WriteLineTruncated(
                $"Status: {statusColor}{machine.Status}{DisplayHelpers.AnsiReset}  " +
                $"X:{DisplayHelpers.AnsiWarning}{machine.WorkPosition.X:F3}{DisplayHelpers.AnsiReset} " +
                $"Y:{DisplayHelpers.AnsiWarning}{machine.WorkPosition.Y:F3}{DisplayHelpers.AnsiReset} " +
                $"Z:{DisplayHelpers.AnsiWarning}{machine.WorkPosition.Z:F3}{DisplayHelpers.AnsiReset}",
                winWidth);
            DisplayHelpers.WriteLineTruncated("", winWidth);

            // Calculate how many steps we can show
            int headerLines = 4;
            int footerLines = 3;
            int availableLines = winHeight - headerLines - footerLines;
            int maxVisibleSteps = Math.Max(3, availableLines);

            // Calculate viewport to keep current step visible
            int viewStart = 0;
            if (_commands.Count > maxVisibleSteps)
            {
                // Keep current step roughly centered
                viewStart = Math.Max(0, _currentStep - maxVisibleSteps / 2);
                viewStart = Math.Min(viewStart, _commands.Count - maxVisibleSteps);
            }
            int viewEnd = Math.Min(viewStart + maxVisibleSteps, _commands.Count);

            // Calculate overlay box dimensions if needed
            int boxWidth = 0;
            int boxStartRow = 0;
            int boxEndRow = 0;
            int boxLeftPad = 0;
            if (overlayMessage != null)
            {
                boxWidth = Math.Min(winWidth - 4, Math.Max(40, overlayMessage.Length + 6));
                boxLeftPad = Math.Max(0, (winWidth - boxWidth) / 2);

                // Center vertically in the step list area
                int stepAreaStart = headerLines + (viewStart > 0 ? 1 : 0);
                int stepAreaHeight = viewEnd - viewStart;
                boxStartRow = stepAreaStart + Math.Max(0, (stepAreaHeight - DisplayHelpers.OverlayBoxHeight) / 2);
                boxEndRow = boxStartRow + DisplayHelpers.OverlayBoxHeight - 1;
            }

            int currentRow = headerLines;

            // Show "more above" indicator
            if (viewStart > 0)
            {
                DisplayHelpers.WriteLineTruncated($"{DisplayHelpers.AnsiDim}  ... {viewStart} more above{DisplayHelpers.AnsiReset}", winWidth);
                currentRow++;
            }

            // Draw steps (with overlay if applicable)
            for (int i = viewStart; i < viewEnd; i++)
            {
                var cmd = _commands[i];
                string marker;
                string color;

                if (i < _currentStep)
                {
                    marker = "[done]";
                    color = DisplayHelpers.AnsiSuccess;
                }
                else if (i == _currentStep)
                {
                    marker = "  ==> ";
                    color = DisplayHelpers.AnsiInfo;
                }
                else
                {
                    marker = "      ";
                    color = DisplayHelpers.AnsiDim;
                }

                var stepNum = $"{i + 1,3}.";
                string line = $"{color}{marker} {stepNum} {cmd.DisplayText}{DisplayHelpers.AnsiReset}";

                // Check if this row should have overlay
                if (overlayMessage != null && currentRow >= boxStartRow && currentRow <= boxEndRow)
                {
                    int boxLine = currentRow - boxStartRow;
                    string boxContent = DisplayHelpers.GetOverlayBoxLine(boxLine, boxWidth,
                        overlayMessage, DisplayHelpers.AnsiWarning,
                        overlaySubtext ?? "", DisplayHelpers.AnsiDim);
                    string composited = DisplayHelpers.CompositeOverlay(line, boxContent, boxLeftPad, winWidth);
                    DisplayHelpers.WriteLineTruncated(composited, winWidth);
                }
                else
                {
                    DisplayHelpers.WriteLineTruncated(line, winWidth);
                }
                currentRow++;
            }

            // If overlay extends beyond step list, draw remaining box lines
            if (overlayMessage != null)
            {
                while (currentRow <= boxEndRow)
                {
                    int boxLine = currentRow - boxStartRow;
                    string boxContent = DisplayHelpers.GetOverlayBoxLine(boxLine, boxWidth,
                        overlayMessage, DisplayHelpers.AnsiWarning,
                        overlaySubtext ?? "", DisplayHelpers.AnsiDim);
                    // CompositeOverlay handles empty margin lines (returns background)
                    string composited = DisplayHelpers.CompositeOverlay("", boxContent, boxLeftPad, winWidth);
                    DisplayHelpers.WriteLineTruncated(composited, winWidth);
                    currentRow++;
                }
            }

            // Original code continues below with "more below" and footer
            // Show "more below" indicator
            if (viewEnd < _commands.Count)
            {
                DisplayHelpers.WriteLineTruncated($"{DisplayHelpers.AnsiDim}  ... {_commands.Count - viewEnd} more below{DisplayHelpers.AnsiReset}", winWidth);
            }

            // Footer
            DisplayHelpers.WriteLineTruncated("", winWidth);
            if (overlayMessage == null)
            {
                DisplayHelpers.WriteLineTruncated($"{DisplayHelpers.AnsiDim}Press Escape to abort macro{DisplayHelpers.AnsiReset}", winWidth);
            }
        }

        /// <summary>
        /// Executes a single command. Returns true on success, false on failure.
        /// </summary>
        private bool ExecuteCommand(MacroCommand command)
        {
            var machine = AppState.Machine;

            // Check for abort before each command
            if (CheckAbort())
            {
                return false;
            }

            switch (command.Type)
            {
                case MacroCommandType.Load:
                    return ExecuteLoad(command.Args);

                case MacroCommandType.Jog:
                    JogMenu.Show();
                    return !CheckAbort();

                case MacroCommandType.Home:
                    return ExecuteHome();

                case MacroCommandType.Safe:
                    MachineCommands.MoveToSafeHeight(machine, RetractZMm);
                    return WaitForIdle();

                case MacroCommandType.Zero:
                    return ExecuteZero(command.Args);

                case MacroCommandType.Unlock:
                    MachineCommands.Unlock(machine);
                    Thread.Sleep(CommandDelayMs);
                    return true;

                case MacroCommandType.ProbeZ:
                    return ExecuteProbeZ();

                case MacroCommandType.ProbeGrid:
                    ProbeMenu.Show();
                    return !CheckAbort();

                case MacroCommandType.ProbeApply:
                    return ExecuteProbeApply();

                case MacroCommandType.Mill:
                    MillMenu.Show();
                    return !CheckAbort();

                case MacroCommandType.Prompt:
                    return ExecutePrompt(command.Args);

                case MacroCommandType.Confirm:
                    return ExecuteConfirm(command.Args);

                case MacroCommandType.Echo:
                    if (command.Args.Length > 0)
                    {
                        AnsiConsole.MarkupLine($"[{ColorInfo}]{Markup.Escape(command.Args[0])}[/]");
                    }
                    return true;

                case MacroCommandType.Wait:
                    return ExecuteWait(command.Args);

                default:
                    AnsiConsole.MarkupLine($"[{ColorError}]Unknown command: {command.Type}[/]");
                    return false;
            }
        }

        private bool ExecuteLoad(string[] args)
        {
            if (args.Length == 0)
            {
                AnsiConsole.MarkupLine($"[{ColorError}]load command requires a filename[/]");
                return false;
            }

            string path = args[0];

            if (!File.Exists(path))
            {
                AnsiConsole.MarkupLine($"[{ColorError}]File not found: {Markup.Escape(path)}[/]");
                return false;
            }

            try
            {
                var file = GCodeFile.Load(path);
                AppState.CurrentFile = file;
                AppState.Machine.SetFile(file.GetGCode());
                AppState.AreProbePointsApplied = false;
                return true;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[{ColorError}]Error loading file: {Markup.Escape(ex.Message)}[/]");
                return false;
            }
        }

        private bool ExecuteHome()
        {
            var machine = AppState.Machine;
            MachineCommands.Home(machine);

            // Wait for homing to complete
            var deadline = DateTime.Now.AddMilliseconds(HomingTimeoutMs);
            while (DateTime.Now < deadline)
            {
                if (CheckAbort())
                {
                    return false;
                }

                if (StatusHelpers.IsIdle(machine))
                {
                    AppState.IsHomed = true;
                    return true;
                }

                if (StatusHelpers.IsAlarm(machine))
                {
                    AnsiConsole.MarkupLine($"[{ColorError}]Homing failed - machine in alarm state[/]");
                    return false;
                }

                Thread.Sleep(StatusPollIntervalMs);
            }

            AnsiConsole.MarkupLine($"[{ColorError}]Homing timed out[/]");
            return false;
        }

        private bool ExecuteZero(string[] args)
        {
            var machine = AppState.Machine;
            string axes = args.Length > 0 ? args[0].ToUpper() : "XYZ";

            // Build the axis string for G10 L20 P1
            string axisCmd = "";
            bool zeroingZ = false;
            if (axes.Contains('X'))
            {
                axisCmd += "X0";
            }
            if (axes.Contains('Y'))
            {
                axisCmd += "Y0";
            }
            if (axes.Contains('Z'))
            {
                axisCmd += "Z0";
                zeroingZ = true;
            }

            if (string.IsNullOrEmpty(axisCmd))
            {
                axisCmd = "X0Y0Z0";
                zeroingZ = true;
            }

            MachineCommands.ZeroWorkOffset(machine, axisCmd);
            AppState.IsWorkZeroSet = true;

            // If zeroing Z, move to safe height (matches JogMenu behavior)
            if (zeroingZ)
            {
                Thread.Sleep(CommandDelayMs);
                MachineCommands.MoveToSafeHeight(machine, RetractZMm);
                return WaitForIdle();
            }

            Thread.Sleep(CommandDelayMs);
            return true;
        }

        private bool ExecuteProbeZ()
        {
            var machine = AppState.Machine;

            if (!MenuHelpers.RequireConnection())
            {
                return false;
            }

            // Use the single-point probe mechanism
            // Spindle descends until it touches the surface, then stops
            // User should call "zero xyz" after this to set work zero
            AppState.SingleProbing = true;
            bool probeSuccess = false;
            bool probeDone = false;

            AppState.SingleProbeCallback = (pos, success) =>
            {
                probeSuccess = success;
                probeDone = true;
                // Do NOT auto-zero - user will call "zero xyz" explicitly
            };

            // Start probe
            machine.ProbeStart();
            var settings = AppState.Settings;
            MachineCommands.ProbeZ(machine, settings.ProbeMaxDepth, settings.ProbeFeed);

            // Wait for probe to complete
            var deadline = DateTime.Now.AddMilliseconds(ZHeightWaitTimeoutMs);
            while (!probeDone && DateTime.Now < deadline)
            {
                if (CheckAbort())
                {
                    machine.ProbeStop();
                    AppState.SingleProbing = false;
                    AppState.SingleProbeCallback = null;
                    return false;
                }
                Thread.Sleep(StatusPollIntervalMs);
            }

            AppState.SingleProbing = false;
            AppState.SingleProbeCallback = null;

            if (!probeDone)
            {
                AnsiConsole.MarkupLine($"[{ColorError}]Probe timed out[/]");
                return false;
            }

            if (!probeSuccess)
            {
                AnsiConsole.MarkupLine($"[{ColorError}]Probe failed - no contact[/]");
                return false;
            }

            // Stay at probe position - user will call "zero xyz" then "safe"
            return true;
        }

        private bool ExecuteProbeApply()
        {
            var probePoints = AppState.ProbePoints;
            if (probePoints == null)
            {
                AnsiConsole.MarkupLine($"[{ColorError}]No probe data available[/]");
                return false;
            }

            if (probePoints.NotProbed.Count > 0)
            {
                AnsiConsole.MarkupLine($"[{ColorError}]Probe grid is incomplete[/]");
                return false;
            }

            AppState.ApplyProbeData();
            return true;
        }

        private bool ExecutePrompt(string[] args)
        {
            string message = args.Length > 0 ? args[0] : "Press Enter to continue";

            // Draw progress with overlay
            DrawProgress(message, "Enter=Continue  Escape=Abort");

            while (true)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter)
                {
                    return true;
                }
                if (key.Key == ConsoleKey.Escape)
                {
                    _aborted = true;
                    return false;
                }
            }
        }

        private bool ExecuteConfirm(string[] args)
        {
            string message = args.Length > 0 ? args[0] : "Continue?";

            // Draw progress with overlay
            DrawProgress(message, "Y=Yes  N=No (aborts)");

            while (true)
            {
                var key = Console.ReadKey(true);
                char c = char.ToLower(key.KeyChar);

                if (key.Key == ConsoleKey.Y || c == 'y' || key.Key == ConsoleKey.Enter)
                {
                    return true;
                }
                if (key.Key == ConsoleKey.N || c == 'n' || key.Key == ConsoleKey.Escape)
                {
                    _aborted = true;
                    return false;
                }
            }
        }

        private bool ExecuteWait(string[] args)
        {
            // Wait for machine to reach idle state
            return WaitForIdle();
        }

        private bool WaitForIdle()
        {
            var machine = AppState.Machine;
            var deadline = DateTime.Now.AddMilliseconds(IdleWaitTimeoutMs);

            while (DateTime.Now < deadline)
            {
                if (CheckAbort())
                {
                    return false;
                }

                if (StatusHelpers.IsIdle(machine))
                {
                    return true;
                }

                if (StatusHelpers.IsAlarm(machine))
                {
                    AnsiConsole.MarkupLine($"[{ColorError}]Machine entered alarm state[/]");
                    return false;
                }

                Thread.Sleep(StatusPollIntervalMs);
            }

            // If we got here, we timed out but might still be OK
            return StatusHelpers.IsIdle(machine);
        }

        private bool CheckAbort()
        {
            if (_aborted)
            {
                return true;
            }

            // Check for Escape key
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Escape)
                {
                    _aborted = true;
                    return true;
                }
            }

            return false;
        }
    }
}
