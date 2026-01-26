// Extracted from Program.cs

using coppercli.Core.Communication;
using coppercli.Helpers;
using Spectre.Console;
using static coppercli.CliConstants;
using static coppercli.Core.Util.GrblProtocol;

namespace coppercli.Menus
{
    /// <summary>
    /// Mill menu for running G-code files with progress display.
    /// </summary>
    internal static class MillMenu
    {
        // ANSI escape codes for real-time display. We use raw ANSI codes here instead of
        // Spectre.Console markup because the milling progress display uses Console.SetCursorPosition
        // to redraw in-place at high frequency. Spectre.Console's rendering model doesn't support
        // this pattern well and causes visible flicker. Raw ANSI codes allow smooth updates.
        private const string Cyan = "\u001b[36m";
        private const string BoldCyan = "\u001b[1;36m";
        private const string Yellow = "\u001b[93m";
        private const string BoldBlue = "\u001b[1;34m";
        private const string BoldRed = "\u001b[1;31m";
        private const string Reset = "\u001b[0m";

        public static void Show()
        {
            if (!MenuHelpers.RequireConnection())
            {
                return;
            }

            var machine = AppState.Machine;

            if (machine.File.Count == 0)
            {
                MenuHelpers.ShowError("No file loaded");
                return;
            }

            if (MenuHelpers.ConfirmOrQuit("Probe equipment removed and spindle ready?", true) != true)
            {
                return;
            }

            // Move Z up to safe height
            AnsiConsole.MarkupLine($"[dim]Moving to safe height Z{SafeZHeightMm:F1}...[/]");
            machine.SendLine(CmdAbsolute);
            machine.SendLine($"{CmdRapidMove} Z{SafeZHeightMm:F1}");
            Thread.Sleep(CommandDelayMs);

            machine.FileGoto(0);
            machine.FileStart();
            MonitorMilling();
        }

        private static void MonitorMilling()
        {
            var machine = AppState.Machine;
            var currentFile = AppState.CurrentFile;

            bool paused = false;
            bool hasEverRun = false;
            var visitedCells = new HashSet<(int, int)>();

            var startTime = DateTime.Now;
            var pauseStartTime = DateTime.Now;
            var totalPausedTime = TimeSpan.Zero;
            int startLine = machine.FilePosition;

            var (lastWidth, lastHeight) = GetSafeWindowSize();

            Console.Clear();
            Console.CursorVisible = false;

            try
            {
                while (true)
                {
                    bool isRunning = machine.Mode == Machine.OperatingMode.SendFile;

                    if (isRunning)
                    {
                        hasEverRun = true;
                    }

                    bool reachedEnd = machine.FilePosition >= machine.File.Count;
                    bool machineIdle = machine.Status == StatusIdle;
                    if (hasEverRun && !isRunning && !paused && reachedEnd && machineIdle)
                    {
                        break;
                    }

                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(true);
                        if (InputHelpers.IsKey(key, ConsoleKey.P, 'p'))
                        {
                            machine.FeedHold();
                            paused = true;
                            pauseStartTime = DateTime.Now;
                        }
                        else if (InputHelpers.IsKey(key, ConsoleKey.R, 'r'))
                        {
                            machine.CycleStart();
                            paused = false;
                            totalPausedTime += DateTime.Now - pauseStartTime;
                        }
                        else if (InputHelpers.IsKey(key, ConsoleKey.X, 'x'))
                        {
                            StopAndRaiseZ();
                            return;
                        }
                    }

                    var currentPausedTime = paused ? (DateTime.Now - pauseStartTime) : TimeSpan.Zero;
                    var elapsed = DateTime.Now - startTime - totalPausedTime - currentPausedTime;

                    var (curWidth, curHeight) = GetSafeWindowSize();
                    if (curWidth != lastWidth || curHeight != lastHeight)
                    {
                        Console.Clear();
                        lastWidth = curWidth;
                        lastHeight = curHeight;
                    }

                    DrawMillProgress(paused, visitedCells, elapsed, startLine);
                    Thread.Sleep(StatusPollIntervalMs);
                }

                var finalElapsed = DateTime.Now - startTime - totalPausedTime;
                DrawMillProgress(false, visitedCells, finalElapsed, startLine);
                Console.WriteLine();
                AnsiConsole.MarkupLine("[green]Mill complete[/]");
                Thread.Sleep(ConfirmationDisplayMs);

                // Offer to clear probe data after successful mill
                if (AppState.ProbePoints != null)
                {
                    if (MenuHelpers.ConfirmOrQuit("Clear probe data?", true) == true)
                    {
                        AppState.ProbePoints = null;
                        AppState.ProbePointsApplied = false;
                        Persistence.ClearProbeAutoSave();
                        AnsiConsole.MarkupLine("[green]Probe data cleared[/]");
                        Thread.Sleep(ConfirmationDisplayMs);
                    }
                }
            }
            finally
            {
                Console.CursorVisible = true;
            }
        }

