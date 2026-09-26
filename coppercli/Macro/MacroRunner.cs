using coppercli.Core.Controllers;
using coppercli.Core.GCode;
using coppercli.Core.Util;
using coppercli.Helpers;
using coppercli.Menus;
using Spectre.Console;
using static coppercli.CliConstants;
using static coppercli.Core.Util.Constants;

namespace coppercli.Macro
{
    internal class MacroRunner
    {
        // HeaderLines and FooterLines must match the rows DrawProgress writes above and below
        // the step list, because the viewport height is what is left over after both.
        private const int HeaderLines = 4;
        private const int FooterLines = 3;
        private const int MinVisibleSteps = 3;

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

        private void DrawProgress(string? overlayMessage = null, string? overlaySubtext = null)
        {
            var (winWidth, winHeight) = DisplayHelpers.GetSafeWindowSize();
            var machine = AppState.Machine;

            Console.SetCursorPosition(0, 0);

            DisplayHelpers.WriteLineTruncated($"{DisplayHelpers.AnsiPrompt}Macro: {_macroName}{DisplayHelpers.AnsiReset}", winWidth);
            DisplayHelpers.WriteLineTruncated("", winWidth);

            var activity = MachineWait.GetActivity(machine);
            var statusColor = MachineWait.IsUnavailable(activity)
                ? DisplayHelpers.AnsiError
                : DisplayHelpers.AnsiSuccess;
            DisplayHelpers.WriteLineTruncated(
                $"Status: {statusColor}{DisplayHelpers.GetActivityText(activity, machine.Status)}{DisplayHelpers.AnsiReset}  " +
                $"X:{DisplayHelpers.AnsiWarning}{machine.WorkPosition.X:F3}{DisplayHelpers.AnsiReset} " +
                $"Y:{DisplayHelpers.AnsiWarning}{machine.WorkPosition.Y:F3}{DisplayHelpers.AnsiReset} " +
                $"Z:{DisplayHelpers.AnsiWarning}{machine.WorkPosition.Z:F3}{DisplayHelpers.AnsiReset}",
                winWidth);
            DisplayHelpers.WriteLineTruncated("", winWidth);

            int availableLines = winHeight - HeaderLines - FooterLines;
            int maxVisibleSteps = Math.Max(MinVisibleSteps, availableLines);

            int viewStart = 0;
            if (_commands.Count > maxVisibleSteps)
            {
                viewStart = Math.Max(0, _currentStep - maxVisibleSteps / 2);
                viewStart = Math.Min(viewStart, _commands.Count - maxVisibleSteps);
            }
            int viewEnd = Math.Min(viewStart + maxVisibleSteps, _commands.Count);

            string[] overlayLines = Array.Empty<string>();
            string[] overlayColors = Array.Empty<string>();
            int boxWidth = 0;
            int boxStartRow = 0;
            int boxEndRow = 0;
            int boxLeftPad = 0;
            if (overlayMessage != null)
            {
                (overlayLines, overlayColors) = DisplayHelpers.BuildOverlayContent(
                    overlayMessage, overlaySubtext, DisplayHelpers.AnsiWarning, winWidth);

                boxWidth = DisplayHelpers.CalculateOverlayBoxWidth(overlayLines, winWidth);
                boxLeftPad = (winWidth - boxWidth) / 2;

                int boxHeight = DisplayHelpers.CalculateOverlayBoxHeight(overlayLines);
                int stepAreaStart = HeaderLines + (viewStart > 0 ? 1 : 0);
                int stepAreaHeight = viewEnd - viewStart;
                boxStartRow = stepAreaStart + Math.Max(0, (stepAreaHeight - boxHeight) / 2);
                boxEndRow = boxStartRow + boxHeight - 1;
            }

            string OverlayRow(string background, int row) => DisplayHelpers.CompositeOverlay(
                background,
                DisplayHelpers.GetOverlayBoxLine(row - boxStartRow, boxWidth, overlayLines, overlayColors),
                boxLeftPad,
                winWidth);

            int currentRow = HeaderLines;

            if (viewStart > 0)
            {
                DisplayHelpers.WriteLineTruncated($"{DisplayHelpers.AnsiDim}  ... {viewStart} more above{DisplayHelpers.AnsiReset}", winWidth);
                currentRow++;
            }

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

                if (overlayMessage != null && currentRow >= boxStartRow && currentRow <= boxEndRow)
                {
                    DisplayHelpers.WriteLineTruncated(OverlayRow(line, currentRow), winWidth);
                }
                else
                {
                    DisplayHelpers.WriteLineTruncated(line, winWidth);
                }
                currentRow++;
            }

