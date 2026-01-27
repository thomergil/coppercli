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
            var machine = AppState.Machine;

            // Defense in depth: ensure auto-clear is disabled during milling
            machine.EnableAutoStateClear = false;

            if (!MenuHelpers.RequireConnection())
            {
                return;
            }

            if (machine.File.Count == 0)
            {
                MenuHelpers.ShowError("No file loaded");
                return;
            }

            // Ask first - user might open door to check/prepare
            if (MenuHelpers.ConfirmOrQuit("Probe clip and magnet removed, door closed, spindle ready?", true) != true)
            {
                return;
            }

            // === TAKE CONTROL OF MACHINE STATE ===
            // Machine may be in unknown state: door open, still moving, etc.
            // We must get it to a known good state before proceeding.

            AnsiConsole.MarkupLine("[dim]Preparing machine...[/]");

            // Clear Door state if present (sends CycleStart - safe while moving)
            MachineCommands.ClearDoorState(machine);

            // Wait for machine to reach Idle
            StatusHelpers.WaitForIdle(machine, IdleWaitTimeoutMs);

            // Check for Alarm - cannot proceed, user must manually resolve
            if (StatusHelpers.IsAlarm(machine))
            {
                MenuHelpers.ShowError("Machine is in ALARM state. Please home the machine and try again.");
                return;
            }

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

            Logger.Clear();
            Logger.Log("=== MonitorMilling started ===");
            Logger.Log("Log file: {0}", Logger.LogFilePath);
            Logger.Log("File.Count={0}, FilePosition={1}", machine.File.Count, machine.FilePosition);

            // Subscribe to machine events for logging
            Action<string> logLineSent = (line) => Logger.Log("TX> {0}", line);
            Action<string> logLineReceived = (line) => Logger.Log("RX< {0}", line);
            Action<string> logStatusReceived = (line) => Logger.Log("STATUS: {0}", line);
            Action logModeChanged = () => Logger.Log("MODE changed to: {0}", machine.Mode);
            Action logStatusChanged = () => Logger.Log("Status changed to: {0}", machine.Status);
            Action<string> logInfo = (msg) => Logger.Log("INFO: {0}", msg);

            machine.LineSent += logLineSent;
            machine.LineReceived += logLineReceived;
            machine.StatusReceived += logStatusReceived;
            machine.OperatingModeChanged += logModeChanged;
            machine.StatusChanged += logStatusChanged;
            machine.Info += logInfo;

            Console.Clear();
            Console.CursorVisible = false;

            try
            {
                // === SETTLING PHASE ===
                // Wait for machine to be stable in Idle state for the full settle period
                int settleSeconds = PostIdleSettleMs / OneSecondMs;
                int stableCount = 0;
                Logger.Log("Settling phase: waiting {0} seconds", settleSeconds);
                while (stableCount < settleSeconds)
                {
                    string statusBefore = machine.Status;
                    DrawMillProgress(false, visitedCells, TimeSpan.Zero, 0, $"Settling... {settleSeconds - stableCount}s");

                    // Check for X key to stop during settling
                    for (int ms = 0; ms < OneSecondMs; ms += StatusPollIntervalMs)
                    {
                        if (Console.KeyAvailable)
                        {
                            var key = Console.ReadKey(true);
                            if (InputHelpers.IsKey(key, ConsoleKey.X, 'x'))
                            {
                                Logger.Log("Stopping during settling (X pressed)");
                                StopAndRaiseZ();
                                return;
                            }
                        }
                        Thread.Sleep(StatusPollIntervalMs);
                    }

                    if (machine.Status != statusBefore || !StatusHelpers.IsIdle(machine))
                    {
                        Logger.Log("Settling: status changed from {0} to {1}, resetting", statusBefore, machine.Status);
                        MachineCommands.ClearDoorState(machine);
                        StatusHelpers.WaitForIdle(machine, IdleWaitTimeoutMs);
                        stableCount = 0;
                    }
                    else
                    {
                        stableCount++;
                    }
                }
                Logger.Log("Settling complete");

                // === MACHINE IS NOW IN KNOWN GOOD STATE ===

                // Move Z up to safe height and start milling
                Logger.Log("Moving to safe height Z{0}", SafeZHeightMm);
                DrawMillProgress(false, visitedCells, TimeSpan.Zero, 0, $"Moving to safe height Z{SafeZHeightMm:F1}...");
                machine.SendLine(CmdAbsolute);
                machine.SendLine($"{CmdRapidMove} Z{SafeZHeightMm:F1}");
                StatusHelpers.WaitForIdle(machine, ZHeightWaitTimeoutMs);
                Logger.Log("At safe height, status={0}", machine.Status);

                // Start milling
                Logger.Log("Before FileGoto: Mode={0}, FilePosition={1}, Connected={2}", machine.Mode, machine.FilePosition, machine.Connected);
                machine.FileGoto(0);
                Logger.Log("After FileGoto: Mode={0}, FilePosition={1}", machine.Mode, machine.FilePosition);
                Logger.Log("Calling FileStart (requires Mode=Manual)...");
                machine.FileStart();
                Logger.Log("After FileStart: Mode={0}, FilePosition={1}", machine.Mode, machine.FilePosition);
                if (machine.Mode != Machine.OperatingMode.SendFile)
                {
                    Logger.Log("WARNING: Mode is not SendFile after FileStart! Mode={0}", machine.Mode);
                }
                Thread.Sleep(CommandDelayMs);
                Logger.Log("After delay: Mode={0}, FilePosition={1}", machine.Mode, machine.FilePosition);
                Console.Clear();

                int loopCount = 0;
                while (true)
                {
                    loopCount++;
                    bool isRunning = machine.Mode == Machine.OperatingMode.SendFile;

                    if (isRunning && !hasEverRun)
                    {
                        Logger.Log("Loop {0}: hasEverRun set to true", loopCount);
                        hasEverRun = true;
                    }

                    bool reachedEnd = machine.FilePosition >= machine.File.Count;
                    bool machineIdle = machine.Status == StatusIdle;

                    // Log state every 10 iterations or on significant changes
                    if (loopCount % 10 == 1 || !isRunning)
                    {
                        Logger.Log("Loop {0}: Mode={1}, isRunning={2}, hasEverRun={3}, paused={4}, reachedEnd={5}, machineIdle={6}, FilePos={7}/{8}, Status={9}",
                            loopCount, machine.Mode, isRunning, hasEverRun, paused, reachedEnd, machineIdle,
                            machine.FilePosition, machine.File.Count, machine.Status);
                    }

                    if (hasEverRun && !isRunning && !paused && reachedEnd && machineIdle)
                    {
                        Logger.Log("Exit condition met - milling complete");
                        break;
                    }

                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(true);
                        Logger.Log("Key pressed: {0}", key.Key);
                        if (InputHelpers.IsKey(key, ConsoleKey.P, 'p'))
                        {
                            Logger.Log("Pausing (FeedHold)");
                            machine.FeedHold();
                            paused = true;
                            pauseStartTime = DateTime.Now;
                        }
                        else if (InputHelpers.IsKey(key, ConsoleKey.R, 'r'))
                        {
                            Logger.Log("Resuming: Mode={0}, Status={1}", machine.Mode, machine.Status);

                            // If machine is in Hold state, release the hold
                            if (StatusHelpers.IsHold(machine))
                            {
                                Logger.Log("Status is Hold - sending CycleStart to release hold");
                                machine.CycleStart();
                            }

                            // If Mode is Manual (file sending stopped), restart it
                            if (machine.Mode == Machine.OperatingMode.Manual)
                            {
                                Logger.Log("Mode is Manual - calling FileStart to resume file sending");
                                machine.FileStart();
                            }

                            paused = false;
                            totalPausedTime += DateTime.Now - pauseStartTime;
                            Logger.Log("After resume: Mode={0}, Status={1}", machine.Mode, machine.Status);
                        }
                        else if (InputHelpers.IsKey(key, ConsoleKey.X, 'x'))
                        {
                            Logger.Log("Stopping (X pressed)");
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
                        AppState.AreProbePointsApplied = false;
                        Persistence.ClearProbeAutoSave();
                        AppState.Session.LastSavedProbeFile = "";
                        Persistence.SaveSession();
                        AnsiConsole.MarkupLine("[green]Probe data cleared[/]");
                        Thread.Sleep(ConfirmationDisplayMs);
                    }
                }
            }
            finally
            {
                // Unsubscribe from events
                machine.LineSent -= logLineSent;
                machine.LineReceived -= logLineReceived;
                machine.StatusReceived -= logStatusReceived;
                machine.OperatingModeChanged -= logModeChanged;
                machine.StatusChanged -= logStatusChanged;
                machine.Info -= logInfo;

                Logger.Log("=== MonitorMilling ended ===");
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

        private static void DrawMillProgress(bool paused, HashSet<(int, int)> visitedCells, TimeSpan elapsed, int startLine, string? settlingMessage = null)
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

            // Show machine status prominently when not running normally
            string status = machine.Status;
            string statusDisplay;
            if (status.StartsWith(StatusHold))
            {
                statusDisplay = $"{Yellow}{OverlayHoldMessage}{Reset}";
            }
            else if (paused)
            {
                statusDisplay = $"{Yellow}PAUSED{Reset}";
            }
            else if (status.StartsWith(StatusAlarm))
            {
                statusDisplay = $"{BoldRed}{StatusAlarm}{Reset}";
            }
            else
            {
                statusDisplay = $"{Cyan}{status}{Reset}";
            }

            int lineWidth = totalLines.ToString().Length;
            string lineStr = fileLine.ToString().PadLeft(lineWidth);

            if (settlingMessage != null)
            {
                WriteLineTruncated($"  {Yellow}{settlingMessage}{Reset}", winWidth);
            }
            else
            {
                WriteLineTruncated($"  {Cyan}{BuildProgressBar(pct, Math.Min(MillProgressBarWidth, winWidth - 15))}{Reset} {pct,5:F1}%", winWidth);
            }
            WriteLineTruncated($"  Status: {statusDisplay}    Elapsed: {Cyan}{FormatTimeSpan(elapsed)}{Reset}   ETA: {Cyan}{etaStr}{Reset}", winWidth);
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

            // Determine overlay message and color (if any)
            string? overlayMessage = null;
            string overlayColor = Yellow;

            if (settlingMessage != null)
            {
                overlayMessage = settlingMessage;
            }
            else if (status.StartsWith(StatusHold))
            {
                overlayMessage = OverlayHoldMessage;
            }
            else if (status.StartsWith(StatusAlarm))
            {
                overlayMessage = OverlayAlarmMessage;
                overlayColor = BoldRed;
            }

            DrawPositionGrid(gridWidth, gridHeight, gridX, gridY, visitedCells, winWidth, minX, maxX, minY, maxY, overlayMessage, overlayColor);
        }

        private static void WriteLineTruncated(string text, int maxWidth)
        {
            // Calculate display length (excluding ANSI codes)
            int displayLen = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\u001b')
                {
                    while (i < text.Length && text[i] != 'm') i++;
                }
                else
                {
                    displayLen++;
                }
            }

            if (displayLen > maxWidth)
            {
                // Truncate to maxWidth display characters
                var result = new System.Text.StringBuilder();
                int displayed = 0;
                for (int i = 0; i < text.Length && displayed < maxWidth; i++)
                {
                    if (text[i] == '\u001b')
                    {
                        // Copy entire ANSI sequence
                        while (i < text.Length && text[i] != 'm')
                        {
                            result.Append(text[i]);
                            i++;
                        }
                        if (i < text.Length)
                        {
                            result.Append(text[i]); // 'm'
                        }
                    }
                    else
                    {
                        result.Append(text[i]);
                        displayed++;
                    }
                }
                text = result.ToString();
            }
            else if (displayLen < maxWidth)
            {
                text = text + new string(' ', maxWidth - displayLen);
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
            HashSet<(int, int)> visited, int winWidth, double minX, double maxX, double minY, double maxY,
            string? overlayMessage = null, string overlayColor = Yellow)
        {
            int matrixWidth = width * MillGridCharsPerCell;
            int leftPadding = Math.Max(0, (winWidth - matrixWidth - MillBorderPadding) / 2);
            string pad = new string(' ', leftPadding);

            // Calculate overlay box position (if overlay is shown)
            // Box is 6 lines tall: top border, empty, message, X=Stop, empty, bottom border
            int boxHeight = 6;
            int boxWidth = Math.Min(40, matrixWidth - 4);
            int boxStartChar = (matrixWidth - boxWidth) / 2;

            // Center vertically in the grid (grid rows go from height-1 down to 0)
            int boxCenterRow = height / 2;
            int boxTopRow = boxCenterRow + boxHeight / 2;
            int boxBottomRow = boxTopRow - boxHeight + 1;

            // Pre-build the box content lines
            int msgPad = overlayMessage != null ? Math.Max(0, (boxWidth - 2 - overlayMessage.Length) / 2) : 0;
            string centeredMsg = overlayMessage != null
                ? overlayMessage.PadLeft(msgPad + overlayMessage.Length).PadRight(boxWidth - 2)
                : "";

            // X=Stop line (centered, with color formatting)
            string xStopText = "X=Stop";
            int xStopPad = Math.Max(0, (boxWidth - 2 - xStopText.Length) / 2);
            string centeredXStop = xStopText.PadLeft(xStopPad + xStopText.Length).PadRight(boxWidth - 2);

            WriteLineTruncated($"{pad}┌{new string('─', matrixWidth)}┐", winWidth);

            for (int y = height - 1; y >= 0; y--)
            {
                // Build the grid row content first
                var gridContent = new System.Text.StringBuilder();
                for (int x = 0; x < width; x++)
                {
                    if (x == posX && y == posY)
                    {
                        gridContent.Append(Yellow).Append(MillCurrentPosMarker).Append(Reset);
                    }
                    else if (visited.Contains((x, y)))
                    {
                        gridContent.Append(MillVisitedMarker);
                    }
                    else
                    {
                        gridContent.Append(MillEmptyMarker);
                    }
                }

                string rowContent = gridContent.ToString();

                // If overlay is active and this row is within the box, overlay the box content
                if (overlayMessage != null && y <= boxTopRow && y >= boxBottomRow)
                {
                    int boxLineIndex = boxTopRow - y;
                    string boxLine = boxLineIndex switch
                    {
                        0 => $"╔{new string('═', boxWidth - 2)}╗",
                        1 => $"║{new string(' ', boxWidth - 2)}║",
                        2 => $"║{overlayColor}{centeredMsg}{Reset}║",
                        3 => $"║{BoldRed}{centeredXStop}{Reset}║",
                        4 => $"║{new string(' ', boxWidth - 2)}║",
                        5 => $"╚{new string('═', boxWidth - 2)}╝",
                        _ => ""
                    };

                    // Overlay the box onto the row
                    // We need to work with display positions, not string positions (ANSI codes don't take space)
                    rowContent = OverlayOnRow(rowContent, boxLine, boxStartChar, matrixWidth);
                }

                WriteLineTruncated($"{pad}│{rowContent}│", winWidth);
            }

            WriteLineTruncated($"{pad}└{new string('─', matrixWidth)}┘", winWidth);
            WriteLineTruncated($"{pad}  X: {minX:F1} to {maxX:F1}  Y: {minY:F1} to {maxY:F1}", winWidth);
        }

        /// <summary>
        /// Overlay a string onto a row at a specific display position.
        /// Handles ANSI escape codes correctly (they don't take display width).
        /// Works by iterating through both strings and outputting from the appropriate source.
        /// </summary>
        private static string OverlayOnRow(string row, string overlay, int startPos, int totalWidth)
        {
            var result = new System.Text.StringBuilder();
            int rowIdx = 0;
            int overlayIdx = 0;
            int displayPos = 0;

            // Calculate overlay display length
            int overlayDisplayLen = 0;
            for (int j = 0; j < overlay.Length; j++)
            {
                if (overlay[j] == '\u001b')
                {
                    while (j < overlay.Length && overlay[j] != 'm') j++;
                }
                else
                {
                    overlayDisplayLen++;
                }
            }

            int overlayEnd = startPos + overlayDisplayLen;

            while (displayPos < totalWidth)
            {
                if (displayPos >= startPos && displayPos < overlayEnd)
                {
                    // In overlay region: output from overlay, skip row content
                    // First, skip any ANSI codes in the row (consume but don't output)
                    while (rowIdx < row.Length && row[rowIdx] == '\u001b')
                    {
                        while (rowIdx < row.Length && row[rowIdx] != 'm') rowIdx++;
                        if (rowIdx < row.Length) rowIdx++; // skip 'm'
                    }
                    // Skip the visible character in row
                    if (rowIdx < row.Length) rowIdx++;

                    // Output from overlay: first any ANSI codes, then the visible char
                    while (overlayIdx < overlay.Length && overlay[overlayIdx] == '\u001b')
                    {
                        while (overlayIdx < overlay.Length && overlay[overlayIdx] != 'm')
                        {
                            result.Append(overlay[overlayIdx]);
                            overlayIdx++;
                        }
                        if (overlayIdx < overlay.Length)
                        {
                            result.Append(overlay[overlayIdx]); // 'm'
                            overlayIdx++;
                        }
                    }
                    // Output the visible char from overlay
                    if (overlayIdx < overlay.Length)
                    {
                        result.Append(overlay[overlayIdx]);
                        overlayIdx++;
                    }

                    displayPos++;
                }
                else
                {
                    // Outside overlay region: output from row
                    // Copy ANSI codes
                    while (rowIdx < row.Length && row[rowIdx] == '\u001b')
                    {
                        while (rowIdx < row.Length && row[rowIdx] != 'm')
                        {
                            result.Append(row[rowIdx]);
                            rowIdx++;
                        }
                        if (rowIdx < row.Length)
                        {
                            result.Append(row[rowIdx]); // 'm'
                            rowIdx++;
                        }
                    }
                    // Copy visible char
                    if (rowIdx < row.Length)
                    {
                        result.Append(row[rowIdx]);
                        rowIdx++;
                    }
                    else
                    {
                        result.Append(' ');
                    }
                    displayPos++;
                }
            }

            return result.ToString();
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

            if (StatusHelpers.IsAlarm(machine))
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
