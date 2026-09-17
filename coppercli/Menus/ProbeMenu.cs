using System.Threading;
using coppercli.Core.Controllers;
using coppercli.Core.GCode;
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
    /// The grid-probing screen. It starts `ProbeController` on a task and then polls: the
    /// controller runs the probe, this file draws the grid and reads the keyboard.
    /// </summary>
    internal static class ProbeMenu
    {
        private enum ProbeAction
        {
            ContinueProbing,
            ClearProbeData,
            ClearAndStartProbing,
            StartProbing,
            LoadFromFile,
            RecoverAutosave,
            SaveToFile,
            ApplyToGCode,
            Back
        }

        /// <summary>The prompt the run is waiting on, drawn by the wait loop.</summary>
        private static UserInputRequest? _pendingPrompt;

        /// <summary>
        /// The run's last progress update. The controller supplies the wording, which this
        /// screen draws rather than deriving it from the machine status.
        /// </summary>
        private static ProgressInfo? _latestProgress;

        private static Task? _probeTask;
        private static CancellationTokenSource? _probeCts;

        public static void Show()
        {
            var machine = AppState.Machine;

            // Auto-clear sends $X, which would clear an alarm the operator needs to see
            // while the probe is down. Restored on exit, as in the other menus.
            bool autoStateClear = machine.EnableAutoStateClear;
            machine.EnableAutoStateClear = false;

            try
            {
                // A map left on disk, by a server restart or a run holding the file, is
                // reported rather than dropped silently.
                string? notAdopted = AppState.EnsureProbeDataLoaded();
                if (notAdopted != null)
                {
                    MenuHelpers.ShowError(notAdopted);
                }

                while (true)
                {
                    Console.Clear();
                    AnsiConsole.Write(new Rule($"[{ColorBold} {ColorPrompt}]Probe[/]").RuleStyle(ColorPrompt));

                    var probePoints = AppState.ProbePoints;
                    var currentFile = AppState.CurrentFile;

                    bool hasIncomplete = HasIncompleteProbeData();
                    bool hasUnsaved = HasUnsavedCompleteProbe();

                    if (probePoints != null)
                    {
                        AnsiConsole.WriteLine(probePoints.GetInfo());
                        if (hasUnsaved)
                        {
                            AnsiConsole.MarkupLine($"[{ColorWarning}]{ProbeStatusUnsaved}[/]");
                        }
                        else if (!AppState.AreProbePointsApplied && currentFile != null)
                        {
                            AnsiConsole.MarkupLine($"[{ColorWarning}]{ProbeStatusNotApplied}[/]");
                        }
                        else if (AppState.AreProbePointsApplied)
                        {
                            AnsiConsole.MarkupLine($"[{ColorSuccess}]{ProbeStatusApplied}[/]");
                        }
                    }
                    else if (hasIncomplete)
                    {
                        AnsiConsole.MarkupLine($"[{ColorWarning}]{ProbeStatusIncomplete}[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[{ColorDim}]{ProbeStatusNoData}[/]");
                    }

                    if (currentFile == null)
                    {
                        AnsiConsole.MarkupLine($"[{ColorDim}]{ProbeStatusNoFile}[/]");
                    }

                    if (!AppState.IsWorkZeroSet)
                    {
                        AnsiConsole.MarkupLine($"[{ColorDim}]{ProbeStatusNoZero}[/]");
                    }

                    AnsiConsole.WriteLine();

                    var menu = BuildProbeMenu(hasIncomplete, hasUnsaved);
                    var choice = MenuHelpers.ShowMenu(ProbeMenuHeader, menu);

                    switch (choice.Option)
                    {
                        case ProbeAction.ContinueProbing:
                            if (ContinueProbing())
                            {
                                return;
                            }
                            break;
                        case ProbeAction.ClearProbeData:
                            ClearProbeData();
                            break;
                        case ProbeAction.ClearAndStartProbing:
                            // Asked before the discard, so a partial probe is not lost only
                            // for probing to turn out to be blocked.
                            if (ProbingIsBlocked())
                            {
                                break;
                            }

                            if (!ClearProbeData())
                            {
                                break;
                            }

                            if (StartProbing())
                            {
                                return;
                            }
                            break;
                        case ProbeAction.StartProbing:
                            if (StartProbing())
                            {
                                return;
                            }
                            break;
                        case ProbeAction.LoadFromFile:
                            if (LoadProbeGrid())
                            {
                                return;
                            }
                            break;
                        case ProbeAction.RecoverAutosave:
                            RecoverFromAutosave();
                            break;
                        case ProbeAction.SaveToFile:
                            PromptSaveProbeData();
                            break;
                        case ProbeAction.ApplyToGCode:
                            ApplyProbeGrid();
                            break;
                        case ProbeAction.Back:
                            return;
                    }
                }
            }
            finally
            {
                machine.EnableAutoStateClear = autoStateClear;
            }
        }

        private static MenuDef<ProbeAction> BuildProbeMenu(bool hasIncomplete, bool hasUnsaved)
        {
            var menu = new MenuDef<ProbeAction>();

            if (hasUnsaved)
            {
                menu.Add(new MenuItem<ProbeAction>(ProbeMenuSaveUnsaved, 's', ProbeAction.SaveToFile));
                menu.Add(new MenuItem<ProbeAction>(ProbeMenuDiscard, 'x', ProbeAction.ClearProbeData));
            }

            if (hasIncomplete)
            {
                menu.Add(new MenuItem<ProbeAction>(ProbeMenuContinue, 'c', ProbeAction.ContinueProbing,
                    Blocker: MenuHelpers.GetProbeDisabledReason));
                if (!hasUnsaved)  // Discard is already on the menu above
                {
                    menu.Add(new MenuItem<ProbeAction>(ProbeMenuDiscard, 'x', ProbeAction.ClearProbeData));
                }
                menu.Add(new MenuItem<ProbeAction>(ProbeMenuDiscardAndStart, 'p', ProbeAction.ClearAndStartProbing,
                    Blocker: MenuHelpers.GetProbeDisabledReason));
            }
            else
            {
                menu.Add(new MenuItem<ProbeAction>(ProbeMenuStart, 'p', ProbeAction.StartProbing,
                    Blocker: MenuHelpers.GetProbeDisabledReason));
            }

            menu.Add(new MenuItem<ProbeAction>(ProbeMenuLoad, 'l', ProbeAction.LoadFromFile));

            // Recover is offered only for a map this job can use, the same check
            // `ForceLoadProbeFromAutosave` applies.
            if (AppState.ReadUsableAutosave() != null)
            {
                menu.Add(new MenuItem<ProbeAction>(ProbeMenuRecover, 'r', ProbeAction.RecoverAutosave,
                    Blocker: MenuHelpers.GetProbeDisabledReason));
            }

            bool hasComplete = ProbeGrid.StateOf(AppState.CurrentProbeGrid) == ProbeDataState.Complete;

            if (hasComplete && !hasUnsaved)
            {
                menu.Add(new MenuItem<ProbeAction>(ProbeMenuSave, 's', ProbeAction.SaveToFile));
            }

            if (hasComplete && AppState.CurrentFile != null && !AppState.AreProbePointsApplied)
            {
                menu.Add(new MenuItem<ProbeAction>(ProbeMenuApply, 'a', ProbeAction.ApplyToGCode));
            }

            menu.Add(new MenuItem<ProbeAction>(ProbeMenuBack, 'q', ProbeAction.Back));

            return menu;
        }

        /// <summary>A map for this job with points still to measure.</summary>
        internal static bool HasIncompleteProbeData() =>
            // `CurrentProbeGrid` is what ContinueProbing acts on: the map in memory, or the
            // autosave when nothing is loaded. The autosave alone would not resume a map
            // loaded from a file.
            ProbeGrid.StateOf(AppState.CurrentProbeGrid) is ProbeDataState.Partial or ProbeDataState.Ready;

        /// <summary>An autosaved map for this job that the operator has not saved.</summary>
        private static bool HasUnsavedCompleteProbe() =>
            ProbeGrid.StateOf(AppState.ReadUsableAutosave()) == ProbeDataState.Complete;

        /// <summary>
        /// Whether probing is blocked right now, showing the reason if it is. The menu's
        /// enabled state is from when it was drawn, so every action checks again.
        /// </summary>
        private static bool ProbingIsBlocked()
        {
            string? blocked = MenuHelpers.GetProbeDisabledReason();
            if (blocked == null)
            {
                return false;
            }

            MenuHelpers.ShowError(blocked);
            return true;
        }

        /// <returns>True once the data is discarded; false once the refusal has been shown.</returns>
        private static bool ClearProbeData()
        {
            string? notDiscarded = AppState.DiscardProbeDataAndAutosave();
            if (notDiscarded != null)
            {
                MenuHelpers.ShowError(notDiscarded);
                return false;
            }

            AnsiConsole.MarkupLine($"[{ColorWarning}]{ProbeStatusCleared}[/]");
            return true;
        }

        /// <returns>True when a complete grid was loaded, which sends the caller back to
        /// the main menu.</returns>
        private static bool LoadProbeGrid()
        {
            var path = FileMenu.BrowseForProbeGridFile();
            if (path == null)
            {
                return false;
            }

            try
            {
                // `LoadProbeGridFromFile` reloads the original G-code first where a grid was
                // already applied, so this grid is not applied on top of the old one.
                var (grid, refused) = AppState.LoadProbeGridFromFile(path);
                if (grid == null)
                {
                    MenuHelpers.ShowError(refused!);
                    return false;
                }

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    AppState.Session.LastProbeBrowseDirectory = dir;
                }
                Persistence.SaveSession();

                AnsiConsole.MarkupLine($"[{ColorSuccess}]{ProbeStatusLoaded}[/]");
                AnsiConsole.WriteLine(grid.GetInfo());

                if (grid.HasCompleteData)
                {
                    string? notApplied = AppState.ApplyProbeData();
                    if (notApplied != null)
                    {
                        MenuHelpers.ShowError(notApplied);
                        return false;
                    }

                    AnsiConsole.MarkupLine($"[{ColorSuccess}]{ProbeStatusAppliedSuccess}[/]");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                MenuHelpers.ShowFailure(CliConstants.FailedLoadingTheHeightMap, ex);
                MenuHelpers.WaitEnter();
                return false;
            }
        }

        private static void RecoverFromAutosave()
        {
            var (grid, refused) = AppState.ForceLoadProbeFromAutosave();
            if (grid == null)
            {
                MenuHelpers.ShowError(refused!);
                return;
            }

            AnsiConsole.MarkupLine($"[{ColorSuccess}]{string.Format(ProbeStatusRecovered, grid.Progress, grid.TotalPoints)}[/]");
            AnsiConsole.WriteLine(grid.GetInfo());
        }

        private static void ApplyProbeGrid()
        {
            string? notApplied = AppState.ApplyProbeData();
            if (notApplied != null)
            {
                MenuHelpers.ShowError(notApplied);
                return;
            }

            AnsiConsole.MarkupLine($"[{ColorSuccess}]{ProbeStatusAppliedSuccess}[/]");
        }

        /// <returns>True when milling ran, which sends the caller back to the main menu.</returns>
        internal static bool ContinueProbing()
        {
            if (ProbingIsBlocked())
            {
                return false;
            }

            string? notAdopted = AppState.EnsureProbeDataLoaded();
            if (notAdopted != null)
            {
                MenuHelpers.ShowError(notAdopted);
                return false;
            }

            var probePoints = AppState.ProbePoints;

            if (probePoints == null)
            {
                MenuHelpers.ShowError(ProbeErrorNoIncomplete);
                return false;
            }

            if (probePoints.HasCompleteData)
            {
                AnsiConsole.MarkupLine($"[{ColorWarning}]{ProbeStatusComplete}[/]");
                MenuHelpers.WaitEnter();
                return false;
            }

            AnsiConsole.MarkupLine($"[{ColorSuccess}]{string.Format(ProbeFormatResume, probePoints.Progress, probePoints.TotalPoints)}[/]");

            return RunProbeController(probePoints, traceOutline: false);
        }

        /// <returns>True when milling ran, which sends the caller back to the main menu.</returns>
        private static bool StartProbing()
        {
            if (ProbingIsBlocked())
            {
                return false;
            }

            var currentFile = AppState.CurrentFile!;

            var margin = MenuHelpers.AskDouble(ProbePromptMargin, DefaultProbeMargin);
            if (margin == null)
            {
                return false;
            }

            var gridSize = MenuHelpers.AskDouble(ProbePromptGridSize, DefaultProbeGridSize);
            if (gridSize == null)
            {
                return false;
            }

            if (!CreateProbeGrid(margin.Value, gridSize.Value))
            {
                return false;
            }

            var traceChoice = MenuHelpers.ConfirmOrQuit(ProbePromptTraceOutline, true);
            if (traceChoice == null)
            {
                return false;
            }

            if (SleepPrevention.ShouldWarn())
            {
                var proceed = MenuHelpers.ConfirmOrQuit(
                    $"[{ColorWarning}]{SleepPreventionWarning}[/]. Continue?",
                    false);
                if (proceed != true)
                {
                    return false;
                }
            }

            return RunProbeController(AppState.ProbePoints!, traceOutline: traceChoice == true);
        }

        private static bool RunProbeController(ProbeGrid grid, bool traceOutline)
        {
            var controller = AppState.Probe;
            var settings = AppState.Settings;

            controller.Options = ProbeOptions.FromSettings(settings, traceOutline);

            // The controller fills this same object as it probes; the draw loop reads it.
            controller.LoadGrid(grid);

            _pendingPrompt = null;
            _latestProgress = null;
            controller.PointCompleted += OnPointCompleted;
            controller.PhaseChanged += OnPhaseChanged;
            controller.ProgressChanged += OnProgressChanged;
            controller.ErrorOccurred += OnErrorOccurred;
            controller.UserInputRequired += OnUserInputRequired;

            SleepPrevention.Start();

            _probeCts = new CancellationTokenSource();
            bool completed = false;
            bool cancelled = false;

            try
            {
                _probeTask = controller.StartAsync(_probeCts.Token);

                AnsiConsole.MarkupLine($"[{ColorSuccess}]{ProbeStatusStarted}[/]");

                WaitForProbeComplete(controller, grid);

                completed = controller.State == ControllerState.Completed;
                cancelled = controller.State == ControllerState.Cancelled;
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (Exception ex)
            {
                MenuHelpers.ShowFailureAndWait(CliConstants.FailedProbing, ex);
            }
            finally
            {
                controller.PointCompleted -= OnPointCompleted;
                controller.PhaseChanged -= OnPhaseChanged;
                controller.ProgressChanged -= OnProgressChanged;
                controller.ErrorOccurred -= OnErrorOccurred;
                controller.UserInputRequired -= OnUserInputRequired;
                _pendingPrompt = null;
                _latestProgress = null;
                SleepPrevention.Stop();
                _probeCts?.Dispose();
                _probeCts = null;
                _probeTask = null;

                try
                {
                    controller.ReleaseAsync().GetAwaiter().GetResult();
                }
                catch (Exception resetEx)
                {
                    Logger.Log("ProbeMenu: could not reset the probe controller - {0}", resetEx.Message);
                }
            }

            Console.WriteLine();

            if (cancelled)
            {
                AnsiConsole.MarkupLine($"[{ColorWarning}]{ProbeStatusStopped}[/]");
            }

            if (completed)
            {
                ShowProbeResults();
                if (MenuHelpers.ConfirmOrQuit(ProbePromptApply, true) == true)
                {
                    ApplyProbeGrid();
                    return OfferToMill();
                }
            }

            return false;
        }

        private static void WaitForProbeComplete(ProbeController controller, ProbeGrid grid)
        {
            int lastProgress = -1;

            // The message this loop last painted over the grid, which marks when the grid
            // underneath needs redrawing.
            string? shownOperatorMessage = null;
            bool wasPaused = false;

            while (controller.IsRunInProgress)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);

                    if (InputHelpers.IsEscapeKey(key))
                    {
                        _probeCts?.Cancel();
                        AnsiConsole.MarkupLine($"\n[{ColorWarning}]{ProbeStatusStopping}[/]");

                        try
                        {
                            _probeTask?.Wait(TimeSpan.FromMilliseconds(Constants.ControllerCancelTimeoutMs));
                        }
                        catch
                        {
                        }
                        break;
                    }
                    else if (InputHelpers.IsKey(key, ConsoleKey.Spacebar))
                    {
                        if (controller.State == ControllerState.Running)
                        {
                            controller.Pause();
                            AnsiConsole.MarkupLine($"\n[{ColorWarning}]{ProbeStatusPaused}[/]");
                        }
                        else if (controller.IsPaused)
                        {
                            controller.Resume();
                            AnsiConsole.MarkupLine($"\n[{ColorSuccess}]{ProbeStatusResumed}[/]");
                        }
                    }
                }

                // Draw before the prompt below, so the box sits over the probe screen.
                if (grid.Progress != lastProgress)
                {
                    lastProgress = grid.Progress;
                    DrawProbeMatrix(grid);
                }

                // The run leaves its prompt in `_pendingPrompt`; only this loop writes to
                // the screen, so it draws the overlay.
                var request = Interlocked.Exchange(ref _pendingPrompt, null);
                if (request != null)
                {
                    MenuHelpers.ShowPromptOverlay(request);

                    shownOperatorMessage = null;
                    DrawProbeMatrix(grid);
                    lastProgress = grid.Progress;
                    continue;
                }

                // The overlay covers the grid, and the run does not redraw until it has
                // retracted and traversed, so this loop restores the grid itself.
                var progress = Volatile.Read(ref _latestProgress);
                string? operatorMessage =
                    progress?.Phase == ControllerConstants.PhaseWaitingForOperator
                        ? progress.Message
                        : null;

                if (operatorMessage != shownOperatorMessage)
                {
                    shownOperatorMessage = operatorMessage;
                    if (operatorMessage != null)
                    {
                        DisplayHelpers.ShowOverlay(
                            operatorMessage, messageColor: DisplayHelpers.AnsiWarning);
                    }
                    else
                    {
                        DrawProbeMatrix(grid);
                        lastProgress = grid.Progress;
                    }
                }

                // Includes a pause the height check raised without a keypress.
                bool isPaused = controller.IsPaused;
                if (isPaused && !wasPaused)
                {
                    AnsiConsole.MarkupLine($"\n[{ColorWarning}]{ProbeStatusPaused}[/]");
                }
                wasPaused = isPaused;

                Thread.Sleep(StatusPollIntervalMs);
            }

            if (grid.HasCompleteData)
            {
                DrawProbeMatrix(grid);
            }
        }

        private static void OnPointCompleted(int index, Vector2 coords, double z)
        {
            Persistence.SaveProbeProgress();
            Logger.Log($"Probe point {index + 1} complete: ({coords.X:F3}, {coords.Y:F3}) Z={z:F3}");
        }

        private static void OnPhaseChanged(ProbePhase phase)
        {
            Logger.Log($"Probe phase: {phase}");
        }

        private static void OnUserInputRequired(UserInputRequest request)
        {
            Volatile.Write(ref _pendingPrompt, request);
        }

        private static void OnProgressChanged(ProgressInfo progress)
        {
            // Point progress is drawn from the grid itself. This is kept for the one thing
            // the grid does not carry: whether the run is waiting on the operator.
            Volatile.Write(ref _latestProgress, progress);
        }

        private static void OnErrorOccurred(ControllerError error)
        {
            MenuHelpers.ShowRunError(error);
        }

        private static bool CreateProbeGrid(double margin, double gridSize)
        {
            var currentFile = AppState.CurrentFile!;

            try
            {
                var (grid, refused) = AppState.SetupProbeGrid(
                    new Vector2(currentFile.Min.X, currentFile.Min.Y),
                    new Vector2(currentFile.Max.X, currentFile.Max.Y),
                    margin,
                    gridSize);

                if (grid == null)
                {
                    MenuHelpers.ShowError(refused!);
                    return false;
                }

                AnsiConsole.MarkupLine($"[{ColorSuccess}]{string.Format(ProbeFormatGrid, grid.SizeX, grid.SizeY, grid.TotalPoints)}[/]");
                AnsiConsole.MarkupLine($"[{ColorDim}]{string.Format(ProbeFormatBounds, grid.Min.X, grid.Max.X, grid.Min.Y, grid.Max.Y)}[/]");
                return true;
            }
            catch (Exception ex)
            {
                MenuHelpers.ShowFailureAndWait(CliConstants.FailedSettingUpTheGrid, ex);
                return false;
            }
        }

        private static string AnsiRgb(int r, int g, int b) => $"\x1b[38;2;{r};{g};{b}m";

        private static void DrawProbeMatrix(ProbeGrid probePoints)
        {
            // This runs on the UI thread while probing removes points on another, so
            // enumerating the live queue would throw and abandon the run partway through.
            var unprobed = new HashSet<(int, int)>(probePoints.SnapshotRemaining());

            var (winWidth, winHeight) = GetSafeWindowSize();
            int maxWidth = Math.Min((winWidth - ProbeGridConsolePadding) / 2, ProbeGridMaxDisplayWidth);
            int maxHeight = Math.Min(winHeight - ProbeGridHeaderPadding, ProbeGridMaxDisplayHeight);

            int stepX = Math.Max(1, (probePoints.SizeX + maxWidth - 1) / maxWidth);
            int stepY = Math.Max(1, (probePoints.SizeY + maxHeight - 1) / maxHeight);

            int matrixWidth = ((probePoints.SizeX + stepX - 1) / stepX) * 2;
            int leftPadding = Math.Max(0, (winWidth - matrixWidth) / 2);
            string pad = new string(' ', leftPadding);

            double minZ = probePoints.MinHeight;
            double maxZ = probePoints.MaxHeight;
            double rangeZ = maxZ - minZ;
            bool hasRange = probePoints.HasValidHeights && rangeZ > Constants.HeightRangeEpsilon;

            Console.Clear();
            string zRange = probePoints.HasValidHeights
                ? $"Z: {probePoints.MinHeight:F3} to {probePoints.MaxHeight:F3}"
                : ProbeDisplayZNoData;
            string header = $"{ProbeDisplayHeader} {probePoints.Progress}/{probePoints.TotalPoints} | {zRange}";
            int headerPad = Math.Max(0, (winWidth - header.Length) / 2);
            Console.WriteLine();
            AnsiConsole.MarkupLine(new string(' ', headerPad) + $"[{ColorBold}]{header}[/]");
            AnsiConsole.MarkupLine(new string(' ', headerPad) + $"[{ColorDim}]{ProbeDisplayEscapeStop}[/]");

            if (hasRange)
            {
                var (rLow, gLow, bLow) = HeightGradient.Color(0.0);
                var (rMid, gMid, bMid) = HeightGradient.Color(0.5);
                var (rHigh, gHigh, bHigh) = HeightGradient.Color(1.0);
                double midZ = (minZ + maxZ) / 2;
                string legend = $"{AnsiRgb(rLow, gLow, bLow)}██{AnsiReset} {minZ:F3}  " +
                                $"{AnsiRgb(rMid, gMid, bMid)}██{AnsiReset} {midZ:F3}  " +
                                $"{AnsiRgb(rHigh, gHigh, bHigh)}██{AnsiReset} {maxZ:F3}";
                int legendDisplayLen = CalculateDisplayLength(legend);
                int legendPad = Math.Max(0, (winWidth - legendDisplayLen) / 2);
                Console.WriteLine(new string(' ', legendPad) + legend);
            }

            Console.WriteLine();

            for (int y = probePoints.SizeY - 1; y >= 0; y -= stepY)
            {
                var line = new System.Text.StringBuilder();
                line.Append(pad);
                for (int x = 0; x < probePoints.SizeX; x += stepX)
                {
                    double heightSum = 0;
                    int heightCount = 0;
                    bool hasUnprobed = false;

                    for (int dy = 0; dy < stepY && y - dy >= 0; dy++)
                    {
                        for (int dx = 0; dx < stepX && x + dx < probePoints.SizeX; dx++)
                        {
                            int px = x + dx;
                            int py = y - dy;
                            if (unprobed.Contains((px, py)))
                            {
                                hasUnprobed = true;
                            }
                            else
                            {
                                var h = probePoints.Points[px, py];
                                if (h.HasValue)
                                {
                                    heightSum += h.Value;
                                    heightCount++;
                                }
                            }
                        }
                    }

                    if (hasUnprobed || heightCount == 0)
                    {
                        line.Append(AnsiDim).Append("··").Append(AnsiReset);
                    }
                    else
                    {
                        double avgHeight = heightSum / heightCount;
                        double t = hasRange ? (avgHeight - minZ) / rangeZ : 0.5;
                        var (r, g, b) = HeightGradient.Color(t);
                        line.Append(AnsiRgb(r, g, b)).Append("██").Append(AnsiReset);
                    }
                }
                Console.WriteLine(line.ToString());
            }
        }

        private static void ShowProbeResults()
        {
            var probePoints = AppState.ProbePoints;

            if (probePoints == null)
            {
                return;
            }

            AnsiConsole.MarkupLine($"[{ColorSuccess}]{ProbeStatusCompleteSuccess}[/]");
            AnsiConsole.MarkupLine($"  {ProbeDisplayPoints} {probePoints.TotalPoints}");
            if (probePoints.HasValidHeights)
            {
                AnsiConsole.MarkupLine($"  {ProbeDisplayZRange} {probePoints.MinHeight:F3} to {probePoints.MaxHeight:F3} {ProbeDisplayMm}");
                AnsiConsole.MarkupLine($"  {ProbeDisplayVariance} {probePoints.MaxHeight - probePoints.MinHeight:F3} {ProbeDisplayMm}");
            }
            AnsiConsole.WriteLine();

            PromptSaveProbeData();
        }

        private static void PromptSaveProbeData()
        {
            var session = AppState.Session;
            var currentFile = AppState.CurrentFile;

            // `CurrentProbeGrid` is the map the Save entry was offered for: the one in
            // memory, or the autosave when nothing is loaded. `ProbePoints` alone reports
            // nothing to save for an autosaved map the menu has just offered to save.
            if (ProbeGrid.StateOf(AppState.CurrentProbeGrid) != ProbeDataState.Complete)
            {
                MenuHelpers.ShowError(ProbeErrorNoComplete);
                return;
            }

            string defaultFilename;
            if (currentFile != null && !string.IsNullOrEmpty(currentFile.FileName))
            {
                defaultFilename = Path.GetFileNameWithoutExtension(currentFile.FileName) + ProbeGridExtension;
            }
            else
            {
                defaultFilename = DateTime.Now.ToString(ProbeDateFormat) + ProbeGridExtension;
            }

            var path = FileMenu.BrowseForSaveLocation(
                ProbeGridExtensions,
                defaultFilename,
                session.LastProbeBrowseDirectory);

            if (path == null)
            {
                AnsiConsole.MarkupLine($"[{ColorWarning}]{ProbeStatusNotSaved}[/]");
                Thread.Sleep(ConfirmationDisplayMs);
                return;
            }

            if (File.Exists(path))
            {
                if (!MenuHelpers.Confirm(string.Format(ProbeFormatOverwrite, Path.GetFileName(path)), true))
                {
                    AnsiConsole.MarkupLine($"[{ColorWarning}]{ProbeStatusNotSaved}[/]");
                    Thread.Sleep(ConfirmationDisplayMs);
                    return;
                }
            }

            if (Persistence.SaveProbeToFile(path))
            {
                AnsiConsole.MarkupLine($"[{ColorSuccess}]{string.Format(ProbeFormatSaved, Markup.Escape(path))}[/]");

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    session.LastProbeBrowseDirectory = dir;
                    Persistence.SaveSession();
                }
                Thread.Sleep(ConfirmationDisplayMs);
            }
            else
            {
                AnsiConsole.MarkupLine($"[{ColorError}]{string.Format(ProbeFormatSaveError, "Failed to save probe data")}[/]");
                MenuHelpers.WaitEnter();
            }
        }

        /// <returns>True when milling ran.</returns>
        private static bool OfferToMill()
        {
            var currentFile = AppState.CurrentFile;
            if (currentFile != null && currentFile.ContainsMotion && AppState.AreProbePointsApplied)
            {
                if (MenuHelpers.ConfirmOrQuit(ProbePromptMill, true) == true)
                {
                    MillMenu.Show();
                    return true;
                }
            }
            return false;
        }
    }
}
