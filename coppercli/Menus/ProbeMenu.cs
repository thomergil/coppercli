// Extracted from Program.cs

using coppercli.Core.GCode;
using coppercli.Core.Util;
using coppercli.Helpers;
using Spectre.Console;
using static coppercli.CliConstants;
using static coppercli.Core.Util.GrblProtocol;
using static coppercli.Helpers.DisplayHelpers;

namespace coppercli.Menus
{
    /// <summary>
    /// Probe menu for grid probing workflow.
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
            SaveToFile,
            ApplyToGCode,
            Back
        }

        public static void Show()
        {
            var machine = AppState.Machine;

            // Defense in depth: ensure auto-clear is disabled during probing
            machine.EnableAutoStateClear = false;

            while (true)
            {
                Console.Clear();
                AnsiConsole.Write(new Rule($"[{ColorBold} {ColorPrompt}]Probe[/]").RuleStyle(ColorPrompt));

                var probePoints = AppState.ProbePoints;
                var currentFile = AppState.CurrentFile;

                bool hasIncomplete = HasIncompleteProbeData();
                bool hasComplete = probePoints != null && probePoints.NotProbed.Count == 0;

                if (probePoints != null)
                {
                    AnsiConsole.WriteLine(probePoints.GetInfo());
                    if (!AppState.AreProbePointsApplied && currentFile != null)
                    {
                        AnsiConsole.MarkupLine($"[{ColorWarning}]* Probe data not yet applied to G-Code[/]");
                    }
                    else if (AppState.AreProbePointsApplied)
                    {
                        AnsiConsole.MarkupLine($"[{ColorSuccess}]Probe data applied to G-Code[/]");
                    }
                }
                else if (hasIncomplete)
                {
                    AnsiConsole.MarkupLine($"[{ColorWarning}]Incomplete probe data found (autosaved)[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[{ColorDim}]No probe data[/]");
                }

                if (currentFile == null)
                {
                    AnsiConsole.MarkupLine($"[{ColorDim}]No G-Code file loaded (required for probing)[/]");
                }

                if (!AppState.IsWorkZeroSet)
                {
                    AnsiConsole.MarkupLine($"[{ColorDim}]Work zero not set (required for probing)[/]");
                }

                AnsiConsole.WriteLine();

                bool canProbe = currentFile != null && AppState.IsWorkZeroSet && machine.Connected;

                var menu = BuildProbeMenu(hasIncomplete, hasComplete, canProbe);
                var choice = MenuHelpers.ShowMenu("Probe options:", menu);

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
                        LoadProbeGrid();
                        break;
                    case ProbeAction.SaveToFile:
                        PromptSaveProbeData();
                        break;
                    case ProbeAction.ApplyToGCode:
                        ApplyProbeGrid();
                        break;
                    case ProbeAction.Back:
                        if (AppState.Probing)
                        {
                            AppState.StopProbing();
                        }
                        return;
                }
            }
        }

        private static string? GetProbeDisabledReason()
        {
            if (!AppState.Machine.Connected)
            {
                return "connect first";
            }
            if (AppState.CurrentFile == null)
            {
                return "load G-Code first";
            }
            if (!AppState.IsWorkZeroSet)
            {
                return "set work zero first";
            }
            return null;
        }

        private static MenuDef<ProbeAction> BuildProbeMenu(bool hasIncomplete, bool hasComplete, bool canProbe)
        {
            var menu = new MenuDef<ProbeAction>();

            if (hasIncomplete)
            {
                menu.Add(new MenuItem<ProbeAction>("Continue Probing", 'c', ProbeAction.ContinueProbing,
                    EnabledWhen: () => canProbe, DisabledReason: GetProbeDisabledReason));
                menu.Add(new MenuItem<ProbeAction>("Clear Probe Data", 'x', ProbeAction.ClearProbeData));
                menu.Add(new MenuItem<ProbeAction>("Clear and Start Probing", 'p', ProbeAction.ClearAndStartProbing,
                    EnabledWhen: () => canProbe, DisabledReason: GetProbeDisabledReason));
            }
            else
            {
                menu.Add(new MenuItem<ProbeAction>("Start Probing", 'p', ProbeAction.StartProbing,
                    EnabledWhen: () => canProbe, DisabledReason: GetProbeDisabledReason));
            }

            menu.Add(new MenuItem<ProbeAction>("Load from File", 'l', ProbeAction.LoadFromFile));

            // Re-check hasComplete after potential menu state changes
            var probePoints = AppState.ProbePoints;
            hasComplete = probePoints != null && probePoints.NotProbed.Count == 0;

            if (hasComplete)
            {
                menu.Add(new MenuItem<ProbeAction>("Save to File", 's', ProbeAction.SaveToFile));

                if (AppState.CurrentFile != null && !AppState.AreProbePointsApplied)
                {
                    menu.Add(new MenuItem<ProbeAction>("Apply to G-Code", 'a', ProbeAction.ApplyToGCode));
                }
            }

            menu.Add(new MenuItem<ProbeAction>("Back", 'q', ProbeAction.Back));

            return menu;
        }

        private static bool HasIncompleteProbeData()
        {
            var session = AppState.Session;
            var probePoints = AppState.ProbePoints;
            return (probePoints != null && probePoints.NotProbed.Count > 0) ||
                   (!string.IsNullOrEmpty(session.ProbeAutoSavePath) && File.Exists(session.ProbeAutoSavePath));
        }

        private static void ClearProbeData()
        {
            AppState.DiscardProbeData();
            Persistence.ClearProbeAutoSave();
            AnsiConsole.MarkupLine($"[{ColorWarning}]Probe data cleared[/]");
        }

        private static void LoadProbeGrid()
        {
            var path = FileMenu.BrowseForProbeGridFile();
            if (path == null)
            {
                return;
            }

            try
            {
                AppState.ProbePoints = ProbeGrid.Load(path);
                AppState.ResetProbeApplicationState();
                AnsiConsole.MarkupLine($"[{ColorSuccess}]Probe data loaded[/]");
                AnsiConsole.WriteLine(AppState.ProbePoints.GetInfo());
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[{ColorError}]Error: {Markup.Escape(ex.Message)}[/]");
                MenuHelpers.WaitEnter();
            }
        }

        private static void ApplyProbeGrid()
        {
            if (AppState.CurrentFile == null)
            {
                MenuHelpers.ShowError("No G-code file loaded");
                return;
            }

            if (AppState.ProbePoints == null || AppState.ProbePoints.NotProbed.Count > 0)
            {
                MenuHelpers.ShowError("Probe data not complete");
                return;
            }

            try
            {
                AppState.ApplyProbeData();
                AnsiConsole.MarkupLine($"[{ColorSuccess}]Probe data applied to G-Code![/]");
            }
            catch (Exception ex)
            {
                MenuHelpers.ShowError($"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Continues an interrupted probing session.
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
                MenuHelpers.ShowError("Work zero not set. Use Move menu to zero all axes (0) first.");
                return false;
            }

            var session = AppState.Session;
            var probePoints = AppState.ProbePoints;
            var machine = AppState.Machine;
            var settings = AppState.Settings;

            if (probePoints == null || probePoints.NotProbed.Count == 0)
            {
                if (string.IsNullOrEmpty(session.ProbeAutoSavePath) || !File.Exists(session.ProbeAutoSavePath))
                {
                    MenuHelpers.ShowError("No incomplete probe data found.");
                    return false;
                }

                try
                {
                    AppState.ProbePoints = ProbeGrid.Load(session.ProbeAutoSavePath);
                    probePoints = AppState.ProbePoints;
                    AppState.ResetProbeApplicationState();
                }
                catch (Exception ex)
                {
                    MenuHelpers.ShowError($"Error loading probe data: {ex.Message}");
                    return false;
                }
            }

            if (probePoints!.NotProbed.Count == 0)
            {
                AnsiConsole.MarkupLine($"[{ColorWarning}]Probe data is already complete.[/]");
                MenuHelpers.WaitEnter();
                return false;
            }

            AnsiConsole.MarkupLine($"[{ColorSuccess}]Resuming probe: {probePoints.Progress}/{probePoints.TotalPoints} points complete[/]");

            // Ensure machine is idle before starting
            StatusHelpers.WaitForIdle(machine, IdleWaitTimeoutMs);

            AppState.StartProbing();

            machine.SendLine(CmdAbsolute);
            machine.SendLine($"{CmdRapidMove} Z{settings.ProbeSafeHeight:F3}");

            AnsiConsole.MarkupLine($"[{ColorSuccess}]Probing resumed. Press Escape to stop.[/]");

            ProbeNextPoint();
            WaitForProbingComplete();

            AppState.StopProbing();
            Console.WriteLine();

            if (AppState.ProbePoints != null && AppState.ProbePoints.NotProbed.Count == 0)
            {
                Persistence.ClearProbeAutoSave();
                ShowProbeResults();
                if (MenuHelpers.ConfirmOrQuit("Apply probe data to G-Code?", true) == true)
                {
                    ApplyProbeGrid();
                    return OfferToMill();
                }
            }
            return false;
        }

        /// <summary>
        /// Starts a new probing session.
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
                MenuHelpers.ShowError("No G-Code file loaded. Load a file first.");
                return false;
            }

            if (!AppState.IsWorkZeroSet)
            {
                MenuHelpers.ShowError("Work zero not set. Use Move menu to zero all axes (0) first.");
                return false;
            }

            var margin = MenuHelpers.AskDouble("Probe margin (mm)", DefaultProbeMargin);
            if (margin == null)
            {
                return false;
            }

            var gridSize = MenuHelpers.AskDouble("Grid size (mm)", DefaultProbeGridSize);
            if (gridSize == null)
            {
                return false;
            }

            if (!CreateProbeGrid(margin.Value, gridSize.Value))
            {
                return false;
            }

            var machine = AppState.Machine;
            var settings = AppState.Settings;

            var traceChoice = MenuHelpers.ConfirmOrQuit("Trace outline first?", true);
            if (traceChoice == null)
            {
                return false;
            }
            if (traceChoice == true)
            {
                if (!TraceProbeOutline())
                {
                    return false;
                }
            }

            // Ensure machine is idle before starting
            StatusHelpers.WaitForIdle(machine, IdleWaitTimeoutMs);

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

            // Start sleep prevention (no-op if unavailable)
            SleepPrevention.Start();

            try
            {
                AppState.StartProbing();

                machine.SendLine(CmdAbsolute);
                machine.SendLine($"{CmdRapidMove} Z{settings.ProbeSafeHeight:F3}");

                AnsiConsole.MarkupLine($"[{ColorSuccess}]Probing started. Press Escape to stop.[/]");

                ProbeNextPoint();
                WaitForProbingComplete();

                AppState.StopProbing();
                Console.WriteLine();

                if (AppState.ProbePoints != null && AppState.ProbePoints.NotProbed.Count == 0)
                {
                    Persistence.ClearProbeAutoSave();
                    ShowProbeResults();
                    if (MenuHelpers.ConfirmOrQuit("Apply probe data to G-Code?", true) == true)
                    {
                        ApplyProbeGrid();
                        return OfferToMill();
                    }
                }
                return false;
            }
            finally
            {
                SleepPrevention.Stop();
            }
        }

        private static bool CreateProbeGrid(double margin, double gridSize)
        {
            var currentFile = AppState.CurrentFile!;
            var minX = currentFile.Min.X - margin;
            var minY = currentFile.Min.Y - margin;
            var maxX = currentFile.Max.X + margin;
            var maxY = currentFile.Max.Y + margin;

            try
            {
                AppState.ProbePoints = new ProbeGrid(gridSize, new Vector2(minX, minY), new Vector2(maxX, maxY));
                AppState.ResetProbeApplicationState();
                var hm = AppState.ProbePoints;
                AnsiConsole.MarkupLine($"[{ColorSuccess}]Probe grid: {hm.SizeX}x{hm.SizeY} = {hm.TotalPoints} points[/]");
                AnsiConsole.MarkupLine($"[{ColorDim}]Bounds: X({minX:F2} to {maxX:F2}) Y({minY:F2} to {maxY:F2})[/]");
                return true;
            }
            catch (Exception ex)
            {
                MenuHelpers.ShowError($"Error creating probe grid: {ex.Message}");
                return false;
            }
        }

        private static bool TraceProbeOutline()
        {
            var probePoints = AppState.ProbePoints;
            var machine = AppState.Machine;
            var settings = AppState.Settings;

            if (probePoints == null)
            {
                return false;
            }

            var traceHeight = MenuHelpers.AskDouble("Trace height (mm)", settings.OutlineTraceHeight);
            if (traceHeight == null)
            {
                return false;
            }

            var traceFeed = MenuHelpers.AskDouble("Trace feed (mm/min)", settings.OutlineTraceFeed);
            if (traceFeed == null)
            {
                return false;
            }

            AnsiConsole.MarkupLine($"[{ColorWarning}]Tracing probe outline at Z={traceHeight.Value:F1}mm, feed={traceFeed.Value:F0}mm/min[/]");
            AnsiConsole.MarkupLine($"[{ColorDim}]Press Escape to cancel[/]");

            double minX = probePoints.Min.X;
            double minY = probePoints.Min.Y;
            double maxX = probePoints.Max.X;
            double maxY = probePoints.Max.Y;

            double currentZ = machine.WorkPosition.Z;
            double safeZ = Math.Max(currentZ, traceHeight.Value);
            AnsiConsole.MarkupLine($"[{ColorDim}]Current Z={currentZ:F2}, moving to Z={safeZ:F2}[/]");
            MachineCommands.RapidMoveAndWaitZ(machine, safeZ);

            var corners = new[]
            {
                (minX, minY, "bottom-left"),
                (maxX, minY, "bottom-right"),
                (maxX, maxY, "top-right"),
                (minX, maxY, "top-left"),
                (minX, minY, "back to start")
            };

            foreach (var (x, y, label) in corners)
            {
                if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
                {
                    machine.FeedHold();
                    machine.SoftReset();
                    AnsiConsole.MarkupLine($"\n[{ColorWarning}]Outline trace cancelled - machine stopped[/]");
                    return false;
                }

                AnsiConsole.MarkupLine($"  Moving to {label} ({x:F1}, {y:F1})...");
                machine.SendLine(CmdAbsolute);
                machine.SendLine($"{CmdLinearMove} X{x:F3} Y{y:F3} F{traceFeed.Value:F0}");

                if (!StatusHelpers.WaitForMoveComplete(machine, x, y, CheckEscape))
                {
                    machine.FeedHold();
                    machine.SoftReset();
                    AnsiConsole.MarkupLine($"\n[{ColorWarning}]Outline trace cancelled - machine stopped[/]");
                    return false;
                }
            }

            AnsiConsole.MarkupLine($"[{ColorSuccess}]Outline trace complete![/]");

            return MenuHelpers.ConfirmOrQuit("Continue with probing?", true) ?? false;
        }

        private static bool CheckEscape() =>
            Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape;

        private static void WaitForProbingComplete()
        {
            var probePoints = AppState.ProbePoints;
            int lastProgress = -1;

            while (AppState.Probing && probePoints != null && probePoints.NotProbed.Count > 0)
            {
                if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
                {
                    AppState.StopProbing();
                    AnsiConsole.MarkupLine($"\n[{ColorWarning}]Probing stopped by user[/]");
                    break;
                }

                if (probePoints.Progress != lastProgress)
                {
                    lastProgress = probePoints.Progress;
                    DrawProbeMatrix();
                }

                Thread.Sleep(StatusPollIntervalMs);
            }

            // Wait for machine to finish (e.g., the final Z raise) and draw completed state
            if (probePoints != null && probePoints.NotProbed.Count == 0)
            {
                DrawProbeMatrix();
                StatusHelpers.WaitForIdle(AppState.Machine, IdleWaitTimeoutMs);
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

        private static void DrawProbeMatrix()
        {
            var probePoints = AppState.ProbePoints;
            if (probePoints == null)
            {
                return;
            }

            var unprobed = new HashSet<(int, int)>();
            foreach (var p in probePoints.NotProbed)
            {
                unprobed.Add((p.Item1, p.Item2));
            }

            int maxWidth = Math.Min((Console.WindowWidth - ProbeGridConsolePadding) / 2, ProbeGridMaxDisplayWidth);
            int maxHeight = Math.Min(Console.WindowHeight - ProbeGridHeaderPadding, ProbeGridMaxDisplayHeight);

            int stepX = Math.Max(1, (probePoints.SizeX + maxWidth - 1) / maxWidth);
            int stepY = Math.Max(1, (probePoints.SizeY + maxHeight - 1) / maxHeight);

            int matrixWidth = ((probePoints.SizeX + stepX - 1) / stepX) * 2;
            int leftPadding = Math.Max(0, (Console.WindowWidth - matrixWidth) / 2);
            string pad = new string(' ', leftPadding);

            // Get height range for color mapping
            double minZ = probePoints.MinHeight;
            double maxZ = probePoints.MaxHeight;
            double rangeZ = maxZ - minZ;
            bool hasRange = probePoints.HasValidHeights && rangeZ > 0.0001;

            Console.Clear();
            string zRange = probePoints.HasValidHeights
                ? $"Z: {probePoints.MinHeight:F3} to {probePoints.MaxHeight:F3}"
                : "Z: --";
            string header = $"Probing: {probePoints.Progress}/{probePoints.TotalPoints} | {zRange}";
            int headerPad = Math.Max(0, (Console.WindowWidth - header.Length) / 2);
            Console.WriteLine();
            AnsiConsole.MarkupLine(new string(' ', headerPad) + $"[{ColorBold}]{header}[/]");
            AnsiConsole.MarkupLine(new string(' ', headerPad) + $"[{ColorDim}]Press Escape to stop[/]");

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
                // Approximate legend display length (3 colored blocks * 2 chars + 3 values ~7 chars each + spacing)
                int legendDisplayLen = 2 + 7 + 2 + 2 + 7 + 2 + 2 + 7;
                int legendPad = Math.Max(0, (Console.WindowWidth - legendDisplayLen) / 2);
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

            AnsiConsole.MarkupLine($"[{ColorSuccess}]Probing complete![/]");
            AnsiConsole.MarkupLine($"  Points: {probePoints.TotalPoints}");
            if (probePoints.HasValidHeights)
            {
                AnsiConsole.MarkupLine($"  Z range: {probePoints.MinHeight:F3} to {probePoints.MaxHeight:F3} mm");
                AnsiConsole.MarkupLine($"  Variance: {probePoints.MaxHeight - probePoints.MinHeight:F3} mm");
            }
            AnsiConsole.WriteLine();

            PromptSaveProbeData();
        }

        /// <summary>
        /// Prompts user to save probe data to a file.
        /// Can be called after probing completes or from the menu.
        /// </summary>
        private static void PromptSaveProbeData()
        {
            var probePoints = AppState.ProbePoints;
            var session = AppState.Session;
            var currentFile = AppState.CurrentFile;

            if (probePoints == null || probePoints.NotProbed.Count > 0)
            {
                MenuHelpers.ShowError("No complete probe data to save.");
                return;
            }

            string defaultFilename;
            if (currentFile != null && !string.IsNullOrEmpty(currentFile.FileName))
            {
                defaultFilename = Path.GetFileNameWithoutExtension(currentFile.FileName) + ".pgrid";
            }
            else
            {
                defaultFilename = DateTime.Now.ToString(ProbeDateFormat) + ".pgrid";
            }

            string defaultPath = !string.IsNullOrEmpty(session.LastBrowseDirectory)
                ? Path.Combine(session.LastBrowseDirectory, defaultFilename)
                : defaultFilename;

            while (true)
            {
                var path = MenuHelpers.AskString("Save probe data:", defaultPath);

                // Escape pressed - cancel save
                if (path == null)
                {
                    AnsiConsole.MarkupLine($"[{ColorWarning}]Probe data not saved[/]");
                    return;
                }

                if (path.StartsWith("~"))
                {
                    path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Substring(2));
                }

                if (File.Exists(path))
                {
                    if (!MenuHelpers.Confirm($"Overwrite {Path.GetFileName(path)}?", true))
                    {
                        // Loop back to prompt for a different filename
                        defaultPath = path;
                        continue;
                    }
                }

                try
                {
                    probePoints.Save(path);
                    AnsiConsole.MarkupLine($"[{ColorSuccess}]Probe data saved to {Markup.Escape(path)}[/]");

                    session.LastSavedProbeFile = Path.GetFullPath(path);
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        session.LastBrowseDirectory = dir;
                    }
                    Persistence.SaveSession();
                    return;
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[{ColorError}]Error saving: {Markup.Escape(ex.Message)}[/]");
                    return;
                }
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
                if (MenuHelpers.ConfirmOrQuit("Proceed to Milling?", true) == true)
                {
                    MillMenu.Show();
                    return true;
                }
            }
            return false;
        }

        private static void ProbeNextPoint()
        {
            var probePoints = AppState.ProbePoints;
            var machine = AppState.Machine;
            var settings = AppState.Settings;

            if (!AppState.Probing || probePoints == null || probePoints.NotProbed.Count == 0)
            {
                return;
            }

            SortProbePointsByDistance();

            var coords = probePoints.GetCoordinates(probePoints.NotProbed[0]);

            machine.SendLine(CmdAbsolute);
            machine.SendLine($"{CmdRapidMove} X{coords.X:F3} Y{coords.Y:F3}");
            machine.SendLine($"{CmdProbeToward} Z-{settings.ProbeMaxDepth:F3} F{settings.ProbeFeed:F1}");

            MachineCommands.RaiseZRelative(machine, settings.ProbeMinimumHeight);
        }

        private static void SortProbePointsByDistance()
        {
            var probePoints = AppState.ProbePoints;
            var settings = AppState.Settings;

            if (probePoints == null)
            {
                return;
            }

            var currentPos = AppState.Machine.WorkPosition.GetXY();
            probePoints.NotProbed.Sort((a, b) =>
            {
                var va = probePoints.GetCoordinates(a) - currentPos;
                var vb = probePoints.GetCoordinates(b) - currentPos;
                va.X *= settings.ProbeXAxisWeight;
                vb.X *= settings.ProbeXAxisWeight;
                return va.Magnitude.CompareTo(vb.Magnitude);
            });
        }

        /// <summary>
        /// Called by event handler when GRBL reports probe result.
        /// </summary>
        public static void OnProbeFinished(Vector3 pos, bool success)
        {
            if (AppState.SingleProbing)
            {
                AppState.SingleProbing = false;
                AppState.Machine.ProbeStop();
                AppState.SingleProbeCallback?.Invoke(pos, success);
                AppState.SingleProbeCallback = null;
                return;
            }

            var probePoints = AppState.ProbePoints;
            var settings = AppState.Settings;

            if (!AppState.Probing || probePoints == null || probePoints.NotProbed.Count == 0)
            {
                return;
            }

            if (success)
            {
                var point = probePoints.NotProbed[0];
                probePoints.AddPoint(point.Item1, point.Item2, pos.Z);
                probePoints.NotProbed.RemoveAt(0);
                Persistence.SaveProbeProgress();

                if (probePoints.NotProbed.Count > 0)
                {
                    ProbeNextPoint();
                }
                else
                {
                    AppState.Machine.SendLine(CmdAbsolute);
                    AppState.Machine.SendLine($"{CmdRapidMove} Z{Math.Max(settings.ProbeSafeHeight, pos.Z):F3}");
                    AppState.Probing = false;
                }
            }
            else
            {
                if (settings.AbortOnProbeFail)
                {
                    AppState.Probing = false;
                    AnsiConsole.MarkupLine($"\n[{ColorError}]Probe failed! Aborting.[/]");
                    return;
                }

                if (probePoints.NotProbed.Count > 0)
                {
                    probePoints.NotProbed.RemoveAt(0);
                }

                MachineCommands.RaiseZRelative(AppState.Machine, settings.ProbeSafeHeight);

                if (probePoints.NotProbed.Count > 0)
                {
                    ProbeNextPoint();
                }
                else
                {
                    AppState.Probing = false;
                }
            }
        }
    }
}
