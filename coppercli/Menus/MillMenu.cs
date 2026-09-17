// Mill menu - thin wrapper around MillingController
// Controller owns workflow logic, TUI owns display and user interaction

using System.Linq;
using System.Threading;
using coppercli.Core.Communication;
using coppercli.Core.Controllers;
using coppercli.Core.Util;
using coppercli.Helpers;
using Spectre.Console;
using static coppercli.CliConstants;
using static coppercli.Core.Util.Constants;
using static coppercli.Core.Controllers.ControllerConstants;
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
        private static ToolChangeInfo? _pendingToolChange;
        private static UserInputRequest? _pendingPrompt;
        private static ControllerError? _latestError;

        // ETA is estimated only while actually milling. The clock starts when streaming
        // begins - not before, or the setup phases (settle, home, retract) would pollute
        // the pace the estimate is built from.
        private static EtaEstimator? _etaEstimator;
        /// <summary>
        /// When the run began streaming, on the monotonic clock. The ETA is computed from
        /// this, and a wall clock that steps would move it by an hour mid-job.
        /// </summary>
        private static long? _millStreamStartMs;

        /// <summary>
        /// The paused total when streaming began. The ETA's clock starts later than the
        /// display's, so subtracting the whole paused total made the first estimates negative.
        /// </summary>
        private static long _millStreamPausedAtStartMs;

        // Tool change display state (replaces ToolChangeHelpers static properties)
        private static string? _toolChangeOverlayMessage;
        private static string? _toolChangeOverlaySubMessage;
        private static string? _toolChangeStatusAction;

        public static void Show()
        {
            var machine = AppState.Machine;

            // Auto-clear sends $X, which would clear an alarm the operator has to see.
            machine.EnableAutoStateClear = false;

            try
            {
                // Can the job start? Shared with the web server.
                var canStart = MenuHelpers.CheckMillCanStart();

                // Blocking errors, mapped in one place shared with the disabled-reason
                // display. The milling controller decides whether the machine's state
                // allows a job: it handles the enclosure and settles the machine.
                string? blockingReason = MenuHelpers.GetMillBlockerReason(canStart);
                if (blockingReason != null)
                {
                    MenuHelpers.ShowError(blockingReason);
                    return;
                }

                // Handle dangerous command warnings (prompt user)
                if (canStart.Warnings.Contains(MillWarning.DangerousCommands) &&
                    canStart.DangerousWarnings?.Count > 0)
                {
                    AnsiConsole.MarkupLine($"[{ColorError}]WARNING: File contains potentially dangerous commands:[/]");
                    foreach (var warning in canStart.DangerousWarnings)
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
                if (canStart.Warnings.Contains(MillWarning.NoMachineProfile))
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
            _pendingToolChange = null;
            _pendingPrompt = null;
            _latestError = null;
            _etaEstimator = null;
            _millStreamStartMs = null;
            _millStreamPausedAtStartMs = 0;

            // TUI state for display
            bool paused = false;
            var visitedCells = new HashSet<(int, int)>();
            var startMs = Environment.TickCount64;
            var pauseStartMs = Environment.TickCount64;
            long totalPausedMs = 0;
            var (lastWidth, lastHeight) = GetSafeWindowSize();

            // Cancellation token for stopping the controller. Not wrapped in `using`:
            // if the bounded wait in the finally below times out, the controller may
            // still be handing this token to a linked source on a background thread,
            // and disposing under it there throws ObjectDisposedException on that
            // thread. Disposing it isn't what makes the run stop, so let it be
            // collected normally instead.
            var cts = new CancellationTokenSource();
            Task? millTask = null;

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
                Logger.Log("Controller state: {0}", state);
            };
            Action<ProgressInfo> onProgressChanged = progress =>
            {
                Volatile.Write(ref _latestProgress, progress);
            };
            Action<ToolChangeInfo> onToolChange = info =>
            {
                _pendingToolChange = info;
                Logger.Log("Tool change detected: T{0} at line {1}", info.ToolNumber, info.LineNumber);
            };
            // The milling controller's own prompt (M0/M1), distinct from the tool-change
            // controller ToolChangeDetected hands off to - RunToolChangeController below
            // subscribes to a different controller instance's UserInputRequired.
            Action<UserInputRequest> onUserInputRequired = request =>
            {
                Volatile.Write(ref _pendingPrompt, request);
                Logger.Log("Mill controller user input required: {0}", request.Message);
            };
            Action<ControllerError> onError = error =>
            {
                _latestError = error;
                Logger.Log("Controller error: {0}", error.Message);
            };

            controller.StateChanged += onStateChanged;
            controller.ProgressChanged += onProgressChanged;
            controller.ToolChangeDetected += onToolChange;
            controller.UserInputRequired += onUserInputRequired;
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
                    DrawMillProgress(false, visitedCells, TimeSpan.Zero, EtaUnknown, safetyMsg, SafetyDepthSubMessage);

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
                        if (InputHelpers.IsKey(key, ConsoleKey.DownArrow))
                        {
                            AppState.AdjustDepthDeeper();
                            Logger.Log("Depth adjustment: {0:F2}mm (deeper)", AppState.DepthAdjustment);
                        }
                        if (InputHelpers.IsKey(key, ConsoleKey.UpArrow))
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
                controller.Options = MillingOptions.Create(currentFile?.FileName,
                    AppState.DepthAdjustment, AppState.Machine.IsHomed);

                Logger.Log("Starting controller: RequireHoming={0}, DepthAdjustment={1:F3}",
                    controller.Options.RequireHoming, controller.Options.DepthAdjustment);

                // Clear the last run off the controller, whatever state it left behind.
                controller.ReleaseAsync().GetAwaiter().GetResult();

                // Start controller. The task is kept (not fire-and-forget): the
                // finally below waits on it so shutdown blocks until the run's own
                // cleanup has actually finished, not merely until events say so.
                millTask = controller.StartAsync(cts.Token);

                // Record start time (drives the elapsed display).
                startMs = Environment.TickCount64;

                Console.Clear();

                // === MONITOR LOOP ===
                while (true)
                {
                    // Read from the controller, not from a copy an event has to update.
                    var state = controller.State;
                    if (ControllerBase.IsFinishedState(state))
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
                        pauseStartMs = Environment.TickCount64;

                        // Run tool change using ToolChangeController
                        bool success = RunToolChangeController(tcInfo, visitedCells, startMs, totalPausedMs);

                        if (success)
                        {
                            Logger.Log("Tool change completed successfully, resuming");
                            totalPausedMs += Environment.TickCount64 - pauseStartMs;
                            controller.Resume();
                        }
                        else
                        {
                            Logger.Log("Tool change aborted by user");
                            cts.Cancel();
                            return;
                        }
                    }

                    // Handle pending operator pause (M0/M1) - the mill controller's own
                    // prompt, not routed through RunToolChangeController since no
                    // separate tool-change workflow is running.
                    var pendingPrompt = Interlocked.Exchange(ref _pendingPrompt, null);
                    if (pendingPrompt != null)
                    {
                        var request = pendingPrompt;

                        Logger.Log("Handling operator pause: {0}", request.Message);
                        pauseStartMs = Environment.TickCount64;

                        bool continued = MenuHelpers.ShowPromptOverlay(request);
                        Console.Clear();

                        if (continued)
                        {
                            totalPausedMs += Environment.TickCount64 - pauseStartMs;
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
                                pauseStartMs = Environment.TickCount64;
                            }
                        }
                        else if (InputHelpers.IsKey(key, ConsoleKey.R))
                        {
                            if (ControllerBase.IsPausedState(state))
                            {
                                Logger.Log("Resuming");
                                controller.Resume();
                                totalPausedMs += Environment.TickCount64 - pauseStartMs;
                            }
                        }
                        else if (InputHelpers.IsExitKey(key))
                        {
                            Logger.Log("Stopping (Escape pressed)");
                            cts.Cancel();
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

                    // Derived from the controller, never tracked beside it, so a key press
                    // and a pause the run raised itself read the same.
                    paused = ControllerBase.IsPausedState(controller.State);

                    // Start the ETA clock the moment milling actually begins streaming,
                    // so setup time does not distort the pace it learns from.
                    if (_millStreamStartMs == null &&
                        Volatile.Read(ref _latestProgress)?.Phase == PhaseMilling &&
                        AppState.CurrentFile != null)
                    {
                        _millStreamStartMs = Environment.TickCount64;
                        _millStreamPausedAtStartMs = totalPausedMs;
                        _etaEstimator = new EtaEstimator(AppState.CurrentFile.TotalTime, machine.File.Count);
                    }

                    // One pause accounting, used for both clocks. The display elapsed runs
                    // from the moment the operator hit go; the ETA's clock runs from when
                    // milling actually began streaming, so it excludes only the pauses since
                    // then - see _millStreamPausedAtStartMs.
                    long currentPausedMs = paused ? Environment.TickCount64 - pauseStartMs : 0;
                    var elapsed = TimeSpan.FromMilliseconds(
                        Environment.TickCount64 - startMs - totalPausedMs - currentPausedMs);

                    string etaStr = EtaUnknown;
                    if (_etaEstimator != null && _millStreamStartMs != null)
                    {
                        var millingElapsed = TimeSpan.FromMilliseconds(
                            Environment.TickCount64 - _millStreamStartMs.Value
                            - (totalPausedMs - _millStreamPausedAtStartMs) - currentPausedMs);
                        var remaining = _etaEstimator.Update(machine.FilePosition, millingElapsed);
                        etaStr = remaining.HasValue ? FormatTimeSpan(remaining.Value) : EtaUnknown;
                    }

                    // Handle window resize
                    var (curWidth, curHeight) = GetSafeWindowSize();
                    if (curWidth != lastWidth || curHeight != lastHeight)
                    {
                        Console.Clear();
                        lastWidth = curWidth;
                        lastHeight = curHeight;
                    }

                    // What the run is doing when it is not cutting - settling, homing, or
                    // waiting out an open enclosure - drawn over the screen. The run is the only
                    // source: this screen does not read the machine and word it again. An
                    // empty message is the run withdrawing what it published, so it puts the
                    // progress bar back rather than drawing a blank box.
                    var progress = Volatile.Read(ref _latestProgress);
                    string? statusMessage =
                        progress != null && progress.Phase != PhaseMilling
                            && !string.IsNullOrEmpty(progress.Message)
                            ? progress.Message
                            : null;

                    DrawMillProgress(paused, visitedCells, elapsed, etaStr, statusMessage);
                    Thread.Sleep(StatusPollIntervalMs);
                }

                // === COMPLETION ===
                var finalState = controller.State;
                var finalElapsed = TimeSpan.FromMilliseconds(
                    Environment.TickCount64 - startMs - totalPausedMs);

                if (finalState == ControllerState.Completed)
                {
                    DrawMillProgress(false, visitedCells, finalElapsed, EtaUnknown);

                    // Offer to clear probe data after successful mill
                    if (AppState.ProbePoints != null)
                    {
                        if (ShowOverlayConfirm(ProbePromptClear, true) == true)
                        {
                            string? notDiscarded = AppState.DiscardProbeDataAndAutosave();
                            ShowOverlayTimed(
                                notDiscarded ?? ProbeStatusCleared,
                                ConfirmationDisplayMs,
                                messageColor: notDiscarded == null ? null : AnsiWarning);
                        }
                    }
                }
                else if (finalState == ControllerState.Failed && _latestError != null)
                {
                    MenuHelpers.ShowRunError(_latestError);
                }
            }
            finally
            {
                // Cancel unconditionally: a no-op once the controller has already
                // finished, but on an exit path that never touched cts (an exception
                // escaping the loop, say) it is what tells the controller to unwind.
                cts.Cancel();

                // Wait on the run's own task rather than polling IsActive: IsActive
                // excludes Completing, so polling it would return while the safe-stop,
                // re-home, and depth-adjustment restore that make up Completing are
                // still running. Waiting on the task returns only once that unwind has
                // finished, whatever state it ends in. Bounded so an
                // operator abort can never hang the TUI; a cancelled run's Wait can
                // throw AggregateException, and nothing on a machine-abort path may
                // escape this finally.
                bool stopped = true;
                try
                {
                    stopped = millTask == null
                        || millTask.Wait(TimeSpan.FromMilliseconds(ControllerCancelTimeoutMs));
                }
                catch
                {
                    // A cancelled run faults its task; that is the abort working, not a
                    // failure to stop.
                }

                if (!stopped)
                {
                    // Wait() reports a timeout by returning false rather than throwing, so
                    // without this the operator is not told the machine may still be
                    // moving.
                    Logger.Log("Milling teardown timed out after {0}ms", ControllerCancelTimeoutMs);
                    MenuHelpers.ShowError(StopTimedOutWarning);
                }

                // Runs on every exit (normal, abort, exception), so the controller is
                // always cleared. Logged rather than thrown, because the rest of this
                // finally unsubscribes and releases the machine.
                try
                {
                    controller.ReleaseAsync().GetAwaiter().GetResult();
                }
                catch (Exception releaseEx)
                {
                    Logger.Log("MillMenu: could not release the milling controller - {0}",
                        releaseEx.Message);
                }

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
                controller.UserInputRequired -= onUserInputRequired;
                controller.ErrorOccurred -= onError;

                Logger.Log("=== MonitorMilling ended ===");
                Console.CursorVisible = true;
            }
        }

        /// <summary>
        /// True for the terminal states a run can end in - Completed, Failed, or
        /// Cancelled. This is exactly the precondition <see cref="ControllerBase.Reset"/>
        /// itself requires (besides Idle, which needs no reset), so callers that guard
        /// a Reset() call with this can never hit the InvalidOperationException Reset()
        /// throws outside it.
        /// </summary>

        private static void DrawMillProgress(bool paused, HashSet<(int, int)> visitedCells, TimeSpan elapsed, string etaStr, string? statusMessage = null, string? statusSubMessage = null)
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

            // Status line text. MachineWait.GetActivity decides the cases; the words
            // below belong to this screen.
            var activity = MachineWait.GetActivity(machine);
            string statusDisplay = GetMillStatusText(activity, paused, machine.Status);

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
            WriteLineTruncated($"  {AnsiInfo}P{AnsiReset}=Pause  {AnsiInfo}R{AnsiReset}=Resume  {AnsiInfo}+/-/0{AnsiReset}=Feed  {AnsiAlert}Esc{AnsiReset}=Stop{feedOvrStr}", winWidth);

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
            else if (activity == MachineActivity.Hold)
            {
                overlayMessage = OverlayHoldMessage;
            }
            else if (activity == MachineActivity.Alarm)
            {
                overlayMessage = OverlayAlarmMessage;
                overlayColor = AnsiAlert;
            }

            DrawPositionGrid(gridWidth, gridHeight, gridX, gridY, visitedCells, winWidth, minX, maxX, minY, maxY, overlayMessage, overlayColor, overlaySubMessage);
        }

        /// <summary>
        /// The status line text. The last arm is not about the machine: a paused run, or a
        /// GRBL state this screen has no text for.
        /// </summary>
        private static string GetMillStatusText(MachineActivity activity, bool paused, string status)
        {
            // Hold has its own text here, because this screen can name the resume key.
            // Every other case uses the shared text.
            if (activity == MachineActivity.Hold)
            {
                return $"{AnsiWarning}{OverlayHoldMessage}{AnsiReset}";
            }

            if (MachineWait.NeedsAttention(activity))
            {
                string alert = activity is MachineActivity.Alarm or MachineActivity.DoorOpen
                    ? AnsiAlert
                    : AnsiWarning;
                return $"{alert}{DisplayHelpers.GetActivityText(activity, status)}{AnsiReset}";
            }

            return paused
                ? $"{AnsiWarning}{MillPausedStatus}{AnsiReset}"
                : $"{AnsiInfo}{DisplayHelpers.GetActivityText(activity, status)}{AnsiReset}";
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


        private static void DrawPositionGrid(int width, int height, int posX, int posY,
            HashSet<(int, int)> visited, int winWidth, double minX, double maxX, double minY, double maxY,
            string? overlayMessage = null, string overlayColor = AnsiWarning, string? overlaySubMessage = null)
        {
            int matrixWidth = width * MillGridCharsPerCell;
            int leftPadding = Math.Max(0, (winWidth - matrixWidth - MillBorderPadding) / 2);
            string pad = new string(' ', leftPadding);

            // The box is no wider than the grid, so the enclosure prompt is wrapped to fit
            // rather than cut off at the key that answers it.
            var (overlayLines, overlayColors) = BuildOverlayContent(
                overlayMessage ?? string.Empty, overlaySubMessage ?? StopKeyHint,
                overlayColor, matrixWidth);

            int boxWidth = CalculateOverlayBoxWidth(overlayLines, matrixWidth);
            int boxStartChar = (matrixWidth - boxWidth) / 2;

            // Center vertically in the grid (grid rows go from height-1 down to 0)
            int boxHeight = CalculateOverlayBoxHeight(overlayLines);
            int boxCenterRow = height / 2;
            int boxTopRow = boxCenterRow + boxHeight / 2;
            int boxBottomRow = boxTopRow - boxHeight + 1;

            WriteLineTruncated($"{pad}┌{new string('─', matrixWidth)}┐", winWidth);

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
                    string boxLine = GetOverlayBoxLine(
                        boxLineIndex, boxWidth, overlayLines, overlayColors);

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
            long startMs,
            long totalPausedMs)
        {
            var toolChangeController = AppState.ToolChange;

            // Clear the last tool change off the controller, whatever state it left behind.
            toolChangeController.ReleaseAsync().GetAwaiter().GetResult();

            // Set options from user settings and file bounds
            var settings = AppState.Settings;
            var currentFile = AppState.CurrentFile;
            toolChangeController.Options = ToolChangeOptions.FromSettings(settings, currentFile);

            // State for tracking tool change progress
            bool completed = false;
            bool success = false;
            UserInputRequest? pendingInput = null;
            string? userResponse = null;
            var pauseStartMs = Environment.TickCount64;

            // Helper to refresh display
            void RefreshDisplay()
            {
                long currentPausedMs = Environment.TickCount64 - pauseStartMs;
                var elapsed = TimeSpan.FromMilliseconds(
                    Environment.TickCount64 - startMs - totalPausedMs - currentPausedMs);
                DrawMillProgress(false, visitedCells, elapsed, EtaUnknown);
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
                _toolChangeStatusAction = string.Format(ToolChangeAbortHint, progress.Message);
                _toolChangeOverlayMessage = null;
                _toolChangeOverlaySubMessage = null;
                RefreshDisplay();
            };

            Action<UserInputRequest> onUserInputRequired = request =>
            {
                // Store the request - we'll handle it in the main loop. Written and read on
                // different threads, so both ends go through Volatile/Interlocked.
                Volatile.Write(ref pendingInput, request);
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
                // Not disposed: the bounded wait below can time out with the controller
                // still holding this token, and disposing under it throws on that thread.
                var toolChangeCts = new CancellationTokenSource();

                var toolChangeTask = Task.Run(async () =>
                {
                    return await toolChangeController.HandleToolChangeAsync(tcInfo, toolChangeCts.Token);
                });

                // Main loop - handle input and refresh display
                while (!completed)
                {
                    // Handle pending user input request
                    var request = Interlocked.Exchange(ref pendingInput, null);
                    if (request != null)
                    {

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

                        // Jogging is offered for the Z-zero step, and never for the
                        // enclosure: the jog screen handles the door itself, and this run is
                        // already parked on its own door prompt.
                        bool isWaitingForZeroZ =
                            toolChangeController.Phase == ToolChangePhase.WaitingForZeroZ
                            && !request.IsDoorPrompt;
                        string keyHint = isWaitingForZeroZ
                            ? JogContinueOrCancelKeyHint
                            : ContinueOrCancelKeyHint;

                        // The enclosure prompt arrives through this same slot with no
                        // title. Under the "TOOL CHANGE" heading it would read as a prompt
                        // about the tool.
                        if (request.IsDoorPrompt)
                        {
                            toolInfoStr = string.Empty;
                        }
                        else if (request.Title != ToolChangePromptTitle)
                        {
                            toolInfoStr = request.Title;
                        }

                        // Show overlay with prompt
                        _toolChangeOverlayMessage = toolInfoStr;
                        _toolChangeOverlaySubMessage = $"{request.Message}  {keyHint}";
                        _toolChangeStatusAction = null;
                        RefreshDisplay();

                        // Answering a prompt resumes the run on this thread, and the run can
                        // raise its next prompt before the answer returns. Throw away what is
                        // already typed, so the keystroke that answered the last one cannot
                        // answer this one - which for the enclosure prompt would restart the
                        // spindle.
                        InputHelpers.FlushKeyboard();

                        // Wait for user input (Y, X, or J if waiting for Z zero)
                        while (userResponse == null && !completed)
                        {
                            if (Console.KeyAvailable)
                            {
                                var key = Console.ReadKey(true);
                                if (InputHelpers.IsKey(key, ConsoleKey.Y))
                                {
                                    Logger.Log("ToolChange: Y pressed, continuing");
                                    userResponse = OptionContinue;
                                }
                                else if (InputHelpers.IsExitKey(key))
                                {
                                    Logger.Log("ToolChange: Escape pressed, aborting");
                                    userResponse = OptionAbort;
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

                    // Escape has to work while the spindle is taking itself to the tool
                    // setter and probing, not only while a prompt is up: those phases are
                    // the long part of a tool change, and the browser can already stop
                    // them. Read only when no prompt is pending, so this cannot swallow
                    // the keystroke that prompt is waiting for.
                    if (Volatile.Read(ref pendingInput) == null && Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(true);
                        if (InputHelpers.IsExitKey(key))
                        {
                            Logger.Log("ToolChange: abort requested mid-motion");
                            _toolChangeStatusAction = ToolChangeAbortingMessage;
                            toolChangeCts.Cancel();
                        }
                    }

                    RefreshDisplay();
                    Thread.Sleep(StatusPollIntervalMs);
                }

                // Wait for task to complete
                // Bounded: a tool change that never unwinds must not take the TUI with it.
                toolChangeTask.Wait(TimeSpan.FromMilliseconds(ControllerCancelTimeoutMs));

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
