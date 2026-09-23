using coppercli.Core.GCode;
using coppercli.Helpers;
using Spectre.Console;
using static coppercli.CliConstants;

namespace coppercli.Menus
{
    internal static class FileMenu
    {
        private const int FileBrowserChromeLines = 5;
        private const int FileBrowserSaveExtraLines = 2;
        private const string SelectDirMarker = "__SELECT_DIR__";

        private static string ResolveStartDirectory(string? startDirectory)
        {
            if (!string.IsNullOrEmpty(startDirectory) && Directory.Exists(startDirectory))
            {
                return startDirectory;
            }
            var session = AppState.Session;
            if (!string.IsNullOrEmpty(session.LastBrowseDirectory) && Directory.Exists(session.LastBrowseDirectory))
            {
                return session.LastBrowseDirectory;
            }
            return Environment.CurrentDirectory;
        }

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

        public static string? BrowseForSaveLocation(string[] extensions, string? defaultFileName = null, string? startDirectory = null)
        {
            var session = AppState.Session;

            string currentDir = ResolveStartDirectory(startDirectory);

            string filename = defaultFileName ?? "untitled" + (extensions.Length > 0 ? extensions[0] : "");

            while (true)
            {
                Console.Clear();

                var title = $"{FileBrowserSaveTitle}: {filename} ({currentDir})";

                var menu = new MenuDef<SaveAction>(
                    new MenuItem<SaveAction>(FileBrowserMenuSave, 's', SaveAction.Save),
                    new MenuItem<SaveAction>(FileBrowserMenuChangeName, 'n', SaveAction.ChangeName),
                    new MenuItem<SaveAction>(FileBrowserMenuChangeDir, 'd', SaveAction.ChangeDir),
                    new MenuItem<SaveAction>(MenuCancel, '\0', SaveAction.Cancel)  // Escape returns the last item
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
                            filename = PathHelpers.EnsureExtension(newName, extensions);
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
        /// Returns the chosen file, in `saveMode` the directory plus the typed filename, and in
        /// `directoryMode` the directory itself. Null means the operator cancelled.
        /// </summary>
        public static string? BrowseForFile(string[] extensions, string? defaultFileName = null, string? startDirectory = null, bool saveMode = false, bool directoryMode = false)
        {
            var session = AppState.Session;

            string currentDir = ResolveStartDirectory(startDirectory);

            string filter = "";
            bool filterActive = false;

            string filename = defaultFileName ?? "";
            bool editingFilename = false;

            while (true)
            {
                var items = new List<(string Display, string Name, string FullPath, bool IsDir)>();

                if (directoryMode)
                {
                    items.Add((FileBrowserSelectDir, SelectDirMarker, currentDir, true));
                }

                var parent = Directory.GetParent(currentDir);
                if (parent != null)
                {
                    items.Add(("..", "..", parent.FullName, true));
                }

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

                var result = ShowFileBrowserMenu(currentDir, items, filter, filterActive, saveMode, filename, editingFilename, extensions);

                if (result.Action == FileBrowserAction.Cancel)
                {
                    return null;
                }
                else if (result.Action == FileBrowserAction.FilterChanged)
                {
                    filter = result.NewFilter ?? "";
                    filterActive = result.FilterActive;
                }
                else if (result.Action == FileBrowserAction.FilenameChanged)
                {
                    filename = result.Filename ?? "";
                    editingFilename = result.EditingFilename;
                }
                else if (result.Action == FileBrowserAction.SaveWithFilename)
                {
                    session.LastBrowseDirectory = currentDir;
                    Persistence.SaveSession();
                    return Path.Combine(currentDir, result.Filename ?? filename);
                }
                else if (result.Action == FileBrowserAction.Selected && result.SelectedItem != null)
                {
                    var selected = result.SelectedItem.Value;
                    if (selected.Name == SelectDirMarker)
                    {
                        session.LastBrowseDirectory = currentDir;
                        Persistence.SaveSession();
                        return currentDir;
                    }
                    else if (selected.IsDir)
                    {
                        currentDir = selected.FullPath;
                        filter = "";
                        filterActive = false;
                    }
                    else
                    {
                        if (saveMode)
                        {
                            filename = selected.Name;
                        }
                        else
                        {
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
            var filteredItems = string.IsNullOrEmpty(filter)
                ? allItems
                : allItems.Where(i => i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

            int selected = 0;

            while (true)
            {
                var (termWidth, termHeight) = DisplayHelpers.GetSafeWindowSize();
                int chromeLines = FileBrowserChromeLines + (saveMode ? FileBrowserSaveExtraLines : 0);
                int maxVisibleItems = Math.Max(3, termHeight - chromeLines);
                bool needsScrolling = filteredItems.Count > maxVisibleItems;
                int viewStart = 0;

                if (needsScrolling && selected >= maxVisibleItems)
                {
                    viewStart = Math.Min(selected - maxVisibleItems + 1, filteredItems.Count - maxVisibleItems);
                }

                Console.Clear();
                var title = saveMode ? FileBrowserSaveTitle : FileBrowserSelectTitle;
                AnsiConsole.Write(new Rule($"[{ColorBold} {ColorPrompt}]{title}[/]").RuleStyle(ColorPrompt));

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

                selected = Math.Clamp(selected, 0, Math.Max(0, filteredItems.Count - 1));

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

                if (hasMoreAbove)
                {
                    MenuHelpers.MarkupLineClear($"[{ColorDim}]  ▲ {viewStart} more above[/]");
                }

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

                if (hasMoreBelow)
                {
                    MenuHelpers.MarkupLineClear($"[{ColorDim}]  ▼ {filteredItems.Count - viewEnd} more below[/]");
                }

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

                var keyOrNull = InputHelpers.ReadKeyPolling();
                if (keyOrNull == null)
                {
                    continue; // Status changed, redraw
                }
                var key = keyOrNull.Value;

                var shortcuts = new Dictionary<char, int>();
                for (int i = 0; i < filteredItems.Count && i < MaxMenuShortcuts; i++)
                {
                    var shortcut = InputHelpers.GetMenuKey(i);
                    if (shortcut.HasValue)
                    {
                        shortcuts[char.ToUpper(shortcut.Value)] = i;
                    }
                }

                if (editingFilename)
                {
                    if (InputHelpers.IsEscapeKey(key))
                    {
                        return new FileBrowserResult { Action = FileBrowserAction.FilenameChanged, Filename = filename, EditingFilename = false };
                    }
                    else if (InputHelpers.IsEnterKey(key))
                    {
                        var finalFilename = PathHelpers.EnsureExtension(filename, extensions);
                        if (!string.IsNullOrWhiteSpace(finalFilename))
                        {
                            return new FileBrowserResult { Action = FileBrowserAction.SaveWithFilename, Filename = finalFilename };
                        }
                        return new FileBrowserResult { Action = FileBrowserAction.FilenameChanged, Filename = filename, EditingFilename = false };
                    }
                    else if (InputHelpers.IsKey(key, ConsoleKey.Backspace))
                    {
                        if (filename.Length > 0)
                        {
                            return new FileBrowserResult { Action = FileBrowserAction.FilenameChanged, Filename = filename[..^1], EditingFilename = true };
                        }
                    }
                    else if (!char.IsControl(key.KeyChar))
                    {
                        if (!Path.GetInvalidFileNameChars().Contains(key.KeyChar))
                        {
                            return new FileBrowserResult { Action = FileBrowserAction.FilenameChanged, Filename = filename + key.KeyChar, EditingFilename = true };
                        }
                    }
                }
                else if (filterActive)
                {
                    if (InputHelpers.IsEscapeKey(key))
                    {
                        return new FileBrowserResult { Action = FileBrowserAction.FilterChanged, NewFilter = "", FilterActive = false };
                    }
                    else if (InputHelpers.IsKey(key, ConsoleKey.Backspace))
                    {
                        if (filter.Length > 0)
                        {
                            return new FileBrowserResult { Action = FileBrowserAction.FilterChanged, NewFilter = filter[..^1], FilterActive = true };
                        }
                        else
                        {
                            return new FileBrowserResult { Action = FileBrowserAction.FilterChanged, NewFilter = "", FilterActive = false };
                        }
                    }
                    else if (InputHelpers.IsEnterKey(key) && filteredItems.Count > 0)
                    {
                        return new FileBrowserResult { Action = FileBrowserAction.Selected, SelectedItem = filteredItems[selected] };
                    }
                    else if (!char.IsControl(key.KeyChar))
                    {
                        return new FileBrowserResult { Action = FileBrowserAction.FilterChanged, NewFilter = filter + key.KeyChar, FilterActive = true };
                    }
                    else
                    {
                        selected = HandleNavigationKey(key.Key, selected, filteredItems.Count, maxVisibleItems);
                    }
                }
                else
                {
                    char pressedUpper = char.ToUpper(key.KeyChar);
                    if (saveMode && pressedUpper == 'N')
                    {
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
                            var finalFilename = PathHelpers.EnsureExtension(filename, extensions);
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

                // `LoadGCodeIntoMachine` also sets CurrentFile and resets the probe state.
                var loaded = AppState.LoadGCodeIntoMachine(currentFile);
                if (loaded.Refused != null)
                {
                    MenuHelpers.ShowError(loaded.Refused);
                    return;
                }

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    session.LastBrowseDirectory = dir;
                }
                Persistence.SaveSession();

                // `LoadGCodeIntoMachine` already discarded a height map that no longer
                // matches; this only reports why.
                string? whyDropped = loaded.MapDiscardedBecause;
                if (whyDropped != null)
                {
                    AnsiConsole.MarkupLine(
                        $"[{ColorWarning}]{string.Format(HeightMapDiscardedOnLoad, Markup.Escape(whyDropped))}[/]");
                }

                // The session questions settle any saved map or unadopted autosave, so the
                // apply question below covers only an adopted map.
                SessionRestore.AskPendingSteps(
                    step => MenuHelpers.AskSessionStep(step, offerQuit: false),
                    MenuHelpers.ShowError);

                if (AppState.HasCompleteMapNotApplied)
                {
                    if (MenuHelpers.Confirm(ExistingHeightMapQuestion, true))
                    {
                        string? notApplied = AppState.ApplyProbeData();
                        if (notApplied == null)
                        {
                            AnsiConsole.MarkupLine($"[{ColorSuccess}]{HeightMapApplied}[/]");
                        }
                        else
                        {
                            MenuHelpers.ShowError(notApplied);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MenuHelpers.ShowFailure(CliConstants.FailedLoadingTheFile, ex);
                InputHelpers.WaitForKeyPolling();
            }
        }
    }
}
