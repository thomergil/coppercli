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
        // Lines used by file browser chrome (title, directory, blank, help text)
        private const int FileBrowserChromeLines = 5;

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
        /// Generic file browser with filter support. Returns selected file path or null if cancelled.
        /// Press / to start filtering, then type to filter. Backspace removes filter chars, Esc clears filter.
        /// </summary>
        public static string? BrowseForFile(string[] extensions, string? defaultFileName = null, string? startDirectory = null)
        {
            var session = AppState.Session;

            // Start at specified directory, last browse directory, or current directory
            string currentDir = !string.IsNullOrEmpty(startDirectory) && Directory.Exists(startDirectory)
                ? startDirectory
                : !string.IsNullOrEmpty(session.LastBrowseDirectory) && Directory.Exists(session.LastBrowseDirectory)
                    ? session.LastBrowseDirectory
                    : Environment.CurrentDirectory;

            string filter = "";
            bool filterActive = false;

            while (true)
            {
                var items = new List<(string Display, string Name, string FullPath, bool IsDir)>();

                // Add parent directory option first
                var parent = Directory.GetParent(currentDir);
                if (parent != null)
                {
                    items.Add(("..", "..", parent.FullName, true));
                }

                // Add subdirectories
                try
                {
                    foreach (var dir in Directory.GetDirectories(currentDir).OrderBy(d => Path.GetFileName(d)))
                    {
                        var name = Path.GetFileName(dir);
                        if (!name.StartsWith("."))
                        {
                            items.Add((name + "/", name, dir, true));
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
                            var fileName = Path.GetFileName(file);
                            var modTime = File.GetLastWriteTime(file);
                            var timeStr = modTime.ToString("MMM dd HH:mm");
                            var display = $"{fileName,-30} {timeStr}";
                            items.Add((display, fileName, file, false));
                        }
                    }
                }
                catch
                {
                    // Skip inaccessible files
                }

                // Show file browser with filter support
                var result = ShowFileBrowserMenu(currentDir, items, filter, filterActive);

                if (result.Action == FileBrowserAction.Cancel)
                {
                    return null;
                }
                else if (result.Action == FileBrowserAction.FilterChanged)
                {
                    filter = result.NewFilter ?? "";
                    filterActive = result.FilterActive;
                    // Re-render with new filter (same directory)
                }
                else if (result.Action == FileBrowserAction.Selected && result.SelectedItem != null)
                {
                    var selected = result.SelectedItem.Value;
                    if (selected.IsDir)
                    {
                        currentDir = selected.FullPath;
                        filter = ""; // Clear filter when changing directory
                        filterActive = false;
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

        private enum FileBrowserAction
        {
            Cancel,
            Selected,
            FilterChanged
        }

        private struct FileBrowserResult
        {
            public FileBrowserAction Action;
            public (string Display, string Name, string FullPath, bool IsDir)? SelectedItem;
            public string? NewFilter;
            public bool FilterActive;
        }

        /// <summary>
        /// Shows the file browser menu with filter support.
        /// </summary>
        private static FileBrowserResult ShowFileBrowserMenu(
            string currentDir,
            List<(string Display, string Name, string FullPath, bool IsDir)> allItems,
            string filter,
            bool filterActive)
        {
            // Filter items based on current filter (case-insensitive, matches Name)
            var filteredItems = string.IsNullOrEmpty(filter)
                ? allItems
                : allItems.Where(i => i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

            int selected = 0;

            while (true)
            {
                // Calculate viewport size based on terminal height
                var (termWidth, termHeight) = DisplayHelpers.GetSafeWindowSize();
                int maxVisibleItems = Math.Max(3, termHeight - FileBrowserChromeLines);
                bool needsScrolling = filteredItems.Count > maxVisibleItems;
                int viewStart = 0;

                // Adjust view to show selection
                if (needsScrolling && selected >= maxVisibleItems)
                {
                    viewStart = Math.Min(selected - maxVisibleItems + 1, filteredItems.Count - maxVisibleItems);
                }

                // Render
                Console.Clear();
                AnsiConsole.Write(new Rule($"[{ColorBold} {ColorPrompt}]Select File[/]").RuleStyle(ColorPrompt));

                // Show directory and filter
                if (filterActive)
                {
                    var filterDisplay = string.IsNullOrEmpty(filter) ? "_" : Markup.Escape(filter);
                    AnsiConsole.MarkupLine($"[{ColorDim}]{Markup.Escape(currentDir)}[/]  [{ColorWarning}]Filter: {filterDisplay}[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[{ColorDim}]{Markup.Escape(currentDir)}[/]");
                }
                AnsiConsole.WriteLine();

                // Ensure selection is valid
                selected = Math.Clamp(selected, 0, Math.Max(0, filteredItems.Count - 1));

                // Adjust view to keep selection visible
                if (needsScrolling)
                {
                    viewStart = Math.Clamp(viewStart, 0, Math.Max(0, filteredItems.Count - maxVisibleItems));
                    if (selected < viewStart)
                    {
                        viewStart = selected;
                    }
                    else if (selected >= viewStart + maxVisibleItems)
                    {
                        viewStart = selected - maxVisibleItems + 1;
                    }
                }

                int viewEnd = needsScrolling ? Math.Min(viewStart + maxVisibleItems, filteredItems.Count) : filteredItems.Count;
                bool hasMoreAbove = viewStart > 0;
                bool hasMoreBelow = viewEnd < filteredItems.Count;

                // Show "more above" indicator
                if (hasMoreAbove)
                {
                    MenuHelpers.MarkupLineClear($"[{ColorDim}]  ▲ {viewStart} more above[/]");
                }

                // Draw visible options with shortcuts
                for (int i = viewStart; i < viewEnd; i++)
                {
                    var item = filteredItems[i];
                    string escapedDisplay = Markup.Escape(item.Display);
                    var shortcut = InputHelpers.GetMenuKey(i);
                    var prefix = shortcut.HasValue ? $"{shortcut}." : "  ";

                    if (i == selected)
                    {
                        MenuHelpers.MarkupLineClear($"[{ColorSuccess}]> {prefix} {escapedDisplay}[/]");
                    }
                    else
                    {
                        MenuHelpers.MarkupLineClear($"  {prefix} {escapedDisplay}");
                    }
                }

                // Show "more below" indicator
                if (hasMoreBelow)
                {
                    MenuHelpers.MarkupLineClear($"[{ColorDim}]  ▼ {filteredItems.Count - viewEnd} more below[/]");
                }

                // Help text
                if (!filterActive)
                {
                    MenuHelpers.MarkupLineClear($"[{ColorDim}]↑↓ navigate, Enter select, / filter, Esc cancel[/]");
                }
                else
                {
                    MenuHelpers.MarkupLineClear($"[{ColorDim}]↑↓ navigate, Enter select, type to filter, Esc clear[/]");
                }

                // Read key
                var keyOrNull = InputHelpers.ReadKeyPolling();
                if (keyOrNull == null)
                {
                    continue; // Status changed, redraw
                }
                var key = keyOrNull.Value;

                // Build shortcut map for current filtered items
                var shortcuts = new Dictionary<char, int>();
                for (int i = 0; i < filteredItems.Count && i < MaxMenuShortcuts; i++)
                {
                    var shortcut = InputHelpers.GetMenuKey(i);
                    if (shortcut.HasValue)
                    {
                        shortcuts[char.ToUpper(shortcut.Value)] = i;
                    }
                }

                // Handle keys differently based on filter state
                if (!filterActive)
                {
                    // Not filtering - shortcuts select, / starts filter, other keys navigate
                    char pressedUpper = char.ToUpper(key.KeyChar);
                    if (shortcuts.TryGetValue(pressedUpper, out int shortcutIdx))
                    {
                        return new FileBrowserResult { Action = FileBrowserAction.Selected, SelectedItem = filteredItems[shortcutIdx] };
                    }
                    else if (key.KeyChar == '/')
                    {
                        return new FileBrowserResult { Action = FileBrowserAction.FilterChanged, NewFilter = "", FilterActive = true };
                    }
                    else if (key.Key == ConsoleKey.Escape)
                    {
                        return new FileBrowserResult { Action = FileBrowserAction.Cancel };
                    }
                    else if (key.Key == ConsoleKey.Enter && filteredItems.Count > 0)
                    {
                        return new FileBrowserResult { Action = FileBrowserAction.Selected, SelectedItem = filteredItems[selected] };
                    }
                    else if (key.Key == ConsoleKey.UpArrow)
                    {
                        selected = (selected - 1 + filteredItems.Count) % Math.Max(1, filteredItems.Count);
                    }
                    else if (key.Key == ConsoleKey.DownArrow)
                    {
                        selected = (selected + 1) % Math.Max(1, filteredItems.Count);
                    }
                    else if (key.Key == ConsoleKey.PageUp)
                    {
                        selected = Math.Max(0, selected - maxVisibleItems);
                    }
                    else if (key.Key == ConsoleKey.PageDown)
                    {
                        selected = Math.Min(filteredItems.Count - 1, selected + maxVisibleItems);
                    }
                    else if (key.Key == ConsoleKey.Home)
                    {
                        selected = 0;
                    }
                    else if (key.Key == ConsoleKey.End)
                    {
                        selected = Math.Max(0, filteredItems.Count - 1);
                    }
                }
                else
                {
                    // Currently filtering - typing adds to filter, Backspace removes, Esc clears
                    if (key.Key == ConsoleKey.Escape)
                    {
                        // Clear filter and exit filter mode
                        return new FileBrowserResult { Action = FileBrowserAction.FilterChanged, NewFilter = "", FilterActive = false };
                    }
                    else if (key.Key == ConsoleKey.Backspace)
                    {
                        if (filter.Length > 0)
                        {
                            return new FileBrowserResult { Action = FileBrowserAction.FilterChanged, NewFilter = filter[..^1], FilterActive = true };
                        }
                        else
                        {
                            // Backspace on empty filter exits filter mode
                            return new FileBrowserResult { Action = FileBrowserAction.FilterChanged, NewFilter = "", FilterActive = false };
                        }
                    }
                    else if (key.Key == ConsoleKey.Enter && filteredItems.Count > 0)
                    {
                        return new FileBrowserResult { Action = FileBrowserAction.Selected, SelectedItem = filteredItems[selected] };
                    }
                    else if (key.Key == ConsoleKey.UpArrow)
                    {
                        selected = (selected - 1 + filteredItems.Count) % Math.Max(1, filteredItems.Count);
                    }
                    else if (key.Key == ConsoleKey.DownArrow)
                    {
                        selected = (selected + 1) % Math.Max(1, filteredItems.Count);
                    }
                    else if (key.Key == ConsoleKey.PageUp)
                    {
                        selected = Math.Max(0, selected - maxVisibleItems);
                    }
                    else if (key.Key == ConsoleKey.PageDown)
                    {
                        selected = Math.Min(filteredItems.Count - 1, selected + maxVisibleItems);
                    }
                    else if (key.Key == ConsoleKey.Home)
                    {
                        selected = 0;
                    }
                    else if (key.Key == ConsoleKey.End)
                    {
                        selected = Math.Max(0, filteredItems.Count - 1);
                    }
                    else if (!char.IsControl(key.KeyChar))
                    {
                        // Add character to filter
                        return new FileBrowserResult { Action = FileBrowserAction.FilterChanged, NewFilter = filter + key.KeyChar, FilterActive = true };
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
                AnsiConsole.MarkupLine($"[{ColorError}]File not found: {Markup.Escape(path)}[/]");
                MenuHelpers.WaitEnter();
                return;
            }

            try
            {
                var currentFile = GCodeFile.Load(path);
                AppState.CurrentFile = currentFile;

                if (currentFile.Warnings.Count > 0)
                {
                    AnsiConsole.MarkupLine($"[{ColorWarning}]Warnings ({currentFile.Warnings.Count}):[/]");
                    foreach (var w in currentFile.Warnings.Take(5))
                    {
                        AnsiConsole.MarkupLine($"  [{ColorWarning}]{Markup.Escape(w)}[/]");
                    }
                    if (currentFile.Warnings.Count > 5)
                    {
                        AnsiConsole.MarkupLine($"  [{ColorWarning}]... and {currentFile.Warnings.Count - 5} more[/]");
                    }
                    MenuHelpers.WaitEnter();
                }

                // Load into machine
                machine.SetFile(currentFile.GetGCode());

                // Reset height map applied state and depth adjustment for new file
                AppState.ResetProbeApplicationState();

                // Offer to apply existing probe data if complete
                var probePoints = AppState.ProbePoints;
                if (probePoints != null && probePoints.NotProbed.Count == 0)
                {
                    if (MenuHelpers.Confirm("Apply existing probe data to this file?", true))
                    {
                        AppState.ApplyProbeData();
                        AnsiConsole.MarkupLine($"[{ColorSuccess}]Probe data applied![/]");
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
                AnsiConsole.MarkupLine($"[{ColorError}]Error loading file: {Markup.Escape(ex.Message)}[/]");
                InputHelpers.WaitForKeyPolling();
            }
        }
    }
}