            if (overlayMessage != null)
            {
                while (currentRow <= boxEndRow)
                {
                    // A box row that is all margin composites onto nothing, so the empty
                    // background is enough.
                    DisplayHelpers.WriteLineTruncated(OverlayRow(string.Empty, currentRow), winWidth);
                    currentRow++;
                }
            }

            if (viewEnd < _commands.Count)
            {
                DisplayHelpers.WriteLineTruncated($"{DisplayHelpers.AnsiDim}  ... {_commands.Count - viewEnd} more below{DisplayHelpers.AnsiReset}", winWidth);
            }

            DisplayHelpers.WriteLineTruncated("", winWidth);
            if (overlayMessage == null)
            {
                DisplayHelpers.WriteLineTruncated($"{DisplayHelpers.AnsiDim}Press Escape to abort macro{DisplayHelpers.AnsiReset}", winWidth);
            }
        }

        private bool ExecuteCommand(MacroCommand command)
        {
            var machine = AppState.Machine;

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
                    MachineCommands.MoveToSafeHeight(machine, Constants.RetractZMm);
                    return WaitForIdle();

                case MacroCommandType.Zero:
                    return ExecuteZero(command.Args);

                case MacroCommandType.Unlock:
                    return ReportFailure(MachineCommands.Unlock(machine));

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
                string? refused = AppState.LoadGCodeIntoMachine(file).Refused;
                if (refused != null)
                {
                    Logger.Log("Macro: {0}", refused);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                MenuHelpers.ShowFailure(CliConstants.FailedLoadingTheFile, ex);
                return false;
            }
        }

        private bool ExecuteHome() =>
            ReportFailure(MachineCommands.HomeAndWait(AppState.Machine).FailureMessage);

        /// <returns>True if there was no failure.</returns>
        private static bool ReportFailure(string? failure)
        {
            if (failure != null)
            {
                AnsiConsole.MarkupLine($"[{ColorError}]{Markup.Escape(failure)}[/]");
            }
            return failure == null;
        }

        private bool ExecuteZero(string[] args)
        {
            var machine = AppState.Machine;
            string axes = args.Length > 0 ? args[0].ToUpper() : "XYZ";

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

            var zeroed = MachineCommands.SetWorkZeroAndWait(machine, axisCmd);
            if (!ReportFailure(zeroed.Refused))
            {
                return false;
            }

            // The rest of the macro would cut with corrections that do not match the origin.
            if (zeroed.Outcome.LeftTheGCodeWrong())
            {
                AnsiConsole.MarkupLine($"[{ColorError}]{MacroErrorHeightMapWrong}[/]");
                return false;
            }

            // Zeroing Z leaves the tool down at the workpiece, so retract as `JogMenu` does
            // after the same move.
            if (zeroingZ)
            {
                MachineCommands.MoveToSafeHeight(machine, Constants.RetractZMm);
                return WaitForIdle();
            }

            return true;
        }