        private static (int Width, int Height) GetSafeWindowSize()
        {
            try
            {
                return (Console.WindowWidth, Console.WindowHeight);
            }
            catch
            {
                return (80, 24);
            }
        }

        private static void DrawMillProgress(bool paused, HashSet<(int, int)> visitedCells, TimeSpan elapsed, int startLine)
        {
            var machine = AppState.Machine;
            var currentFile = AppState.CurrentFile;

            if (currentFile == null || !currentFile.ContainsMotion)
            {
                return;
            }

            Console.SetCursorPosition(0, 0);

            var (winWidth, winHeight) = GetSafeWindowSize();

            string header = $"{BoldBlue}Milling{Reset}";
            int headerPad = Math.Max(0, (winWidth - 7) / 2);
            WriteLineTruncated(new string(' ', headerPad) + header, winWidth);
            WriteLineTruncated("", winWidth);

            var pos = machine.WorkPosition;
            int fileLine = machine.FilePosition;
            int totalLines = machine.File.Count;
            double pct = totalLines > 0 ? (100.0 * fileLine / totalLines) : 0;
            string etaStr = CalculateEta(elapsed, fileLine - startLine, totalLines - fileLine);
            string pausedIndicator = paused ? $"  {Yellow}PAUSED{Reset}" : "        ";

            int lineWidth = totalLines.ToString().Length;
            string lineStr = fileLine.ToString().PadLeft(lineWidth);

            WriteLineTruncated($"  {Cyan}{BuildProgressBar(pct, Math.Min(MillProgressBarWidth, winWidth - 15))}{Reset} {pct,5:F1}%", winWidth);
            WriteLineTruncated($"  Elapsed: {Cyan}{FormatTimeSpan(elapsed)}{Reset}   ETA: {Cyan}{etaStr}{Reset}{pausedIndicator}", winWidth);
            WriteLineTruncated($"  X:{Cyan}{pos.X,8:F2}{Reset}  Y:{Cyan}{pos.Y,8:F2}{Reset}  Z:{Cyan}{pos.Z,8:F2}{Reset}   Line {lineStr}/{totalLines}", winWidth);
            WriteLineTruncated("", winWidth);
            WriteLineTruncated($"  {BoldCyan}P{Reset}=Pause  {BoldCyan}R{Reset}=Resume  {BoldRed}X{Reset}=Stop", winWidth);

            double minX = currentFile.Min.X;
            double maxX = currentFile.Max.X;
            double minY = currentFile.Min.Y;
            double maxY = currentFile.Max.Y;
            double rangeX = Math.Max(maxX - minX, MillMinRangeThreshold);
            double rangeY = Math.Max(maxY - minY, MillMinRangeThreshold);

            int availableWidth = winWidth - 4;
            int availableHeight = winHeight - MillTermHeightPadding;

            int gridWidth = Math.Clamp(availableWidth / MillGridCharsPerCell, 1, MillGridMaxWidth);
            int gridHeight = Math.Clamp(availableHeight, 1, MillGridMaxHeight);

            bool gridVisible = availableWidth >= MillGridMinWidth && availableHeight >= MillGridMinHeight;

            WriteLineTruncated("", winWidth);

            if (!gridVisible)
            {
                WriteLineTruncated("  (Window too small for map)", winWidth);
                return;
            }

            int gridX = MapToGrid(pos.X, minX, rangeX, gridWidth);
            int gridY = MapToGrid(pos.Y, minY, rangeY, gridHeight);

            if (pos.Z < MillCuttingDepthThreshold)
            {
                visitedCells.Add((gridX, gridY));
            }

            DrawPositionGrid(gridWidth, gridHeight, gridX, gridY, visitedCells, winWidth, minX, maxX, minY, maxY);
        }

