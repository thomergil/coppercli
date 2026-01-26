// Extracted from Program.cs

using coppercli.Core.GCode;
using coppercli.Helpers;
using Spectre.Console;
using static coppercli.CliConstants;

namespace coppercli.Menus
{
    /// <summary>
    /// File menu for loading and browsing G-code and probe files.
    /// </summary>
    internal static class FileMenu
    {
        public static void LoadGCodeFile()
        {
            var path = BrowseForFile(GCodeExtensions);
            if (path != null)
            {
                LoadGCodeFromPath(path);
            }
        }

        public static string? BrowseForProbeGridFile()
        {
            return BrowseForFile(ProbeGridExtensions);
        }

        /// <summary>
        /// Generic file browser. Returns selected file path or null if cancelled.
        /// </summary>
        public static string? BrowseForFile(string[] extensions, string? defaultFileName = null)
        {
            var session = AppState.Session;

            // Start at last browse directory if it exists, otherwise current directory
            string currentDir = !string.IsNullOrEmpty(session.LastBrowseDirectory) && Directory.Exists(session.LastBrowseDirectory)
                ? session.LastBrowseDirectory
                : Environment.CurrentDirectory;

            while (true)
            {
                var items = new List<(string Display, string FullPath, bool IsDir)>();

                // Add parent directory option first
                var parent = Directory.GetParent(currentDir);
                if (parent != null)
                {
                    items.Add(("..", parent.FullName, true));
                }

                // Add subdirectories
                try
                {
                    foreach (var dir in Directory.GetDirectories(currentDir).OrderBy(d => Path.GetFileName(d)))
                    {
                        var name = Path.GetFileName(dir);
                        if (!name.StartsWith("."))
                        {
                            items.Add((name + "/", dir, true));
                        }
                    }
                }
                catch
                {
                    // Skip inaccessible directories
                }

                // Add matching files
                try
                {
                    foreach (var file in Directory.GetFiles(currentDir).OrderBy(f => Path.GetFileName(f)))
                    {
                        var ext = Path.GetExtension(file).ToLower();
                        if (extensions.Contains(ext))
                        {
                            items.Add((Path.GetFileName(file), file, false));
                        }
                    }
                }
                catch
                {
                    // Skip inaccessible files
                }

                // Build menu options
                var menuOptions = new List<string>();
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    menuOptions.Add($"{InputHelpers.GetMenuKey(i)}. {Markup.Escape(item.Display)}");
                }
                menuOptions.Add($"{InputHelpers.GetMenuKey(items.Count)}. Cancel");

                // Show current directory
                Console.Clear();
                AnsiConsole.Write(new Rule("[bold blue]Select File[/]").RuleStyle("blue"));
                AnsiConsole.MarkupLine($"[dim]{currentDir}[/]");
                AnsiConsole.WriteLine();

                int choice = MenuHelpers.ShowMenu("Select file or directory:", menuOptions.ToArray());

                if (choice == items.Count)
                {
                    return null; // Cancel
                }

                if (choice >= 0 && choice < items.Count)
                {
                    var selected = items[choice];
                    if (selected.IsDir)
                    {
                        currentDir = selected.FullPath;
                    }
                    else
                    {
                        // Save browse directory for next time
                        session.LastBrowseDirectory = currentDir;
                        Persistence.SaveSession();
                        return selected.FullPath;
                    }
                }
            }
        }

        public static void LoadGCodeFromPath(string path)
        {
            var session = AppState.Session;
            var machine = AppState.Machine;

            // Expand ~ for home directory
            if (path.StartsWith("~"))
            {
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Substring(2));
            }

            if (!File.Exists(path))
            {
                AnsiConsole.MarkupLine($"[red]File not found: {Markup.Escape(path)}[/]");
                Console.ReadKey();
                return;
            }

            try
            {
                var currentFile = GCodeFile.Load(path);
                AppState.CurrentFile = currentFile;

                if (currentFile.Warnings.Count > 0)
                {
                    AnsiConsole.MarkupLine($"[yellow]Warnings ({currentFile.Warnings.Count}):[/]");
                    foreach (var w in currentFile.Warnings.Take(5))
                    {
                        AnsiConsole.MarkupLine($"  [yellow]{Markup.Escape(w)}[/]");
                    }
                    if (currentFile.Warnings.Count > 5)
                    {
                        AnsiConsole.MarkupLine($"  [yellow]... and {currentFile.Warnings.Count - 5} more[/]");
                    }
                    AnsiConsole.MarkupLine("[yellow]Press any key to continue...[/]");
                    Console.ReadKey(true);
                }

                // Load into machine
                machine.SetFile(currentFile.GetGCode());

                // Reset height map applied state for new file
                AppState.AreProbePointsApplied = false;

                // Offer to apply existing probe data if complete
                var probePoints = AppState.ProbePoints;
                if (probePoints != null && probePoints.NotProbed.Count == 0)
                {
                    if (MenuHelpers.Confirm("Apply existing probe data to this file?", true))
                    {
                        AppState.ApplyProbeData();
                        AnsiConsole.MarkupLine("[green]Probe data applied![/]");
                    }
                }

                // Save the file path and directory for next time
                session.LastLoadedGCodeFile = path;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    session.LastBrowseDirectory = dir;
                }
                Persistence.SaveSession();
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error loading file: {Markup.Escape(ex.Message)}[/]");
                Console.ReadKey(true);
            }
        }
    }
}