        private bool ExecuteProbeZ()
        {
            var machine = AppState.Machine;

            if (!MenuHelpers.RequireConnection())
            {
                return false;
            }

            var controller = AppState.Probe;

            if (controller.IsActive)
            {
                AnsiConsole.MarkupLine($"[{ColorError}]{ProbeErrorAlreadyRunning}[/]");
                return false;
            }

            controller.Options = ProbeOptions.FromSettings(AppState.Settings);

            // Probing descends at the probe feed and can take minutes, so the macro watches for
            // the abort key rather than blocking. Only a soft reset stops a G38.2 already
            // running; cancelling the token abandons the wait and leaves the tool descending.
            using var cts = new CancellationTokenSource();
            var probeTask = Task.Run(() => controller.ProbeZSingleAsync(cts.Token));

            while (!probeTask.IsCompleted)
            {
                if (CheckAbort())
                {
                    cts.Cancel();
                    machine.SoftReset();
                    return false;
                }

                Thread.Sleep(StatusPollIntervalMs);
            }

            bool probeSuccess;
            try
            {
                probeSuccess = probeTask.GetAwaiter().GetResult().Success;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (TimeoutException)
            {
                AnsiConsole.MarkupLine($"[{ColorError}]{ControllerConstants.ErrorProbeTimeout}[/]");
                return false;
            }

            if (!probeSuccess)
            {
                AnsiConsole.MarkupLine($"[{ColorError}]{ControllerConstants.ErrorProbeNoContact}[/]");
                return false;
            }

            // The tool stays at the probe position, for a macro that goes on to "zero xyz"
            // and then "safe".
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

            if (!probePoints.HasCompleteData)
            {
                AnsiConsole.MarkupLine($"[{ColorError}]Probe grid is incomplete[/]");
                return false;
            }

            // "probe apply" is for reusing a grid on the same board. On a different board, or
            // after the origin moved, it would cut to heights measured somewhere else.
            var applicability = AppState.GetProbeApplicability();

            if (!applicability.IsUsable())
            {
                string why = AppState.GetInapplicableReason(
                    applicability, probePoints.Context.SourceFile);
                AnsiConsole.MarkupLine($"[{ColorError}]The height map cannot be applied: {why}[/]");
                return false;
            }

            // A failure here fails the macro: milling a warped board with no height
            // compensation is what this command prevents.
            string? notApplied = AppState.ApplyProbeData();
            if (notApplied != null)
            {
                AnsiConsole.MarkupLine($"[{ColorError}]{Markup.Escape(notApplied)}[/]");
                return false;
            }

            return true;
        }

        private bool ExecutePrompt(string[] args)
        {
            string message = args.Length > 0 ? args[0] : PromptEnter;

            DrawProgress(message, "Enter=Continue  Escape=Abort");

            while (true)
            {
                var key = Console.ReadKey(true);
                if (InputHelpers.IsEnterKey(key))
                {
                    return true;
                }
                if (InputHelpers.IsEscapeKey(key))
                {
                    _aborted = true;
                    return false;
                }
            }
        }

        private bool ExecuteConfirm(string[] args)
        {
            string message = args.Length > 0 ? args[0] : "Continue?";

            DrawProgress(message, "Y=Yes  N=No (aborts)");

            while (true)
            {
                var key = Console.ReadKey(true);

                if (InputHelpers.IsKey(key, ConsoleKey.Y) || InputHelpers.IsEnterKey(key))
                {
                    return true;
                }
                if (InputHelpers.IsKey(key, ConsoleKey.N) || InputHelpers.IsEscapeKey(key))
                {
                    _aborted = true;
                    return false;
                }
            }
        }

        private bool ExecuteWait(string[] args)
        {
            return WaitForIdle();
        }

        private bool WaitForIdle()
        {
            var machine = AppState.Machine;
            // Stopwatch, not the wall clock: an NTP or daylight-saving step would end this
            // wait at once or never.
            var elapsed = System.Diagnostics.Stopwatch.StartNew();

            while (elapsed.ElapsedMilliseconds < IdleWaitTimeoutMs)
            {
                if (CheckAbort())
                {
                    return false;
                }

                if (MachineWait.IsIdle(machine))
                {
                    return true;
                }

                if (MachineWait.IsAlarm(machine))
                {
                    AnsiConsole.MarkupLine($"[{ColorError}]Machine entered alarm state[/]");
                    return false;
                }

                Thread.Sleep(StatusPollIntervalMs);
            }

            return MachineWait.IsIdle(machine);
        }

        private bool CheckAbort()
        {
            if (_aborted)
            {
                return true;
            }

            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);
                if (InputHelpers.IsEscapeKey(key))
                {
                    _aborted = true;
                    return true;
                }
            }

            return false;
        }
    }
}
