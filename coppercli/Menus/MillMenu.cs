// Mill menu - thin wrapper around MillingController
// Controller owns workflow logic, TUI owns display and user interaction

using System.Linq;
using coppercli.Core.Communication;
using coppercli.Core.Controllers;
using coppercli.Core.Util;
using coppercli.Helpers;
using Spectre.Console;
using static coppercli.CliConstants;
using static coppercli.Core.Util.Constants;
using static coppercli.Core.Util.GrblProtocol;
using static coppercli.Helpers.DisplayHelpers;

namespace coppercli.Menus
{
    /// <summary>
    /// Mill menu for running G-code files with progress display.
    /// Uses MillingController for workflow logic.
    /// </summary>
    internal static class MillMenu
    {
        // Shared state for display updates (set by event handlers, read by render loop)
        private static ProgressInfo? _latestProgress;
        private static ControllerState _latestState;
        private static ToolChangeInfo? _pendingToolChange;
        private static ControllerError? _latestError;

        // Tool change display state (replaces ToolChangeHelpers static properties)
        private static string? _toolChangeOverlayMessage;
        private static string? _toolChangeOverlaySubMessage;
        private static string? _toolChangeStatusAction;

        public static void Show()
        {
            var machine = AppState.Machine;

            // Defense in depth: ensure auto-clear is disabled during milling
            machine.EnableAutoStateClear = false;

            try
            {
                // Validate preflight (shared with WebServer)
                var preflight = MenuHelpers.ValidateMillPreflight();

                // Handle blocking errors first
                if (preflight.Error == MillPreflightError.NotConnected)
                {
                    MenuHelpers.ShowError(DisabledConnect);
                    return;
                }
                if (preflight.Error == MillPreflightError.NoFile)
                {
                    MenuHelpers.ShowError(DisabledNoFile);
                    return;
                }
                if (preflight.Error == MillPreflightError.ProbeNotApplied)
                {
                    MenuHelpers.ShowError(DisabledProbeNotApplied);
                    return;
                }
                if (preflight.Error == MillPreflightError.ProbeIncomplete)
                {
                    MenuHelpers.ShowError(string.Format(DisabledProbeIncomplete, preflight.ProbeProgress));
                    return;
                }
                // AlarmState will be handled by EnsureMachineReady below

                // Handle dangerous command warnings (prompt user)
                if (preflight.Warnings.Contains(MillPreflightWarning.DangerousCommands) &&
                    preflight.DangerousWarnings?.Count > 0)
                {
                    AnsiConsole.MarkupLine($"[{ColorError}]WARNING: File contains potentially dangerous commands:[/]");
                    foreach (var warning in preflight.DangerousWarnings)
                    {
                        AnsiConsole.MarkupLine($"[{ColorWarning}]  {warning}[/]");
                    }
                    AnsiConsole.WriteLine();

                    if (MenuHelpers.ConfirmOrQuit("Continue despite warnings?", false) != true)
                    {
                        return;
                    }
                }

                // Handle no machine profile warning
                if (preflight.Warnings.Contains(MillPreflightWarning.NoMachineProfile))
                {
                    if (MenuHelpers.ConfirmOrQuit($"[{ColorWarning}]{NoMachineProfileWarning}[/]. Continue?", false) != true)
                    {
                        return;
                    }
                }

                // Handle sleep prevention warning
                if (SleepPrevention.ShouldWarn())
                {
                    if (MenuHelpers.ConfirmOrQuit($"[{ColorWarning}]{SleepPreventionWarning}[/]. Continue?", false) != true)
                    {
                        return;
                    }
                }

                // === TAKE CONTROL OF MACHINE STATE ===
                AnsiConsole.MarkupLine($"[{ColorDim}]Preparing machine...[/]");

                if (!MachineCommands.EnsureMachineReady(machine))
                {
                    MenuHelpers.ShowError(ErrorMachineAlarm);
                    return;
                }

                MonitorMilling();
            }
            finally
            {
                // Re-enable auto-clear when leaving mill menu
                machine.EnableAutoStateClear = true;
            }
        }

