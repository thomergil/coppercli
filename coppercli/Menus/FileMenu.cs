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
        // Extra line for filename input in save mode
        private const int FileBrowserSaveExtraLines = 2;
        // Marker for "select this directory" option
        private const string SelectDirMarker = "__SELECT_DIR__";

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
            return BrowseForFile(ProbeGridExtensions, startDirectory: AppState.Session.LastProbeBrowseDirectory);
        }

        private enum SaveAction { Save, ChangeName, ChangeDir, Cancel }

        /// <summary>
        /// Simple menu-based save location picker. Shows current path and offers clear actions.
        /// </summary>
        public static string? BrowseForSaveLocation(string[] extensions, string? defaultFileName = null, string? startDirectory = null)
        {
            var session = AppState.Session;

            string currentDir = !string.IsNullOrEmpty(startDirectory) && Directory.Exists(startDirectory)
                ? startDirectory
                : !string.IsNullOrEmpty(session.LastBrowseDirectory) && Directory.Exists(session.LastBrowseDirectory)
                    ? session.LastBrowseDirectory
                    : Environment.CurrentDirectory;

            string filename = defaultFileName ?? "untitled" + (extensions.Length > 0 ? extensions[0] : "");

            while (true)
            {
                Console.Clear();

                // Title shows filename and directory
                var title = $"{FileBrowserSaveTitle}: {filename} ({currentDir})";

                var menu = new MenuDef<SaveAction>(
                    new MenuItem<SaveAction>(FileBrowserMenuSave, 's', SaveAction.Save),
                    new MenuItem<SaveAction>(FileBrowserMenuChangeName, 'n', SaveAction.ChangeName),
                    new MenuItem<SaveAction>(FileBrowserMenuChangeDir, 'd', SaveAction.ChangeDir),
                    new MenuItem<SaveAction>(MenuCancel, '\0', SaveAction.Cancel)  // Last item for Esc
                );

                var result = MenuHelpers.ShowMenuWithRefresh(title, menu);
                if (result == null)
                {
                    continue; // Status changed, redraw
                }

                switch (result.Option)
                {
                    case SaveAction.Save:
                        session.LastBrowseDirectory = currentDir;
                        Persistence.SaveSession();
                        return Path.Combine(currentDir, filename);

                    case SaveAction.ChangeName:
                        var newName = MenuHelpers.AskString(FileBrowserFilenameLabel.TrimEnd(), filename);
                        if (newName != null)
                        {
                            filename = EnsureExtension(newName, extensions);
                        }
                        break;

                    case SaveAction.ChangeDir:
                        var newDir = BrowseForFile(extensions, startDirectory: currentDir, directoryMode: true);
                        if (newDir != null)
                        {
                            currentDir = newDir;
                        }
                        break;

                    case SaveAction.Cancel:
                        return null;
                }
            }
        }


        /// <summary>
        /// Generic file browser with filter support. Returns selected file path or null if cancelled.
        /// Press / to start filtering, then type to filter. Backspace removes filter chars, Esc clears filter.
        /// In save mode, press n to edit filename, Enter saves with current filename.
        /// In directory mode, shows "[Select this directory]" option and returns directory path.
        /// </summary>
        public static string? BrowseForFile(string[] extensions, string? defaultFileName = null, string? startDirectory = null, bool saveMode = false, bool directoryMode = false)
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

            // Save mode: track filename being edited
            string filename = defaultFileName ?? "";
            bool editingFilename = false;

            while (true)
            {
                var items = new List<(string Display, string Name, string FullPath, bool IsDir)>();

                // In directory mode, add option to select current directory
                if (directoryMode)
                {
                    items.Add((FileBrowserSelectDir, SelectDirMarker, currentDir, true));
                }

                // Add parent directory option
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
                            var display = fileName.PadRight(FileBrowserNameColumnWidth) + " " + timeStr;
                            items.Add((display, fileName, file, false));
                        }
                    }
                }
                catch
                {
                    // Skip inaccessible files
                }

                // Show file browser with filter/save support
                var result = ShowFileBrowserMenu(currentDir, items, filter, filterActive, saveMode, filename, editingFilename, extensions);

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
                else if (result.Action == FileBrowserAction.FilenameChanged)
                {
                    filename = result.Filename ?? "";
                    editingFilename = result.EditingFilename;
                    // Re-render with new filename
                }
                else if (result.Action == FileBrowserAction.SaveWithFilename)
                {
                    // Save mode: return full path with filename
                    session.LastBrowseDirectory = currentDir;
                    Persistence.SaveSession();
                    return Path.Combine(currentDir, result.Filename ?? filename);
                }
                else if (result.Action == FileBrowserAction.Selected && result.SelectedItem != null)
                {
                    var selected = result.SelectedItem.Value;
                    if (selected.Name == SelectDirMarker)
                    {
                        // Directory mode: user selected current directory
                        session.LastBrowseDirectory = currentDir;
                        Persistence.SaveSession();
                        return currentDir;
                    }
                    else if (selected.IsDir)
                    {
                        currentDir = selected.FullPath;
                        filter = ""; // Clear filter when changing directory
                        filterActive = false;
                    }
                    else
                    {
                        if (saveMode)
                        {
                            // In save mode, selecting a file pre-fills the filename
                            filename = selected.Name;
                        }
                        else
                        {
                            // In select mode, return the selected file
                            session.LastBrowseDirectory = currentDir;
                            Persistence.SaveSession();
                            return selected.FullPath;
                        }
                    }
                }
            }
        }

        private enum FileBrowserAction
        {
            Cancel,
            Selected,
            FilterChanged,
            FilenameChanged,
            SaveWithFilename
        }

        private struct FileBrowserResult
        {
            public FileBrowserAction Action;
            public (string Display, string Name, string FullPath, bool IsDir)? SelectedItem;
            public string? NewFilter;
            public bool FilterActive;
            public string? Filename;
            public bool EditingFilename;
        }

        /// <summary>
        /// Shows the file browser menu with filter and save mode support.
        /// </summary>
        private static FileBrowserResult ShowFileBrowserMenu(
            string currentDir,
            List<(string Display, string Name, string FullPath, bool IsDir)> allItems,
            string filter,
            bool filterActive,
            bool saveMode = false,
            string filename = "",
            bool editingFilename = false,
            string[]? extensions = null)
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
                int chromeLines = FileBrowserChromeLines + (saveMode ? FileBrowserSaveExtraLines : 0);
                int maxVisibleItems = Math.Max(3, termHeight - chromeLines);
                bool needsScrolling = filteredItems.Count > maxVisibleItems;
                int viewStart = 0;

                // Adjust view to show selection
                if (needsScrolling && selected >= maxVisibleItems)
                {
                    viewStart = Math.Min(selected - maxVisibleItems + 1, filteredItems.Count - maxVisibleItems);
                }

                // Render
                Console.Clear();
                var title = saveMode ? FileBrowserSaveTitle : FileBrowserSelectTitle;
                AnsiConsole.Write(new Rule($"[{ColorBold} {ColorPrompt}]{title}[/]").RuleStyle(ColorPrompt));

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

                // Save mode: show filename input
                if (saveMode)
                {
                    AnsiConsole.WriteLine();
                    var filenameDisplay = string.IsNullOrEmpty(filename) ? "_" : Markup.Escape(filename);
                    if (editingFilename)
                    {
                        MenuHelpers.MarkupLineClear($"[{ColorPrompt}]{FileBrowserFilenameLabel}[/][{ColorWarning}]{filenameDisplay}_[/]");
                    }
                    else
                    {
                        MenuHelpers.MarkupLineClear($"[{ColorPrompt}]{FileBrowserFilenameLabel}[/]{filenameDisplay}");
                    }
                }

                // Help text
                if (editingFilename)
                {
                    MenuHelpers.MarkupLineClear($"[{ColorDim}]{FileBrowserHelpEditName}[/]");
                }
                else if (filterActive)
                {
                    MenuHelpers.MarkupLineClear($"[{ColorDim}]{FileBrowserHelpFilter}[/]");
                }
                else if (saveMode)
                {
                    MenuHelpers.MarkupLineClear($"[{ColorDim}]{FileBrowserHelpSave}[/]");
                }
                else
                {
                    MenuHelpers.MarkupLineClear($"[{ColorDim}]{FileBrowserHelpSelect}[/]");
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

                // Handle keys based on current mode
                if (editingFilename)
                {
                    // Editing filename - typing adds to filename, Backspace removes, Esc/Enter exits
                    if (InputHelpers.IsEscapeKey(key))
                    {
                        // Exit filename edit mode
                        return new FileBrowserResult { Action = FileBrowserAction.FilenameChanged, Filename = filename, EditingFilename = false };
                    }
                    else if (InputHelpers.IsEnterKey(key))
                    {
                        // Save with current filename
                        var finalFilename = EnsureExtension(filename, extensions);
                        if (!string.IsNullOrWhiteSpace(finalFilename))
                        {
                            return new FileBrowserResult { Action = FileBrowserAction.SaveWithFilename, Filename = finalFilename };
                        }
                        // Empty filename - just exit edit mode
                        return new FileBrowserResult { Action = FileBrowserAction.FilenameChanged, Filename = filename, EditingFilename = false };
                    }
                    else if (key.Key == ConsoleKey.Backspace)
                    {
                        if (filename.Length > 0)
                        {
                            return new FileBrowserResult { Action = FileBrowserAction.FilenameChanged, Filename = filename[..^1], EditingFilename = true };
                        }
                    }
                    else if (!char.IsControl(key.KeyChar))
                    {
                        // Add character to filename (skip invalid path chars)
                        if (!Path.GetInvalidFileNameChars().Contains(key.KeyChar))
                        {
                            return new FileBrowserResult { Action = FileBrowserAction.FilenameChanged, Filename = filename + key.KeyChar, EditingFilename = true };
                        }
                    }
                }
                else if (filterActive)
                {
                    // Currently filtering - typing adds to filter, Backspace removes, Esc clears
                    if (InputHelpers.IsEscapeKey(key))
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
                    else if (InputHelpers.IsEnterKey(key) && filteredItems.Count > 0)
                    {
                        return new FileBrowserResult { Action = FileBrowserAction.Selected, SelectedItem = filteredItems[selected] };
                    }
                    else if (!char.IsControl(key.KeyChar))
                    {
                        // Add character to filter
                        return new FileBrowserResult { Action = FileBrowserAction.FilterChanged, NewFilter = filter + key.KeyChar, FilterActive = true };
                    }
                    else
                    {
                        selected = HandleNavigationKey(key.Key, selected, filteredItems.Count, maxVisibleItems);
                    }
                }
                else
                {
                    // Normal mode - shortcuts select, / starts filter, n starts filename edit (save mode)
                    char pressedUpper = char.ToUpper(key.KeyChar);
                    if (saveMode && pressedUpper == 'N')
                    {
                        // Start editing filename
                        return new FileBrowserResult { Action = FileBrowserAction.FilenameChanged, Filename = filename, EditingFilename = true };
                    }
                    else if (shortcuts.TryGetValue(pressedUpper, out int shortcutIdx))
                    {
                        return new FileBrowserResult { Action = FileBrowserAction.Selected, SelectedItem = filteredItems[shortcutIdx] };
                    }
                    else if (key.KeyChar == '/')
                    {
                        return new FileBrowserResult { Action = FileBrowserAction.FilterChanged, NewFilter = "", FilterActive = true };
                    }
                    else if (InputHelpers.IsEscapeKey(key))
                    {
                        return new FileBrowserResult { Action = FileBrowserAction.Cancel };
                    }
                    else if (InputHelpers.IsEnterKey(key))
                    {
                        if (saveMode && !string.IsNullOrWhiteSpace(filename))
                        {
                            // Save with current filename
                            var finalFilename = EnsureExtension(filename, extensions);
                            return new FileBrowserResult { Action = FileBrowserAction.SaveWithFilename, Filename = finalFilename };
                        }
                        else if (filteredItems.Count > 0)
                        {
                            return new FileBrowserResult { Action = FileBrowserAction.Selected, SelectedItem = filteredItems[selected] };
                        }
                    }
                    else
                    {
                        selected = HandleNavigationKey(key.Key, selected, filteredItems.Count, maxVisibleItems);
                    }
                }
            }
        }

        /// <summary>
        /// Ensures the filename has the correct extension.
        /// </summary>
        private static string EnsureExtension(string filename, string[]? extensions)
        {
            if (string.IsNullOrWhiteSpace(filename) || extensions == null || extensions.Length == 0)
            {
                return filename;
            }

            var ext = Path.GetExtension(filename).ToLower();
            if (extensions.Contains(ext))
            {
                return filename;
            }

            // Append the first valid extension
            return filename + extensions[0];
        }

        /// <summary>
        /// Handles navigation keys (arrows, Page Up/Down, Home/End) for list selection.
        /// Returns the new selected index.
        /// </summary>
        private static int HandleNavigationKey(ConsoleKey key, int selected, int itemCount, int pageSize)
        {
            return key switch
            {
                ConsoleKey.UpArrow => (selected - 1 + itemCount) % Math.Max(1, itemCount),
                ConsoleKey.DownArrow => (selected + 1) % Math.Max(1, itemCount),
                ConsoleKey.PageUp => Math.Max(0, selected - pageSize),
                ConsoleKey.PageDown => Math.Min(itemCount - 1, selected + pageSize),
                ConsoleKey.Home => 0,
                ConsoleKey.End => Math.Max(0, itemCount - 1),
                _ => selected
            };
        }

        public static void LoadGCodeFromPath(string path)
        {
            var session = AppState.Session;
            var machine = AppState.Machine;

            // Expand ~ for home directory
            path = PathHelpers.ExpandTilde(path);

            if (!File.Exists(path))
            {
                AnsiConsole.MarkupLine($"[{ColorError}]File not found: {Markup.Escape(path)}[/]");
                MenuHelpers.WaitEnter();
                return;
            }

            try
            {
                var currentFile = GCodeFile.Load(path);

                if (currentFile.Warnings.Count > 0)
                {
                    AnsiConsole.MarkupLine($"[{ColorWarning}]Warnings ({currentFile.Warnings.Count}):[/]");
                    foreach (var w in currentFile.Warnings.Take(MaxFileLoadWarningsShown))
                    {
                        AnsiConsole.MarkupLine($"  [{ColorWarning}]{Markup.Escape(w)}[/]");
                    }
                    if (currentFile.Warnings.Count > MaxFileLoadWarningsShown)
                    {
                        AnsiConsole.MarkupLine($"  [{ColorWarning}]... and {currentFile.Warnings.Count - MaxFileLoadWarningsShown} more[/]");
                    }
                    MenuHelpers.WaitEnter();
                }

                // Load into machine (sets CurrentFile, loads to machine, resets probe state)
                AppState.LoadGCodeIntoMachine(currentFile);

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
