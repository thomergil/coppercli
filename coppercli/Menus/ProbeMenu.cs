// Extracted from Program.cs

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
    /// Probe menu for grid probing workflow.
    /// Uses ProbeController for actual probing operations.
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

        // Controller task for async probing
        private static Task? _probeTask;
        private static CancellationTokenSource? _probeCts;

        public static void Show()
        {
            var machine = AppState.Machine;

            // Defense in depth: ensure auto-clear is disabled during probing
            machine.EnableAutoStateClear = false;

            // Load probe data from disk if needed (e.g., after server restart)
            AppState.EnsureProbeDataLoaded();

            while (true)
            {
                Console.Clear();
                AnsiConsole.Write(new Rule($"[{ColorBold} {ColorPrompt}]Probe[/]").RuleStyle(ColorPrompt));

                var probePoints = AppState.ProbePoints;
                var currentFile = AppState.CurrentFile;

                bool hasIncomplete = HasIncompleteProbeData();
                bool hasComplete = probePoints != null && probePoints.NotProbed.Count == 0;
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

                bool canProbe = currentFile != null && AppState.IsWorkZeroSet && machine.Connected;

                var menu = BuildProbeMenu(hasIncomplete, hasComplete, hasUnsaved, canProbe);
                var choice = MenuHelpers.ShowMenu(ProbeMenuHeader, menu);

                switch (choice.Option)
                {
                    case ProbeAction.ContinueProbing:
                        if (ContinueProbing())
                        {
                            return; // Milling completed, return to main menu
                        }
                        break;
                    case ProbeAction.ClearProbeData:
                        ClearProbeData();
                        break;
                    case ProbeAction.ClearAndStartProbing:
                        ClearProbeData();
                        if (StartProbing())
                        {
                            return; // Milling completed, return to main menu
                        }
                        break;
                    case ProbeAction.StartProbing:
                        if (StartProbing())
                        {
                            return; // Milling completed, return to main menu
                        }
                        break;
                    case ProbeAction.LoadFromFile:
                        if (LoadProbeGrid())
                        {
                            return; // Complete grid loaded, return to main menu
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

        private static MenuDef<ProbeAction> BuildProbeMenu(bool hasIncomplete, bool hasComplete, bool hasUnsaved, bool canProbe)
        {
            var menu = new MenuDef<ProbeAction>();

            // Show Save prominently at top when there's unsaved complete probe data
            if (hasUnsaved)
            {
                menu.Add(new MenuItem<ProbeAction>(ProbeMenuSaveUnsaved, 's', ProbeAction.SaveToFile));
                // Unsaved complete data: "Discard" since work would be lost
                menu.Add(new MenuItem<ProbeAction>(ProbeMenuDiscard, 'x', ProbeAction.ClearProbeData));
            }

            if (hasIncomplete)
            {
                // Partial state: Continue, Discard, or Discard+Start
                menu.Add(new MenuItem<ProbeAction>(ProbeMenuContinue, 'c', ProbeAction.ContinueProbing,
                    EnabledWhen: () => canProbe, DisabledReason: MenuHelpers.GetProbeDisabledReason));
                if (!hasUnsaved)  // Don't duplicate Discard
                {
                    // Partial data: "Discard" since work would be lost
                    menu.Add(new MenuItem<ProbeAction>(ProbeMenuDiscard, 'x', ProbeAction.ClearProbeData));
                }
                menu.Add(new MenuItem<ProbeAction>(ProbeMenuDiscardAndStart, 'p', ProbeAction.ClearAndStartProbing,
                    EnabledWhen: () => canProbe, DisabledReason: MenuHelpers.GetProbeDisabledReason));
            }
            else
            {
                // None or Complete state: Start new probing
                menu.Add(new MenuItem<ProbeAction>(ProbeMenuStart, 'p', ProbeAction.StartProbing,
                    EnabledWhen: () => canProbe, DisabledReason: MenuHelpers.GetProbeDisabledReason));
            }

            menu.Add(new MenuItem<ProbeAction>(ProbeMenuLoad, 'l', ProbeAction.LoadFromFile));

            // Show Recover option if autosave exists
            var autosaveState = Persistence.GetProbeState();
            if (autosaveState != Persistence.ProbeState.None)
            {
                menu.Add(new MenuItem<ProbeAction>(ProbeMenuRecover, 'r', ProbeAction.RecoverAutosave));
            }

            // Re-check hasComplete after potential menu state changes
            var probePoints = AppState.ProbePoints;
            hasComplete = probePoints != null && probePoints.NotProbed.Count == 0;

            // Show Save option (if not already shown as prominent unsaved option)
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

        /// <summary>
        /// Uses Persistence.GetProbeState() as single source of truth for probe state.
        /// Returns true if state is Partial (incomplete data exists).
        /// </summary>
        private static bool HasIncompleteProbeData()
        {
            return Persistence.GetProbeState() == Persistence.ProbeState.Partial;
        }

        /// <summary>
        /// Uses Persistence.GetProbeState() as single source of truth for probe state.
        /// Returns true if state is Complete (complete data exists but not saved by user).
        /// </summary>
        private static bool HasUnsavedCompleteProbe()
        {
            return Persistence.GetProbeState() == Persistence.ProbeState.Complete;
        }

        private static void ClearProbeData()
        {
            AppState.DiscardProbeData();
            Persistence.ClearProbeAutoSave();
            AnsiConsole.MarkupLine($"[{ColorWarning}]{ProbeStatusCleared}[/]");
        }

        /// <summary>
        /// Load probe grid from file. Returns true if a complete grid was loaded
        /// (caller should exit to main menu), false otherwise.
        /// </summary>
        private static bool LoadProbeGrid()
        {
            var path = FileMenu.BrowseForProbeGridFile();
            if (path == null)
            {
                return false;
            }

            try
            {
                AppState.ProbePoints = ProbeGrid.Load(path);

                // Don't copy to autosave - loaded data is already saved (came from a file).
                // Autosave is only for data from active probing that hasn't been saved yet.
                // Clear any stale autosave to prevent "unsaved probe data" prompts.
                Persistence.ClearProbeAutoSave();

                // Update probe browse directory (separate from G-code browse directory)
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    AppState.Session.LastProbeBrowseDirectory = dir;
                }
                Persistence.SaveSession();

                AppState.ResetProbeApplicationState();
                AnsiConsole.MarkupLine($"[{ColorSuccess}]{ProbeStatusLoaded}[/]");
                AnsiConsole.WriteLine(AppState.ProbePoints.GetInfo());

                // If complete, auto-apply and return to main menu
                if (AppState.ProbePoints.NotProbed.Count == 0)
                {
                    AppState.ApplyProbeData();
                    AnsiConsole.MarkupLine($"[{ColorSuccess}]{ProbeStatusAppliedSuccess}[/]");
                    return true;
                }

                // Incomplete: stay in probe menu
                return false;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[{ColorError}]Error: {Markup.Escape(ex.Message)}[/]");
                MenuHelpers.WaitEnter();
                return false;
            }
        }

        private static void RecoverFromAutosave()
        {
            var autosaveState = Persistence.GetProbeState();
            if (autosaveState == Persistence.ProbeState.None)
            {
                AnsiConsole.MarkupLine($"[{ColorWarning}]{ProbeErrorNoAutosave}[/]");
                MenuHelpers.WaitEnter();
                return;
            }

            try
            {
                var grid = AppState.ForceLoadProbeFromAutosave();
                AnsiConsole.MarkupLine($"[{ColorSuccess}]{string.Format(ProbeStatusRecovered, grid.Progress, grid.TotalPoints)}[/]");
                AnsiConsole.WriteLine(grid.GetInfo());
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[{ColorError}]{string.Format(ProbeErrorRecoveryFailed, Markup.Escape(ex.Message))}[/]");
                MenuHelpers.WaitEnter();
            }
        }

        private static void ApplyProbeGrid()
        {
            if (AppState.CurrentFile == null)
            {
                MenuHelpers.ShowError(ProbeErrorNoFile);
                return;
            }

            if (AppState.ProbePoints == null || AppState.ProbePoints.NotProbed.Count > 0)
            {
                MenuHelpers.ShowError(ProbeErrorIncomplete);
                return;
            }

            try
            {
                AppState.ApplyProbeData();
                AnsiConsole.MarkupLine($"[{ColorSuccess}]{ProbeStatusAppliedSuccess}[/]");
            }
            catch (Exception ex)
            {
                MenuHelpers.ShowError($"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Continues an interrupted probing session using ProbeController.
        /// </summary>
        /// <returns>True if milling was performed (caller should exit to main menu).</returns>
        internal static bool ContinueProbing()
        {
            if (!MenuHelpers.RequireConnection())
            {
                return false;
            }

            if (!AppState.IsWorkZeroSet)
            {
                MenuHelpers.ShowError(ProbeErrorNoZero);
                return false;
            }

            // Auto-load from autosave if needed
            AppState.EnsureProbeDataLoaded();

            var probePoints = AppState.ProbePoints;

            if (probePoints == null)
            {
                MenuHelpers.ShowError(ProbeErrorNoIncomplete);
                return false;
            }

            if (probePoints.NotProbed.Count == 0)
            {
                AnsiConsole.MarkupLine($"[{ColorWarning}]{ProbeStatusComplete}[/]");
                MenuHelpers.WaitEnter();
                return false;
            }

            AnsiConsole.MarkupLine($"[{ColorSuccess}]{string.Format(ProbeFormatResume, probePoints.Progress, probePoints.TotalPoints)}[/]");

            return RunProbeController(probePoints, traceOutline: false);
        }

        /// <summary>
        /// Starts a new probing session using ProbeController.
        /// </summary>
        /// <returns>True if milling was performed (caller should exit to main menu).</returns>
        private static bool StartProbing()
        {
            if (!MenuHelpers.RequireConnection())
            {
                return false;
            }

            var currentFile = AppState.CurrentFile;

            if (currentFile == null)
            {
                MenuHelpers.ShowError(ProbeErrorNoFileLoad);
                return false;
            }

            if (!AppState.IsWorkZeroSet)
            {
                MenuHelpers.ShowError(ProbeErrorNoZero);
                return false;
            }

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

            // Warn if in network mode and sleep prevention unavailable
            if (SleepPrevention.ShouldWarn())
            {
                var proceed = MenuHelpers.ConfirmOrQuit(
                    $"[{ColorWarning}]{SleepPreventionWarning}[/]: {SleepPreventionSubMessage.Replace("Y=Continue  X=Cancel", "Continue?")}",
                    false);
                if (proceed != true)
                {
                    return false;
                }
            }

            return RunProbeController(AppState.ProbePoints!, traceOutline: traceChoice == true);
        }

        /// <summary>
        /// Runs the ProbeController with the given grid.
        /// Handles async controller with synchronous TUI input loop.
        /// </summary>
        private static bool RunProbeController(ProbeGrid grid, bool traceOutline)
        {
            var controller = AppState.Probe;
            var settings = AppState.Settings;

            // Configure controller options
            controller.Options = new ProbeOptions
            {
                SafeHeight = settings.ProbeSafeHeight,
                MaxDepth = settings.ProbeMaxDepth,
                ProbeFeed = settings.ProbeFeed,
                MinimumHeight = settings.ProbeMinimumHeight,
                AbortOnFail = settings.AbortOnProbeFail,
                XAxisWeight = settings.ProbeXAxisWeight,
                TraceOutline = traceOutline,
                TraceHeight = settings.OutlineTraceHeight,
                TraceFeed = settings.OutlineTraceFeed
            };

            // Load the grid into controller (same object reference - updates in place)
            controller.LoadGrid(grid);

            // Wire up event handlers
            controller.PointCompleted += OnPointCompleted;
            controller.PhaseChanged += OnPhaseChanged;
            controller.ProgressChanged += OnProgressChanged;
            controller.ErrorOccurred += OnErrorOccurred;

            // Start sleep prevention
            SleepPrevention.Start();

            _probeCts = new CancellationTokenSource();
            bool completed = false;
            bool cancelled = false;

            try
            {
                // Start controller async
                _probeTask = controller.StartAsync(_probeCts.Token);

                AnsiConsole.MarkupLine($"[{ColorSuccess}]{ProbeStatusStarted}[/]");

                // Poll for completion with UI updates
                WaitForProbeComplete(controller, grid);

                // Check final state
                completed = controller.State == ControllerState.Completed;
                cancelled = controller.State == ControllerState.Cancelled;
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"\n[{ColorError}]{string.Format(ProbeFormatError, Markup.Escape(ex.Message))}[/]");
            }
            finally
            {
                // Cleanup
                controller.PointCompleted -= OnPointCompleted;
                controller.PhaseChanged -= OnPhaseChanged;
                controller.ProgressChanged -= OnProgressChanged;
                controller.ErrorOccurred -= OnErrorOccurred;
                SleepPrevention.Stop();
                _probeCts?.Dispose();
                _probeCts = null;
                _probeTask = null;

                // Reset controller for next use
                controller.Reset();
            }

            Console.WriteLine();

            if (cancelled)
            {
                AnsiConsole.MarkupLine($"[{ColorWarning}]{ProbeStatusStopped}[/]");
            }

            if (completed)
            {
                // Probe data is in autosave, ready for user to save or discard
                ShowProbeResults();
                if (MenuHelpers.ConfirmOrQuit(ProbePromptApply, true) == true)
                {
                    ApplyProbeGrid();
                    return OfferToMill();
                }
            }

            return false;
        }

        /// <summary>
        /// Waits for probing to complete, handling ESC/Space keys and UI updates.
        /// </summary>
        private static void WaitForProbeComplete(ProbeController controller, ProbeGrid grid)
        {
            int lastProgress = -1;
            bool wasPaused = false;

            while (controller.State == ControllerState.Initializing ||
                   controller.State == ControllerState.Running ||
                   controller.State == ControllerState.Paused)
            {
                // Check for key presses
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);

                    if (InputHelpers.IsEscapeKey(key))
                    {
                        _probeCts?.Cancel();
                        AnsiConsole.MarkupLine($"\n[{ColorWarning}]{ProbeStatusStopping}[/]");

                        // Wait for controller to stop
                        try
                        {
                            _probeTask?.Wait(TimeSpan.FromMilliseconds(Constants.ControllerCancelTimeoutMs));
                        }
                        catch
                        {
                            // Ignore
                        }
                        break;
                    }
                    else if (key.Key == ConsoleKey.Spacebar)
                    {
                        // Toggle pause/resume
                        if (controller.State == ControllerState.Running)
                        {
                            controller.Pause();
                            AnsiConsole.MarkupLine($"\n[{ColorWarning}]{ProbeStatusPaused}[/]");
                        }
                        else if (controller.State == ControllerState.Paused)
                        {
                            controller.Resume();
                            AnsiConsole.MarkupLine($"\n[{ColorSuccess}]{ProbeStatusResumed}[/]");
                        }
                    }
                }

                // Show paused state change (e.g., auto-pause from slow probe)
                bool isPaused = controller.State == ControllerState.Paused;
                if (isPaused && !wasPaused)
                {
                    AnsiConsole.MarkupLine($"\n[{ColorWarning}]{ProbeStatusPaused}[/]");
                }
                wasPaused = isPaused;

                // Update display when progress changes
                if (grid.Progress != lastProgress)
                {
                    lastProgress = grid.Progress;
                    DrawProbeMatrix(grid);
                }

                Thread.Sleep(StatusPollIntervalMs);
            }

            // Final draw
            if (grid.NotProbed.Count == 0)
            {
                DrawProbeMatrix(grid);
            }
        }

        private static void OnPointCompleted(int index, Vector2 coords, double z)
        {
            // Autosave progress
            Persistence.SaveProbeProgress();
            Logger.Log($"Probe point {index + 1} complete: ({coords.X:F3}, {coords.Y:F3}) Z={z:F3}");
        }

        private static void OnPhaseChanged(ProbePhase phase)
        {
            Logger.Log($"Probe phase: {phase}");
        }

        private static void OnProgressChanged(ProgressInfo progress)
        {
            // Progress is shown via DrawProbeMatrix
        }

        private static void OnErrorOccurred(ControllerError error)
        {
            AnsiConsole.MarkupLine($"\n[{ColorError}]{Markup.Escape(error.Message)}[/]");
        }

        private static bool CreateProbeGrid(double margin, double gridSize)
        {
            var currentFile = AppState.CurrentFile!;

            try
            {
                var grid = AppState.SetupProbeGrid(
                    new Vector2(currentFile.Min.X, currentFile.Min.Y),
                    new Vector2(currentFile.Max.X, currentFile.Max.Y),
                    margin,
                    gridSize);

                AnsiConsole.MarkupLine($"[{ColorSuccess}]{string.Format(ProbeFormatGrid, grid.SizeX, grid.SizeY, grid.TotalPoints)}[/]");
                AnsiConsole.MarkupLine($"[{ColorDim}]{string.Format(ProbeFormatBounds, grid.Min.X, grid.Max.X, grid.Min.Y, grid.Max.Y)}[/]");
                return true;
            }
            catch (Exception ex)
            {
                MenuHelpers.ShowError(string.Format(ProbeFormatGridError, ex.Message));
                return false;
            }
        }

        // ANSI escape for RGB foreground color
        private static string AnsiRgb(int r, int g, int b) => $"\x1b[38;2;{r};{g};{b}m";

        /// <summary>
        /// Maps a normalized value (0-1) to a color gradient: blue -> cyan -> green -> yellow -> red
        /// </summary>
        private static (int R, int G, int B) HeightToColor(double t)
        {
            t = Math.Clamp(t, 0, 1);

            if (t < 0.25)
            {
                // Blue to Cyan (0,0,255) -> (0,255,255)
                double s = t / 0.25;
                return (0, (int)(255 * s), 255);
            }
            else if (t < 0.5)
            {
                // Cyan to Green (0,255,255) -> (0,255,0)
                double s = (t - 0.25) / 0.25;
                return (0, 255, (int)(255 * (1 - s)));
            }
            else if (t < 0.75)
            {
                // Green to Yellow (0,255,0) -> (255,255,0)
                double s = (t - 0.5) / 0.25;
                return ((int)(255 * s), 255, 0);
            }
            else
            {
                // Yellow to Red (255,255,0) -> (255,0,0)
                double s = (t - 0.75) / 0.25;
                return (255, (int)(255 * (1 - s)), 0);
            }
        }

        private static void DrawProbeMatrix(ProbeGrid probePoints)
        {
            var unprobed = new HashSet<(int, int)>();
            foreach (var p in probePoints.NotProbed)
            {
                unprobed.Add((p.Item1, p.Item2));
            }

            var (winWidth, winHeight) = GetSafeWindowSize();
            int maxWidth = Math.Min((winWidth - ProbeGridConsolePadding) / 2, ProbeGridMaxDisplayWidth);
            int maxHeight = Math.Min(winHeight - ProbeGridHeaderPadding, ProbeGridMaxDisplayHeight);

            int stepX = Math.Max(1, (probePoints.SizeX + maxWidth - 1) / maxWidth);
            int stepY = Math.Max(1, (probePoints.SizeY + maxHeight - 1) / maxHeight);

            int matrixWidth = ((probePoints.SizeX + stepX - 1) / stepX) * 2;
            int leftPadding = Math.Max(0, (winWidth - matrixWidth) / 2);
            string pad = new string(' ', leftPadding);

            // Get height range for color mapping
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

            // Show color legend when we have a range
            if (hasRange)
            {
                var (rLow, gLow, bLow) = HeightToColor(0.0);
                var (rMid, gMid, bMid) = HeightToColor(0.5);
                var (rHigh, gHigh, bHigh) = HeightToColor(1.0);
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
                    // Get average height of probed points in this cell
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
                        // Unprobed - dim dots
                        line.Append(AnsiDim).Append("··").Append(AnsiReset);
                    }
                    else
                    {
                        // Probed - color based on height
                        double avgHeight = heightSum / heightCount;
                        double t = hasRange ? (avgHeight - minZ) / rangeZ : 0.5;
                        var (r, g, b) = HeightToColor(t);
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

        /// <summary>
        /// Prompts user to save probe data to a file using the file browser.
        /// Can be called after probing completes or from the menu.
        /// </summary>
        private static void PromptSaveProbeData()
        {
            var probePoints = AppState.ProbePoints;
            var session = AppState.Session;
            var currentFile = AppState.CurrentFile;

            if (probePoints == null || probePoints.NotProbed.Count > 0)
            {
                MenuHelpers.ShowError(ProbeErrorNoComplete);
                return;
            }

            // Generate default filename based on G-code file or timestamp
            string defaultFilename;
            if (currentFile != null && !string.IsNullOrEmpty(currentFile.FileName))
            {
                defaultFilename = Path.GetFileNameWithoutExtension(currentFile.FileName) + ".pgrid";
            }
            else
            {
                defaultFilename = DateTime.Now.ToString(ProbeDateFormat) + ".pgrid";
            }

            // Use file browser to select save location
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

            // Confirm overwrite if file exists
            if (File.Exists(path))
            {
                if (!MenuHelpers.Confirm(string.Format(ProbeFormatOverwrite, Path.GetFileName(path)), true))
                {
                    AnsiConsole.MarkupLine($"[{ColorWarning}]{ProbeStatusNotSaved}[/]");
                    Thread.Sleep(ConfirmationDisplayMs);
                    return;
                }
            }

            // Move autosave to user's chosen location
            if (Persistence.SaveProbeToFile(path))
            {
                AnsiConsole.MarkupLine($"[{ColorSuccess}]{string.Format(ProbeFormatSaved, Markup.Escape(path))}[/]");

                // Update last probe browse directory
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

        /// <summary>
        /// Offers to start milling if conditions are met.
        /// </summary>
        /// <returns>True if milling was performed.</returns>
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
