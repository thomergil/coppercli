// Extracted from Program.cs

using coppercli.Core.Communication;
using coppercli.Helpers;
using Spectre.Console;
using static coppercli.CliConstants;
using static coppercli.Core.Util.GrblProtocol;
using static coppercli.Helpers.DisplayHelpers;

namespace coppercli.Menus
{
    /// <summary>
    /// Mill menu for running G-code files with progress display.
    /// </summary>
    internal static class MillMenu
    {

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
                    string settleMessage = StatusHelpers.IsIdle(machine)
                        ? $"Settling... {settleSeconds - stableCount}s"
                        : "Waiting for idle.";
                    DrawMillProgress(false, visitedCells, TimeSpan.Zero, 0, settleMessage);

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

                // Start milling - let the G-code file handle its own positioning
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

                // Mark that file has run - even if Mode changed back to Manual before we enter the loop,
                // the file DID run (we called FileStart and lines were sent)
                hasEverRun = true;

                Console.Clear();

                int loopCount = 0;
                int stableIdleCount = 0;

                while (true)
                {
                    loopCount++;
                    bool isRunning = machine.Mode == Machine.OperatingMode.SendFile;

                    if (isRunning && !hasEverRun)
                    {
                        Logger.Log("Loop {0}: hasEverRun set to true", loopCount);
                        hasEverRun = true;
                    }

                    // Detect M6-triggered pause: Mode is Manual (file paused), not user-initiated
                    // Note: Don't require hasEverRun - file may have run so fast we never saw SendFile mode
                    if (!isRunning && !paused)
                    {
                        // Check if previous line was M6
                        int prevLine = machine.FilePosition - 1;
                        if (prevLine >= 0 && prevLine < machine.File.Count)
                        {
                            if (ToolChangeHelpers.IsM6Line(machine.File[prevLine]))
                            {
                                // Tool change commences immediately without prompting.
                                // The user expects M6 to mean "do tool change now" - no extra confirmation needed.
                                // HandleToolChange() will guide them through the process.
                                Logger.Log("M6 tool change detected at line {0}, initiating automatically", prevLine);
                                Logger.Log("M6 detection: Status={0}, Mode={1}, BufferState={2}",
                                    machine.Status, machine.Mode, machine.BufferState);
                                Logger.Log("M6 detection: WorkPos=({0:F3}, {1:F3}, {2:F3})",
                                    machine.WorkPosition.X, machine.WorkPosition.Y, machine.WorkPosition.Z);
                                pauseStartTime = DateTime.Now;

                                // Set up refresh callback so tool change can update the overlay
                                ToolChangeHelpers.RefreshDisplay = () =>
                                {
                                    var currentPausedTime = DateTime.Now - pauseStartTime;
                                    var elapsed = DateTime.Now - startTime - totalPausedTime - currentPausedTime;
                                    DrawMillProgress(false, visitedCells, elapsed, startLine);
                                };

                                bool success = ToolChangeHelpers.HandleToolChange();

                                ToolChangeHelpers.RefreshDisplay = null;

                                if (success)
                                {
                                    Logger.Log("Tool change completed successfully, resuming");
                                    totalPausedTime += DateTime.Now - pauseStartTime;

                                    // Resume file sending
                                    machine.FileStart();
                                    Logger.Log("After tool change resume: Mode={0}, Status={1}", machine.Mode, machine.Status);
                                    stableIdleCount = 0;  // Reset stable count after tool change
                                }
                                else
                                {
                                    Logger.Log("Tool change aborted by user");
                                    StopAndRaiseZ();
                                    return;
                                }
                            }
                        }
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

                    // Check for stable idle completion - must be idle for settle period
                    if (hasEverRun && !isRunning && !paused && reachedEnd)
                    {
                        if (StatusHelpers.WaitForStableIdleAsync(machine, ref stableIdleCount))
                        {
                            Logger.Log("Exit condition met - milling complete (stable idle for {0}ms)", IdleSettleMs);
                            break;
                        }
                    }
                    else
                    {
                        stableIdleCount = 0;
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

        private static void DrawMillProgress(bool paused, HashSet<(int, int)> visitedCells, TimeSpan elapsed, int startLine, string? settlingMessage = null)
        {
            var machine = AppState.Machine;
            var currentFile = AppState.CurrentFile;

            if (currentFile == null)
            {
                Logger.Log("DrawMillProgress: currentFile is null, returning early");
                return;
            }
            if (!currentFile.ContainsMotion)
            {
                Logger.Log("DrawMillProgress: ContainsMotion is false, returning early");
                return;
            }

            Console.SetCursorPosition(0, 0);

            var (winWidth, winHeight) = GetSafeWindowSize();

            string header = $"{AnsiBoldBlue}Milling{AnsiReset}";
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
                statusDisplay = $"{AnsiYellow}{OverlayHoldMessage}{AnsiReset}";
            }
            else if (paused)
            {
                statusDisplay = $"{AnsiYellow}PAUSED{AnsiReset}";
            }
            else if (status.StartsWith(StatusAlarm))
            {
                statusDisplay = $"{AnsiBoldRed}{StatusAlarm}{AnsiReset}";
            }
            else
            {
                statusDisplay = $"{AnsiCyan}{status}{AnsiReset}";
            }

            int lineWidth = totalLines.ToString().Length;
            string lineStr = fileLine.ToString().PadLeft(lineWidth);

            if (settlingMessage != null)
            {
                WriteLineTruncated($"  {AnsiYellow}{settlingMessage}{AnsiReset}", winWidth);
            }
            else
            {
                WriteLineTruncated($"  {AnsiCyan}{BuildProgressBar(pct, Math.Min(MillProgressBarWidth, winWidth - 15))}{AnsiReset} {pct,5:F1}%", winWidth);
            }
            WriteLineTruncated($"  Status: {statusDisplay}    Elapsed: {AnsiCyan}{FormatTimeSpan(elapsed)}{AnsiReset}   ETA: {AnsiCyan}{etaStr}{AnsiReset}", winWidth);
            WriteLineTruncated($"  X:{AnsiCyan}{pos.X,8:F2}{AnsiReset}  Y:{AnsiCyan}{pos.Y,8:F2}{AnsiReset}  Z:{AnsiCyan}{pos.Z,8:F2}{AnsiReset}   Line {lineStr}/{totalLines}", winWidth);

            // Tool change action line (always output to keep layout stable)
            if (ToolChangeHelpers.StatusAction != null)
            {
                WriteLineTruncated($"  {AnsiDim}[{ToolChangeLabel}]{AnsiReset} {AnsiCyan}{ToolChangeHelpers.StatusAction}{AnsiReset}", winWidth);
            }
            else
            {
                WriteLineTruncated("", winWidth);
            }

            WriteLineTruncated($"  {AnsiBoldCyan}P{AnsiReset}=Pause  {AnsiBoldCyan}R{AnsiReset}=Resume  {AnsiBoldRed}X{AnsiReset}=Stop", winWidth);

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
            string? overlaySubMessage = null;
            string overlayColor = AnsiYellow;

            if (ToolChangeHelpers.OverlayMessage != null)
            {
                overlayMessage = ToolChangeHelpers.OverlayMessage;
                overlaySubMessage = ToolChangeHelpers.OverlaySubMessage;
            }
            else if (settlingMessage != null)
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
                overlayColor = AnsiBoldRed;
            }

            DrawPositionGrid(gridWidth, gridHeight, gridX, gridY, visitedCells, winWidth, minX, maxX, minY, maxY, overlayMessage, overlayColor, overlaySubMessage);
        }

        private static string BuildProgressBar(double pct, int width)
        {
            int filled = (int)(pct / 100 * width);
            return new string('█', filled) + new string('░', width - filled);
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
            string? overlayMessage = null, string overlayColor = AnsiYellow, string? overlaySubMessage = null)
        {
            int matrixWidth = width * MillGridCharsPerCell;
            int leftPadding = Math.Max(0, (winWidth - matrixWidth - MillBorderPadding) / 2);
            string pad = new string(' ', leftPadding);

            // Calculate overlay box position (if overlay is shown)
            int boxWidth = Math.Min(40, matrixWidth - 4);
            int boxStartChar = (matrixWidth - boxWidth) / 2;

            // Center vertically in the grid (grid rows go from height-1 down to 0)
            int boxCenterRow = height / 2;
            int boxTopRow = boxCenterRow + OverlayBoxHeight / 2;
            int boxBottomRow = boxTopRow - OverlayBoxHeight + 1;

            WriteLineTruncated($"{pad}┌{new string('─', matrixWidth)}┐", winWidth);

            // Use provided sub-message or default to "X=Stop"
            string subMsg = overlaySubMessage ?? "X=Stop";

            for (int y = height - 1; y >= 0; y--)
            {
                // Build the grid row content first
                var gridContent = new System.Text.StringBuilder();
                for (int x = 0; x < width; x++)
                {
                    if (x == posX && y == posY)
                    {
                        gridContent.Append(AnsiYellow).Append(MillCurrentPosMarker).Append(AnsiReset);
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
                    string boxLine = GetOverlayBoxLine(boxLineIndex, boxWidth,
                        overlayMessage, overlayColor, subMsg, AnsiCyan);

                    // Overlay the box onto the row (margin lines are empty - show background)
                    if (!string.IsNullOrEmpty(boxLine))
                    {
                        rowContent = OverlayOnRow(rowContent, boxLine, boxStartChar, matrixWidth);
                    }
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
            machine.SendLine($"{CmdRapidMove} Z{RetractZMm:F1}");
            Thread.Sleep(CommandDelayMs);
        }
    }
}