        private static void MonitorMilling()
        {
            var machine = AppState.Machine;
            var currentFile = AppState.CurrentFile;
            var controller = AppState.Milling;

            // Reset shared state
            _latestProgress = null;
            _latestState = ControllerState.Idle;
            _pendingToolChange = null;
            _latestError = null;

            // TUI state for display
            bool paused = false;
            var visitedCells = new HashSet<(int, int)>();
            var startTime = DateTime.Now;
            var pauseStartTime = DateTime.Now;
            var totalPausedTime = TimeSpan.Zero;
            int startLine = machine.FilePosition;
            var (lastWidth, lastHeight) = GetSafeWindowSize();

            // Cancellation token for stopping the controller
            using var cts = new CancellationTokenSource();

            Logger.Clear();
            Logger.Log("=== MonitorMilling started (controller-based) ===");
            Logger.Log("Log file: {0}", Logger.LogFilePath);
            Logger.Log("File.Count={0}, FilePosition={1}", machine.File.Count, machine.FilePosition);

            // Subscribe to machine events for logging
            Action<string> logLineSent = (line) => Logger.Log("TX> {0}", line);
            Action<string> logLineReceived = (line) => Logger.Log("RX< {0}", line);
            Action<string> logStatusReceived = (line) => Logger.Log("STATUS: {0}", line);
            Action logModeChanged = () => Logger.Log("MODE changed to: {0}", machine.Mode);
            Action logStatusChanged = () => Logger.Log("Status changed to: {0}", machine.Status);
            Action<string> logInfo = (msg) => Logger.Log("INFO: {0}", msg);
            Action<string> logError = (msg) => Logger.Log("ERROR: {0}", msg);

            machine.LineSent += logLineSent;
            machine.LineReceived += logLineReceived;
            machine.StatusReceived += logStatusReceived;
            machine.OperatingModeChanged += logModeChanged;
            machine.StatusChanged += logStatusChanged;
            machine.Info += logInfo;
            machine.NonFatalException += logError;

            // Subscribe to controller events
            Action<ControllerState> onStateChanged = state =>
            {
                _latestState = state;
                Logger.Log("Controller state: {0}", state);
            };
            Action<ProgressInfo> onProgressChanged = progress =>
            {
                _latestProgress = progress;
            };
            Action<ToolChangeInfo> onToolChange = info =>
            {
                _pendingToolChange = info;
                Logger.Log("Tool change detected: T{0} at line {1}", info.ToolNumber, info.LineNumber);
            };
            Action<ControllerError> onError = error =>
            {
                _latestError = error;
                Logger.Log("Controller error: {0}", error.Message);
            };

            controller.StateChanged += onStateChanged;
            controller.ProgressChanged += onProgressChanged;
            controller.ToolChangeDetected += onToolChange;
            controller.ErrorOccurred += onError;

            Console.Clear();
            Console.CursorVisible = false;

            try
            {
                // === SAFETY CONFIRMATION + DEPTH ADJUSTMENT ===
                Logger.Log("Safety confirmation phase, depth={0:F2}mm", AppState.DepthAdjustment);
                while (true)
                {
                    string depthStr = AppState.DepthAdjustment == 0 ? "0" : $"{AppState.DepthAdjustment:+0.00;-0.00}";
                    string safetyMsg = $"{SafetyChecklistMessage}  Depth: {depthStr}mm";
                    DrawMillProgress(false, visitedCells, TimeSpan.Zero, 0, safetyMsg, SafetyDepthSubMessage);

                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(true);
                        if (InputHelpers.IsKey(key, ConsoleKey.Y))
                        {
                            Logger.Log("Safety confirmed, depth={0:F2}mm", AppState.DepthAdjustment);
                            break;
                        }
                        if (InputHelpers.IsKey(key, ConsoleKey.N) ||
                            InputHelpers.IsKey(key, ConsoleKey.X) ||
                            InputHelpers.IsExitKey(key))
                        {
                            Logger.Log("Safety confirmation aborted");
                            return;
                        }
                        if (key.Key == ConsoleKey.DownArrow)
                        {
                            AppState.AdjustDepthDeeper();
                            Logger.Log("Depth adjustment: {0:F2}mm (deeper)", AppState.DepthAdjustment);
                        }
                        if (key.Key == ConsoleKey.UpArrow)
                        {
                            AppState.AdjustDepthShallower();
                            Logger.Log("Depth adjustment: {0:F2}mm (shallower)", AppState.DepthAdjustment);
                        }
                    }
                    Thread.Sleep(StatusPollIntervalMs);
                }

                // Start sleep prevention
                SleepPrevention.Start();

                // === CONFIGURE AND START CONTROLLER ===
                controller.Options = new MillingOptions
                {
                    FilePath = currentFile?.FileName,
                    DepthAdjustment = (float)AppState.DepthAdjustment,
                    RequireHoming = !AppState.Machine.IsHomed,
                };

                Logger.Log("Starting controller: RequireHoming={0}, DepthAdjustment={1:F3}",
                    controller.Options.RequireHoming, controller.Options.DepthAdjustment);

                // Reset controller if needed (from previous run)
                if (controller.State != ControllerState.Idle)
                {
                    controller.Reset();
                }

                // Start controller (fire and forget - we monitor via events)
                _ = controller.StartAsync(cts.Token);

                // Record start time for ETA calculation
                startTime = DateTime.Now;
                startLine = 0;

                Console.Clear();

                // === MONITOR LOOP ===
                while (true)
                {
                    // Check controller state for completion
                    var state = _latestState;
                    if (state == ControllerState.Completed ||
                        state == ControllerState.Failed ||
                        state == ControllerState.Cancelled)
                    {
                        Logger.Log("Controller finished with state: {0}", state);
                        break;
                    }

                    // Handle pending tool change
                    if (_pendingToolChange != null)
                    {
                        var tcInfo = _pendingToolChange;
                        _pendingToolChange = null;

                        Logger.Log("Handling tool change for T{0}", tcInfo.ToolNumber);
                        pauseStartTime = DateTime.Now;

                        // Run tool change using ToolChangeController
                        bool success = RunToolChangeController(tcInfo, visitedCells, startTime, totalPausedTime, startLine);

                        if (success)
                        {
                            Logger.Log("Tool change completed successfully, resuming");
                            totalPausedTime += DateTime.Now - pauseStartTime;
                            controller.Resume();
                            paused = false;
                        }
                        else
                        {
                            Logger.Log("Tool change aborted by user");
                            cts.Cancel();
                            // Wait for controller to finish cleanup
                            Thread.Sleep(ResetWaitMs);
                            return;
                        }
                    }

                    // Handle keyboard input
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(true);
                        Logger.Log("Key pressed: {0}", key.Key);

                        if (InputHelpers.IsKey(key, ConsoleKey.P))
                        {
                            if (state == ControllerState.Running)
                            {
                                Logger.Log("Pausing");
                                controller.Pause();
                                paused = true;
                                pauseStartTime = DateTime.Now;
                            }
                        }
                        else if (InputHelpers.IsKey(key, ConsoleKey.R))
                        {
                            if (state == ControllerState.Paused)
                            {
                                Logger.Log("Resuming");
                                controller.Resume();
                                paused = false;
                                totalPausedTime += DateTime.Now - pauseStartTime;
                            }
                        }
                        else if (InputHelpers.IsExitKey(key))
                        {
                            Logger.Log("Stopping (Escape pressed)");
                            cts.Cancel();
                            // Wait for controller to finish cleanup
                            Thread.Sleep(ResetWaitMs);
                            return;
                        }
                        else if (key.KeyChar == '+' || key.KeyChar == '=')
                        {
                            Logger.Log("Feed override increase (+)");
                            machine.FeedOverrideIncrease();
                        }
                        else if (key.KeyChar == '-' || key.KeyChar == '_')
                        {
                            Logger.Log("Feed override decrease (-)");
                            machine.FeedOverrideDecrease();
                        }
                        else if (key.KeyChar == '0')
                        {
                            Logger.Log("Feed override reset (0)");
                            machine.FeedOverrideReset();
                        }
                    }

                    // Update paused state from controller
                    paused = (state == ControllerState.Paused);

                    // Calculate elapsed time
                    var currentPausedTime = paused ? (DateTime.Now - pauseStartTime) : TimeSpan.Zero;
                    var elapsed = DateTime.Now - startTime - totalPausedTime - currentPausedTime;

                    // Handle window resize
                    var (curWidth, curHeight) = GetSafeWindowSize();
                    if (curWidth != lastWidth || curHeight != lastHeight)
                    {
                        Console.Clear();
                        lastWidth = curWidth;
                        lastHeight = curHeight;
                    }

                    // Draw progress with current phase message if in setup phases
                    string? statusMessage = null;
                    if (_latestProgress != null && _latestProgress.Phase != "Milling")
                    {
                        statusMessage = _latestProgress.Message;
                    }

                    DrawMillProgress(paused, visitedCells, elapsed, startLine, statusMessage);
                    Thread.Sleep(StatusPollIntervalMs);
                }