        private static void WriteLineTruncated(string text, int maxWidth)
        {
            if (text.Length > maxWidth)
            {
                text = text.Substring(0, maxWidth);
            }
            else
            {
                text = text.PadRight(maxWidth);
            }
            Console.WriteLine(text);
        }

        private static string BuildProgressBar(double pct, int width)
        {
            int filled = (int)(pct / 100 * width);
            return new string('█', filled) + new string('░', width - filled);
        }

        private static string FormatTimeSpan(TimeSpan ts)
        {
            return ts.ToString(@"hh\:mm\:ss");
        }

        private static int MapToGrid(double value, double min, double range, int gridSize)
        {
            int index = (int)((value - min) / range * (gridSize - 1));
            return Math.Clamp(index, 0, gridSize - 1);
        }

        private static string CalculateEta(TimeSpan elapsed, int linesCompleted, int linesRemaining)
        {
            if (linesCompleted <= MillMinLinesForEta || elapsed.TotalSeconds <= MillMinSecondsForEta)
            {
                return "--:--:--";
            }
            double secondsPerLine = elapsed.TotalSeconds / linesCompleted;
            var eta = TimeSpan.FromSeconds(secondsPerLine * linesRemaining);
            return FormatTimeSpan(eta);
        }

        private static void DrawPositionGrid(int width, int height, int posX, int posY,
            HashSet<(int, int)> visited, int winWidth, double minX, double maxX, double minY, double maxY)
        {
            int matrixWidth = width * MillGridCharsPerCell;
            int leftPadding = Math.Max(0, (winWidth - matrixWidth - MillBorderPadding) / 2);
            string pad = new string(' ', leftPadding);

            WriteLineTruncated($"{pad}┌{new string('─', matrixWidth)}┐", winWidth);

            for (int y = height - 1; y >= 0; y--)
            {
                var line = new System.Text.StringBuilder(pad);
                line.Append('│');

                for (int x = 0; x < width; x++)
                {
                    if (x == posX && y == posY)
                    {
                        line.Append(Yellow).Append(MillCurrentPosMarker).Append(Reset);
                    }
                    else if (visited.Contains((x, y)))
                    {
                        line.Append(MillVisitedMarker);
                    }
                    else
                    {
                        line.Append(MillEmptyMarker);
                    }
                }

                line.Append('│');
                WriteLineTruncated(line.ToString(), winWidth);
            }

            WriteLineTruncated($"{pad}└{new string('─', matrixWidth)}┘", winWidth);
            WriteLineTruncated($"{pad}  X: {minX:F1} to {maxX:F1}  Y: {minY:F1} to {maxY:F1}", winWidth);
        }

        private static void StopAndRaiseZ()
        {
            var machine = AppState.Machine;
            int position = machine.FilePosition;

            AppState.SuppressErrors = true;
            machine.SoftReset();

            Console.WriteLine();
            AnsiConsole.MarkupLine($"[yellow]Stopped at line {position} - stopping spindle and raising Z...[/]");

            Thread.Sleep(ResetWaitMs);

            if (machine.Status.StartsWith(StatusAlarm))
            {
                machine.SendLine(CmdUnlock);
                Thread.Sleep(CommandDelayMs);
            }

            AppState.SuppressErrors = false;

            machine.SendLine(CmdSpindleOff);
            machine.SendLine(CmdAbsolute);
            machine.SendLine($"{CmdRapidMove} Z{SafeZHeightMm:F1}");
            Thread.Sleep(CommandDelayMs);
        }
    }
}
