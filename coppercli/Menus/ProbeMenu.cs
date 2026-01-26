// Extracted from Program.cs

using coppercli.Core.GCode;
using coppercli.Core.Util;
using coppercli.Helpers;
using Spectre.Console;
using static coppercli.CliConstants;
using static coppercli.Core.Util.GrblProtocol;

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
            ApplyToGCode,
            Back
        }

        public static void Show()
        {
            var machine = AppState.Machine;

            while (true)
            {
                Console.Clear();
                AnsiConsole.Write(new Rule("[bold blue]Probe[/]").RuleStyle("blue"));

                var probePoints = AppState.ProbePoints;
                var currentFile = AppState.CurrentFile;

                bool hasIncomplete = HasIncompleteProbeData();
                bool hasComplete = probePoints != null && probePoints.NotProbed.Count == 0;

                if (probePoints != null)
                {
                    AnsiConsole.WriteLine(probePoints.GetInfo());
                    if (!AppState.ProbePointsApplied && currentFile != null)
                    {
                        AnsiConsole.MarkupLine("[yellow]* Probe data not yet applied to G-Code[/]");
                    }
                    else if (AppState.ProbePointsApplied)
                    {
                        AnsiConsole.MarkupLine("[green]Probe data applied to G-Code[/]");
                    }
                }
                else if (hasIncomplete)
                {
                    AnsiConsole.MarkupLine("[yellow]Incomplete probe data found (autosaved)[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine("[dim]No probe data[/]");
                }

                if (currentFile == null)
                {
                    AnsiConsole.MarkupLine("[dim]No G-Code file loaded (required for probing)[/]");
                }

                if (!AppState.WorkZeroSet)
                {
                    AnsiConsole.MarkupLine("[dim]Work zero not set (required for probing)[/]");
                }

                AnsiConsole.WriteLine();

                bool canProbe = currentFile != null && AppState.WorkZeroSet && machine.Connected;

                var menu = BuildProbeMenu(hasIncomplete, hasComplete, canProbe);
                var choice = MenuHelpers.ShowMenu("Probe options:", menu);

                switch (choice.Option)
                {
                    case ProbeAction.ContinueProbing:
                        ContinueProbing();
                        break;
                    case ProbeAction.ClearProbeData:
                        ClearProbeData();
                        break;
                    case ProbeAction.ClearAndStartProbing:
                        ClearProbeData();
                        StartProbing();
                        break;
                    case ProbeAction.StartProbing:
                        StartProbing();
                        break;
                    case ProbeAction.LoadFromFile:
                        LoadProbeGrid();
                        break;
                    case ProbeAction.ApplyToGCode:
                        ApplyProbeGrid();
                        break;
                    case ProbeAction.Back:
                        if (AppState.Probing)
                        {
                            AppState.Probing = false;
                            machine.ProbeStop();
                        }
                        return;
                }
            }
        }

        private static MenuDef<ProbeAction> BuildProbeMenu(bool hasIncomplete, bool hasComplete, bool canProbe)
        {
            var menu = new MenuDef<ProbeAction>();

            if (hasIncomplete)
            {
                string continueLabel = canProbe
                    ? "Continue Probing"
                    : "Continue Probing [dim](connect & set work zero first)[/]";
                menu.Add(new MenuItem<ProbeAction>(continueLabel, 'c', ProbeAction.ContinueProbing));
                menu.Add(new MenuItem<ProbeAction>("Clear Probe Data", 'x', ProbeAction.ClearProbeData));

                string startLabel = canProbe
                    ? "Clear and Start Probing"
                    : "Clear and Start Probing [dim](load G-Code & set work zero first)[/]";
                menu.Add(new MenuItem<ProbeAction>(startLabel, 'p', ProbeAction.ClearAndStartProbing));
            }
            else
            {
                string startLabel = canProbe
                    ? "Start Probing"
                    : "Start Probing [dim](load G-Code & set work zero first)[/]";
                menu.Add(new MenuItem<ProbeAction>(startLabel, 'p', ProbeAction.StartProbing));
            }

            menu.Add(new MenuItem<ProbeAction>("Load from File", 'l', ProbeAction.LoadFromFile));

            // Re-check hasComplete after potential menu state changes
            var probePoints = AppState.ProbePoints;
            hasComplete = probePoints != null && probePoints.NotProbed.Count == 0;

            if (hasComplete && AppState.CurrentFile != null && !AppState.ProbePointsApplied)
            {
                menu.Add(new MenuItem<ProbeAction>("Apply to G-Code", 'a', ProbeAction.ApplyToGCode));
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
            AppState.ProbePoints = null;
            AppState.ProbePointsApplied = false;
            Persistence.ClearProbeAutoSave();
            AnsiConsole.MarkupLine("[yellow]Probe data cleared[/]");
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
                AppState.ProbePointsApplied = false;
                AnsiConsole.MarkupLine($"[green]Probe data loaded[/]");
                AnsiConsole.WriteLine(AppState.ProbePoints.GetInfo());
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
                Console.ReadKey();
            }
        }

        private static void ApplyProbeGrid()
        {
            var currentFile = AppState.CurrentFile;
            var probePoints = AppState.ProbePoints;
            var machine = AppState.Machine;

            if (currentFile == null)
            {
                MenuHelpers.ShowError("No G-code file loaded");
                return;
            }

            if (probePoints == null || probePoints.NotProbed.Count > 0)
            {
                MenuHelpers.ShowError("Probe data not complete");
                return;
            }

            try
            {
                AppState.CurrentFile = currentFile.ApplyProbeGrid(probePoints);
                machine.SetFile(AppState.CurrentFile.GetGCode());
                AppState.ProbePointsApplied = true;
                AnsiConsole.MarkupLine("[green]Probe data applied to G-Code![/]");
            }
            catch (Exception ex)
            {
                MenuHelpers.ShowError($"Error: {ex.Message}");
            }
        }

        private static void ContinueProbing()
        {
            if (!MenuHelpers.RequireConnection())
            {
                return;
            }

            if (!AppState.WorkZeroSet)
            {
                MenuHelpers.ShowError("Work zero not set. Use Move menu to zero all axes (0) first.");
                return;
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
                    return;
                }

                try
                {
                    AppState.ProbePoints = ProbeGrid.Load(session.ProbeAutoSavePath);
                    probePoints = AppState.ProbePoints;
                    AppState.ProbePointsApplied = false;
                }
                catch (Exception ex)
                {
                    MenuHelpers.ShowError($"Error loading probe data: {ex.Message}");
                    return;
                }
            }

            if (probePoints!.NotProbed.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]Probe data is already complete.[/]");
                Console.ReadKey();
                return;
            }

            AnsiConsole.MarkupLine($"[green]Resuming probe: {probePoints.Progress}/{probePoints.TotalPoints} points complete[/]");

            AppState.Probing = true;
            machine.ProbeStart();

            machine.SendLine(CmdAbsolute);
            machine.SendLine($"{CmdRapidMove} Z{settings.ProbeSafeHeight:F3}");

            AnsiConsole.MarkupLine("[green]Probing resumed. Press Escape to stop.[/]");

            ProbeNextPoint();
            WaitForProbingComplete();

            machine.ProbeStop();
            Console.WriteLine();

            if (AppState.ProbePoints != null && AppState.ProbePoints.NotProbed.Count == 0)
            {
                Persistence.ClearProbeAutoSave();
                ShowProbeResults();
                if (MenuHelpers.ConfirmOrQuit("Apply probe data to G-Code?", true) == true)
                {
                    ApplyProbeGrid();
                }
            }
        }

        private static void StartProbing()
        {
            if (!MenuHelpers.RequireConnection())
            {
                return;
            }

            var currentFile = AppState.CurrentFile;

            if (currentFile == null)
            {
                MenuHelpers.ShowError("No G-Code file loaded. Load a file first.");
                return;
            }

            if (!AppState.WorkZeroSet)
            {
                MenuHelpers.ShowError("Work zero not set. Use Move menu to zero all axes (0) first.");
                return;
            }

            var margin = MenuHelpers.AskDoubleOrQuit("Probe margin (mm)", DefaultProbeMargin);
            if (margin == null)
            {
                return;
            }

            var gridSize = MenuHelpers.AskDoubleOrQuit("Grid size (mm)", DefaultProbeGridSize);
            if (gridSize == null)
            {
                return;
            }

            if (!CreateProbeGrid(margin.Value, gridSize.Value))
            {
                return;
            }

            var machine = AppState.Machine;
            var settings = AppState.Settings;

            var traverseChoice = MenuHelpers.ConfirmOrQuit("Traverse outline first?", true);
            if (traverseChoice == null)
            {
                return;
            }
            if (traverseChoice == true)
            {
                if (!TraverseProbeOutline())
                {
                    return;
                }
            }

            AppState.Probing = true;
            machine.ProbeStart();

            machine.SendLine(CmdAbsolute);
            machine.SendLine($"{CmdRapidMove} Z{settings.ProbeSafeHeight:F3}");

            AnsiConsole.MarkupLine("[green]Probing started. Press Escape to stop.[/]");

            ProbeNextPoint();
            WaitForProbingComplete();

            machine.ProbeStop();
            Console.WriteLine();

            if (AppState.ProbePoints != null && AppState.ProbePoints.NotProbed.Count == 0)
            {
                Persistence.ClearProbeAutoSave();
                ShowProbeResults();
                if (MenuHelpers.ConfirmOrQuit("Apply probe data to G-Code?", true) == true)
                {
                    ApplyProbeGrid();
                }
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
                AppState.ProbePointsApplied = false;
                var hm = AppState.ProbePoints;
                AnsiConsole.MarkupLine($"[green]Probe grid: {hm.SizeX}x{hm.SizeY} = {hm.TotalPoints} points[/]");
                AnsiConsole.MarkupLine($"[dim]Bounds: X({minX:F2} to {maxX:F2}) Y({minY:F2} to {maxY:F2})[/]");
                return true;
            }
            catch (Exception ex)
            {
                MenuHelpers.ShowError($"Error creating probe grid: {ex.Message}");
                return false;
            }
        }

        private static bool TraverseProbeOutline()
        {
            var probePoints = AppState.ProbePoints;
            var machine = AppState.Machine;
            var settings = AppState.Settings;

            if (probePoints == null)
            {
                return false;
            }

            var traverseHeight = MenuHelpers.AskDoubleOrQuit("Traverse height (mm)", settings.OutlineTraverseHeight);
            if (traverseHeight == null)
            {
                return false;
            }

            var traverseFeed = MenuHelpers.AskDoubleOrQuit("Traverse feed (mm/min)", settings.OutlineTraverseFeed);
            if (traverseFeed == null)
            {
                return false;
            }

            AnsiConsole.MarkupLine($"[yellow]Traversing probe outline at Z={traverseHeight.Value:F1}mm, feed={traverseFeed.Value:F0}mm/min[/]");
            AnsiConsole.MarkupLine("[dim]Press Escape to cancel[/]");

            double minX = probePoints.Min.X;
            double minY = probePoints.Min.Y;
            double maxX = probePoints.Max.X;
            double maxY = probePoints.Max.Y;

            double currentZ = machine.WorkPosition.Z;
            double safeZ = Math.Max(currentZ, traverseHeight.Value);
            AnsiConsole.MarkupLine($"[dim]Current Z={currentZ:F2}, moving to Z={safeZ:F2}[/]");
            machine.SendLine(CmdAbsolute);
            machine.SendLine($"{CmdRapidMove} Z{safeZ:F3}");

            WaitForZHeight(safeZ);

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
                    AnsiConsole.MarkupLine("\n[yellow]Outline traverse cancelled - machine stopped[/]");
                    return false;
                }

                AnsiConsole.MarkupLine($"  Moving to {label} ({x:F1}, {y:F1})...");
                machine.SendLine($"{CmdLinearMove} X{x:F3} Y{y:F3} F{traverseFeed.Value:F0}");

                if (!WaitForMoveComplete(x, y))
                {
                    machine.FeedHold();
                    machine.SoftReset();
                    AnsiConsole.MarkupLine("\n[yellow]Outline traverse cancelled - machine stopped[/]");
                    return false;
                }
            }

            AnsiConsole.MarkupLine("[green]Outline traverse complete![/]");

            return MenuHelpers.ConfirmOrQuit("Continue with probing?", true) ?? false;
        }

        private static void WaitForZHeight(double targetZ)
        {
            var startTime = DateTime.Now;

            while ((DateTime.Now - startTime).TotalMilliseconds < ZHeightWaitTimeoutMs)
            {
                double dz = Math.Abs(AppState.Machine.WorkPosition.Z - targetZ);
                if (dz < PositionToleranceMm)
                {
                    return;
                }
                Thread.Sleep(StatusPollIntervalMs);
            }
        }

        private static bool WaitForMoveComplete(double targetX, double targetY)
        {
            var startTime = DateTime.Now;

            while ((DateTime.Now - startTime).TotalMilliseconds < MoveCompleteTimeoutMs)
            {
                if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
                {
                    return false;
                }

                var pos = AppState.Machine.WorkPosition;
                double dx = Math.Abs(pos.X - targetX);
                double dy = Math.Abs(pos.Y - targetY);

                if (dx < PositionToleranceMm && dy < PositionToleranceMm)
                {
                    return true;
                }

                Thread.Sleep(StatusPollIntervalMs);
            }

            return true;
        }

        private static void WaitForProbingComplete()
        {
            var probePoints = AppState.ProbePoints;
            int lastProgress = -1;

            while (AppState.Probing && probePoints != null && probePoints.NotProbed.Count > 0)
            {
                if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
                {
                    AppState.Probing = false;
                    AppState.Machine.ProbeStop();
                    AnsiConsole.MarkupLine("\n[yellow]Probing stopped by user[/]");
                    break;
                }

                if (probePoints.Progress != lastProgress)
                {
                    lastProgress = probePoints.Progress;
                    DrawProbeMatrix();
                }

                Thread.Sleep(StatusPollIntervalMs);
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

            int maxWidth = Math.Min((Console.WindowWidth - 5) / 2, 40);
            int maxHeight = Math.Min(Console.WindowHeight - 10, 30);

            int stepX = Math.Max(1, (probePoints.SizeX + maxWidth - 1) / maxWidth);
            int stepY = Math.Max(1, (probePoints.SizeY + maxHeight - 1) / maxHeight);

            int matrixWidth = ((probePoints.SizeX + stepX - 1) / stepX) * 2;
            int leftPadding = Math.Max(0, (Console.WindowWidth - matrixWidth) / 2);
            string pad = new string(' ', leftPadding);

            Console.Clear();
            string zRange = probePoints.HasValidHeights
                ? $"Z: {probePoints.MinHeight:F3} to {probePoints.MaxHeight:F3}"
                : "Z: --";
            string header = $"Probing: {probePoints.Progress}/{probePoints.TotalPoints} | {zRange}";
            int headerPad = Math.Max(0, (Console.WindowWidth - header.Length) / 2);
            Console.WriteLine();
            AnsiConsole.MarkupLine(new string(' ', headerPad) + $"[bold]{header}[/]");
            AnsiConsole.MarkupLine(new string(' ', headerPad) + "[dim]Press Escape to stop[/]");
            Console.WriteLine();

            for (int y = probePoints.SizeY - 1; y >= 0; y -= stepY)
            {
                var line = new System.Text.StringBuilder();
                line.Append(pad);
                for (int x = 0; x < probePoints.SizeX; x += stepX)
                {
                    bool cellProbed = true;
                    for (int dy = 0; dy < stepY && y - dy >= 0; dy++)
                    {
                        for (int dx = 0; dx < stepX && x + dx < probePoints.SizeX; dx++)
                        {
                            if (unprobed.Contains((x + dx, y - dy)))
                            {
                                cellProbed = false;
                                break;
                            }
                        }
                        if (!cellProbed)
                        {
                            break;
                        }
                    }
                    line.Append(cellProbed ? "██" : "··");
                }
                Console.WriteLine(line.ToString());
            }
        }

        private static void ShowProbeResults()
        {
            var probePoints = AppState.ProbePoints;
            var session = AppState.Session;
            var currentFile = AppState.CurrentFile;

            if (probePoints == null)
            {
                return;
            }

            AnsiConsole.MarkupLine("[green]Probing complete![/]");
            AnsiConsole.MarkupLine($"  Points: {probePoints.TotalPoints}");
            if (probePoints.HasValidHeights)
            {
                AnsiConsole.MarkupLine($"  Z range: {probePoints.MinHeight:F3} to {probePoints.MaxHeight:F3} mm");
                AnsiConsole.MarkupLine($"  Variance: {probePoints.MaxHeight - probePoints.MinHeight:F3} mm");
            }
            AnsiConsole.WriteLine();

            string defaultFilename;
            if (currentFile != null && !string.IsNullOrEmpty(currentFile.FileName))
            {
                defaultFilename = Path.GetFileNameWithoutExtension(currentFile.FileName) + ".pgrid";
            }
            else
            {
                defaultFilename = DateTime.Now.ToString("yyyy-MM-dd-HH-mm") + ".pgrid";
            }

            string defaultPath = !string.IsNullOrEmpty(session.LastBrowseDirectory)
                ? Path.Combine(session.LastBrowseDirectory, defaultFilename)
                : defaultFilename;

            var path = AnsiConsole.Ask("Save probe data:", defaultPath);

            if (path.StartsWith("~"))
            {
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Substring(2));
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                AnsiConsole.MarkupLine("[yellow]Probe data not saved[/]");
                return;
            }

            if (File.Exists(path))
            {
                if (!MenuHelpers.Confirm($"Overwrite {Path.GetFileName(path)}?", true))
                {
                    AnsiConsole.MarkupLine("[yellow]Probe data not saved[/]");
                    return;
                }
            }

            try
            {
                probePoints.Save(path);
                AnsiConsole.MarkupLine($"[green]Probe data saved to {Markup.Escape(path)}[/]");

                session.LastSavedProbeFile = Path.GetFullPath(path);
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    session.LastBrowseDirectory = dir;
                }
                Persistence.SaveSession();
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error saving: {Markup.Escape(ex.Message)}[/]");
            }

            // Offer to proceed to Milling after successful probe
            if (currentFile != null && currentFile.ContainsMotion)
            {
                if (MenuHelpers.ConfirmOrQuit("Proceed to Milling?", true) == true)
                {
                    MillMenu.Show();
                }
            }
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

            machine.SendLine($"{CmdRapidMove} X{coords.X:F3} Y{coords.Y:F3}");
            machine.SendLine($"{CmdProbeToward} Z-{settings.ProbeMaxDepth:F3} F{settings.ProbeFeed:F1}");

            machine.SendLine(CmdRelative);
            machine.SendLine($"{CmdRapidMove} Z{settings.ProbeMinimumHeight:F3}");
            machine.SendLine(CmdAbsolute);
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
                    AppState.Machine.SendLine($"{CmdRapidMove} Z{Math.Max(settings.ProbeSafeHeight, pos.Z):F3}");
                    AppState.Probing = false;
                }
            }
            else
            {
                if (settings.AbortOnProbeFail)
                {
                    AppState.Probing = false;
                    AnsiConsole.MarkupLine("\n[red]Probe failed! Aborting.[/]");
                    return;
                }

                if (probePoints.NotProbed.Count > 0)
                {
                    probePoints.NotProbed.RemoveAt(0);
                }

                AppState.Machine.SendLine(CmdRelative);
                AppState.Machine.SendLine($"{CmdRapidMove} Z{settings.ProbeSafeHeight:F3}");
                AppState.Machine.SendLine(CmdAbsolute);

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