                // === COMPLETION ===
                var finalState = _latestState;
                var finalElapsed = DateTime.Now - startTime - totalPausedTime;

                if (finalState == ControllerState.Completed)
                {
                    DrawMillProgress(false, visitedCells, finalElapsed, startLine);

                    // Offer to clear probe data after successful mill
                    if (AppState.ProbePoints != null)
                    {
                        if (ShowOverlayConfirm(ProbePromptClear, true) == true)
                        {
                            AppState.DiscardProbeData();
                            Persistence.ClearProbeAutoSave();
                            ShowOverlayTimed(ProbeStatusCleared, ConfirmationDisplayMs);
                        }
                    }
                }
                else if (finalState == ControllerState.Failed && _latestError != null)
                {
                    MenuHelpers.ShowError(_latestError.Message);
                }

                // Reset controller for next use
                if (controller.State != ControllerState.Idle)
                {
                    controller.Reset();
                }
            }
            finally
            {
                // Stop sleep prevention
                SleepPrevention.Stop();

                // Unsubscribe from machine events
                machine.LineSent -= logLineSent;
                machine.LineReceived -= logLineReceived;
                machine.StatusReceived -= logStatusReceived;
                machine.OperatingModeChanged -= logModeChanged;
                machine.StatusChanged -= logStatusChanged;
                machine.Info -= logInfo;
                machine.NonFatalException -= logError;

                // Unsubscribe from controller events
                controller.StateChanged -= onStateChanged;
                controller.ProgressChanged -= onProgressChanged;
                controller.ToolChangeDetected -= onToolChange;
                controller.ErrorOccurred -= onError;

                Logger.Log("=== MonitorMilling ended ===");
                Console.CursorVisible = true;
            }
        }

        private static void DrawMillProgress(bool paused, HashSet<(int, int)> visitedCells, TimeSpan elapsed, int startLine, string? statusMessage = null, string? statusSubMessage = null)
        {
            var machine = AppState.Machine;
            var currentFile = AppState.CurrentFile;

            if (currentFile == null)
            {
                Logger.Log("DrawMillProgress: currentFile is null, returning early");
                return;
            }

            Console.SetCursorPosition(0, 0);

            var (winWidth, winHeight) = GetSafeWindowSize();

            string header = $"{AnsiPrompt}Milling{AnsiReset}";
            int headerPad = Math.Max(0, (winWidth - CalculateDisplayLength(header)) / 2);
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
                statusDisplay = $"{AnsiWarning}{OverlayHoldMessage}{AnsiReset}";
            }
            else if (paused)
            {
                statusDisplay = $"{AnsiWarning}PAUSED{AnsiReset}";
            }
            else if (status.StartsWith(StatusAlarm))
            {
                statusDisplay = $"{AnsiCritical}{StatusAlarm}{AnsiReset}";
            }
            else
            {
                statusDisplay = $"{AnsiInfo}{status}{AnsiReset}";
            }

            int lineWidth = totalLines.ToString().Length;
            string lineStr = fileLine.ToString().PadLeft(lineWidth);

            if (statusMessage != null)
            {
                WriteLineTruncated($"  {AnsiWarning}{statusMessage}{AnsiReset}", winWidth);
            }
            else
            {
                WriteLineTruncated($"  {AnsiInfo}{BuildProgressBar(pct, Math.Min(MillProgressBarWidth, winWidth - MillProgressLinePadding))}{AnsiReset} {pct,5:F1}%", winWidth);
            }
            WriteLineTruncated($"  Status: {statusDisplay}    Elapsed: {AnsiInfo}{FormatTimeSpan(elapsed)}{AnsiReset}   ETA: {AnsiInfo}{etaStr}{AnsiReset}", winWidth);
            WriteLineTruncated($"  X:{AnsiInfo}{pos.X,8:F2}{AnsiReset}  Y:{AnsiInfo}{pos.Y,8:F2}{AnsiReset}  Z:{AnsiInfo}{pos.Z,8:F2}{AnsiReset}   Line {lineStr}/{totalLines}", winWidth);

            // Tool change action line (always output to keep layout stable)
            if (_toolChangeStatusAction != null)
            {
                WriteLineTruncated($"  {AnsiDim}[{ToolChangeLabel}]{AnsiReset} {AnsiInfo}{_toolChangeStatusAction}{AnsiReset}", winWidth);
            }
            else
            {
                WriteLineTruncated("", winWidth);
            }

            // Show feed override if not default (100%)
            int feedOvr = machine.FeedOverride;
            string feedOvrStr = feedOvr != OverrideDefaultPercent ? $"  Feed: {AnsiWarning}{feedOvr}%{AnsiReset}" : "";
            WriteLineTruncated($"  {AnsiInfo}P{AnsiReset}=Pause  {AnsiInfo}R{AnsiReset}=Resume  {AnsiInfo}+/-/0{AnsiReset}=Feed  {AnsiCritical}Esc{AnsiReset}=Stop{feedOvrStr}", winWidth);

            double minX = currentFile.Min.X;
            double maxX = currentFile.Max.X;
            double minY = currentFile.Min.Y;
            double maxY = currentFile.Max.Y;
            double rangeX = Math.Max(maxX - minX, MillMinRangeThreshold);
            double rangeY = Math.Max(maxY - minY, MillMinRangeThreshold);

            int availableWidth = winWidth - MillGridHorizontalPadding;
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
            string overlayColor = AnsiWarning;

            if (_toolChangeOverlayMessage != null)
            {
                overlayMessage = _toolChangeOverlayMessage;
                overlaySubMessage = _toolChangeOverlaySubMessage;
            }
            else if (statusMessage != null)
            {
                overlayMessage = statusMessage;
                overlaySubMessage = statusSubMessage;
            }
            else if (status.StartsWith(StatusHold))
            {
                overlayMessage = OverlayHoldMessage;
            }
            else if (status.StartsWith(StatusAlarm))
            {
                overlayMessage = OverlayAlarmMessage;
                overlayColor = AnsiCritical;
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
            string? overlayMessage = null, string overlayColor = AnsiWarning, string? overlaySubMessage = null)
        {
            int matrixWidth = width * MillGridCharsPerCell;
            int leftPadding = Math.Max(0, (winWidth - matrixWidth - MillBorderPadding) / 2);
            string pad = new string(' ', leftPadding);

            // Calculate overlay box width based on content (if overlay is shown)
            int boxWidth = CalculateOverlayBoxWidth(overlayMessage ?? "", overlaySubMessage ?? "", matrixWidth);
            int boxStartChar = (matrixWidth - boxWidth) / 2;

            // Center vertically in the grid (grid rows go from height-1 down to 0)
            int boxCenterRow = height / 2;
            int boxTopRow = boxCenterRow + OverlayBoxHeight / 2;
            int boxBottomRow = boxTopRow - OverlayBoxHeight + 1;

            WriteLineTruncated($"{pad}┌{new string('─', matrixWidth)}┐", winWidth);

            // Use provided sub-message or default to "Esc=Stop"
            string subMsg = overlaySubMessage ?? "Esc=Stop";

            for (int y = height - 1; y >= 0; y--)
            {
                // Build the grid row content first
                var gridContent = new System.Text.StringBuilder();
                for (int x = 0; x < width; x++)
                {
                    if (x == posX && y == posY)
                    {
                        gridContent.Append(AnsiWarning).Append(MillCurrentPosMarker).Append(AnsiReset);
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
                        overlayMessage, overlayColor, subMsg, AnsiInfo);

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
                    while (j < overlay.Length && overlay[j] != 'm')
                    {
                        j++;
                    }
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
                    while (rowIdx < row.Length && row[rowIdx] == '\u001b')
                    {
                        while (rowIdx < row.Length && row[rowIdx] != 'm')
                        {
                            rowIdx++;
                        }
                        if (rowIdx < row.Length)
                        {
                            rowIdx++;
                        }
                    }
                    if (rowIdx < row.Length)
                    {
                        rowIdx++;
                    }

                    while (overlayIdx < overlay.Length && overlay[overlayIdx] == '\u001b')
                    {
                        while (overlayIdx < overlay.Length && overlay[overlayIdx] != 'm')
                        {
                            result.Append(overlay[overlayIdx]);
                            overlayIdx++;
                        }
                        if (overlayIdx < overlay.Length)
                        {
                            result.Append(overlay[overlayIdx]);
                            overlayIdx++;
                        }
                    }
                    if (overlayIdx < overlay.Length)
                    {
                        result.Append(overlay[overlayIdx]);
                        overlayIdx++;
                    }

                    displayPos++;
                }
                else
                {
                    while (rowIdx < row.Length && row[rowIdx] == '\u001b')
                    {
                        while (rowIdx < row.Length && row[rowIdx] != 'm')
                        {
                            result.Append(row[rowIdx]);
                            rowIdx++;
                        }
                        if (rowIdx < row.Length)
                        {
                            result.Append(row[rowIdx]);
                            rowIdx++;
                        }
                    }
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

        /// <summary>
        /// Run tool change using ToolChangeController.
        /// Handles async controller with synchronous TUI input loop.
        /// Returns true if tool change succeeded, false if aborted.
        /// </summary>
        private static bool RunToolChangeController(
            ToolChangeInfo tcInfo,
            HashSet<(int, int)> visitedCells,
            DateTime startTime,
            TimeSpan totalPausedTime,
            int startLine)
        {
            var toolChangeController = AppState.ToolChange;

            // Reset controller if needed
            if (toolChangeController.State != ControllerState.Idle)
            {
                toolChangeController.Reset();
            }

            // Set options from user settings and file bounds
            var settings = AppState.Settings;
            var currentFile = AppState.CurrentFile;
            toolChangeController.Options = new ToolChangeOptions
            {
                ProbeMaxDepth = settings.ProbeMaxDepth,
                ProbeFeed = settings.ProbeFeed,
                RetractHeight = RetractZMm,
                WorkAreaCenter = currentFile != null && currentFile.ContainsMotion
                    ? new Vector3(
                        (currentFile.Min.X + currentFile.Max.X) / 2,
                        (currentFile.Min.Y + currentFile.Max.Y) / 2,
                        0)
                    : null
            };

            // State for tracking tool change progress
            bool completed = false;
            bool success = false;
            UserInputRequest? pendingInput = null;
            string? userResponse = null;
            var pauseStartTime = DateTime.Now;

            // Helper to refresh display
            void RefreshDisplay()
            {
                var currentPausedTime = DateTime.Now - pauseStartTime;
                var elapsed = startTime != DateTime.MinValue
                    ? DateTime.Now - startTime - totalPausedTime - currentPausedTime
                    : TimeSpan.Zero;
                DrawMillProgress(false, visitedCells, elapsed, startLine);
            }

            // Subscribe to controller events
            Action<ControllerState> onStateChanged = state =>
            {
                Logger.Log("ToolChange state: {0}", state);
                if (state == ControllerState.Completed)
                {
                    completed = true;
                    success = true;
                }
                else if (state == ControllerState.Failed || state == ControllerState.Cancelled)
                {
                    completed = true;
                    success = false;
                }
            };

            Action<ProgressInfo> onProgressChanged = progress =>
            {
                // Update status action for display
                _toolChangeStatusAction = progress.Message;
                _toolChangeOverlayMessage = null;
                _toolChangeOverlaySubMessage = null;
                RefreshDisplay();
            };

            Action<UserInputRequest> onUserInputRequired = request =>
            {
                // Store the request - we'll handle it in the main loop
                pendingInput = request;
                Logger.Log("ToolChange user input required: {0}", request.Message);
            };

            Action<ControllerError> onError = error =>
            {
                Logger.Log("ToolChange error: {0}", error.Message);
                _toolChangeOverlayMessage = error.Message;
                _toolChangeOverlaySubMessage = "Esc=Abort";
                RefreshDisplay();
            };

            toolChangeController.StateChanged += onStateChanged;
            toolChangeController.ProgressChanged += onProgressChanged;
            toolChangeController.UserInputRequired += onUserInputRequired;
            toolChangeController.ErrorOccurred += onError;

            try
            {
                // Start tool change controller asynchronously
                var toolChangeTask = Task.Run(async () =>
                {
                    return await toolChangeController.HandleToolChangeAsync(tcInfo);
                });

                // Main loop - handle input and refresh display
                while (!completed)
                {
                    // Handle pending user input request
                    if (pendingInput != null)
                    {
                        var request = pendingInput;
                        pendingInput = null;

                        // Build tool info for overlay
                        string toolInfoStr = "TOOL CHANGE";
                        if (tcInfo.ToolNumber > 0 || tcInfo.ToolName != null)
                        {
                            string toolDetail = tcInfo.ToolNumber > 0 ? $"T{tcInfo.ToolNumber}" : "";
                            if (tcInfo.ToolName != null)
                            {
                                toolDetail += string.IsNullOrEmpty(toolDetail) ? tcInfo.ToolName : $" - {tcInfo.ToolName}";
                            }
                            toolInfoStr = $"TOOL CHANGE: {toolDetail}";
                        }

                        // Check if we're waiting for Z zero (Mode B) - allow jogging
                        bool isWaitingForZeroZ = toolChangeController.Phase == ToolChangePhase.WaitingForZeroZ;
                        string keyHint = isWaitingForZeroZ
                            ? "J=Jog  Y=Continue  Esc=Cancel"
                            : "Y=Continue  Esc=Cancel";

                        // Show overlay with prompt
                        _toolChangeOverlayMessage = toolInfoStr;
                        _toolChangeOverlaySubMessage = $"{request.Message}  {keyHint}";
                        _toolChangeStatusAction = null;
                        RefreshDisplay();

                        // Wait for user input (Y, X, or J if waiting for Z zero)
                        while (userResponse == null && !completed)
                        {
                            if (Console.KeyAvailable)
                            {
                                var key = Console.ReadKey(true);
                                if (InputHelpers.IsKey(key, ConsoleKey.Y))
                                {
                                    Logger.Log("ToolChange: Y pressed, continuing");
                                    userResponse = "Continue";
                                }
                                else if (InputHelpers.IsExitKey(key))
                                {
                                    Logger.Log("ToolChange: Escape pressed, aborting");
                                    userResponse = "Abort";
                                }
                                else if (isWaitingForZeroZ && InputHelpers.IsKey(key, ConsoleKey.J))
                                {
                                    Logger.Log("ToolChange: J pressed, opening jog menu");
                                    JogMenu.Show();
                                    // After returning from jog menu, refresh display and continue waiting
                                    Console.Clear();
                                    RefreshDisplay();
                                }
                            }
                            RefreshDisplay();
                            Thread.Sleep(StatusPollIntervalMs);
                        }

                        // Send the response to the controller
                        if (userResponse != null)
                        {
                            request.OnResponse(userResponse);
                            userResponse = null;
                        }

                        // Clear overlay
                        _toolChangeOverlayMessage = null;
                        _toolChangeOverlaySubMessage = null;
                    }

                    RefreshDisplay();
                    Thread.Sleep(StatusPollIntervalMs);
                }

                // Wait for task to complete
                toolChangeTask.Wait();

                // Clear display state
                _toolChangeOverlayMessage = null;
                _toolChangeOverlaySubMessage = null;
                _toolChangeStatusAction = null;

                return success;
            }
            finally
            {
                toolChangeController.StateChanged -= onStateChanged;
                toolChangeController.ProgressChanged -= onProgressChanged;
                toolChangeController.UserInputRequired -= onUserInputRequired;
                toolChangeController.ErrorOccurred -= onError;
            }
        }
    }
}
