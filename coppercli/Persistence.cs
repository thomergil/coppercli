using coppercli.Core.GCode;
using coppercli.Core.Settings;
using coppercli.Core.Util;
using coppercli.Helpers;
using Spectre.Console;
using System.Text.Json;
using static coppercli.CliConstants;

namespace coppercli
{
    internal static class Persistence
    {
        private static string GetAppDataDir()
        {
            var appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppTitle);
            Directory.CreateDirectory(appDataDir);
            return appDataDir;
        }

        private static string GetSettingsPath()
        {
            return Path.Combine(GetAppDataDir(), SettingsFileName);
        }

        private static string GetSessionPath()
        {
            return Path.Combine(GetAppDataDir(), SessionFileName);
        }

        public static string GetProbeAutoSavePath()
        {
            return Path.Combine(GetAppDataDir(), ProbeAutoSaveFileName);
        }

        /// <summary>
        /// The parsed autosave, or null if it is absent or unreadable. Return a new object
        /// each time so measurements added by a caller do not appear in later reads.
        /// </summary>
        public static ProbeGrid? ReadProbeAutoSave()
        {
            var path = GetProbeAutoSavePath();
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return ProbeGrid.Load(path);
            }
            catch (Exception ex)
            {
                Logger.Log("ReadProbeAutoSave: failed to load {0} - {1}", path, ex.Message);
                return null;
            }
        }

        // A property renamed in `MachineSettings` needs an entry here, tagged with the version
        // that renamed it, or an existing user's value is dropped on upgrade. `MigrateSettings`
        // skips an entry whose new name is already in the file, so entries can stay forever.
        private static readonly (string Old, string New)[] SettingsMigrations =
        {
            ("OutlineTraverseHeight", "OutlineTraceHeight"),   // v0.4.0
            ("OutlineTraverseFeed", "OutlineTraceFeed"),       // v0.4.0
        };

        public static MachineSettings LoadSettings()
        {
            try
            {
                var path = GetSettingsPath();
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    json = MigrateSettings(json);
                    var loaded = JsonSerializer.Deserialize<MachineSettings>(json) ?? new MachineSettings();
                    RepairOutOfRangeSettings(loaded);
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                RenameUnreadableFile(GetSettingsPath(), ex);
            }
            return new MachineSettings();
        }

        /// <summary>
        /// Put back the default for any setting the file cannot supply a usable value for.
        /// Repaired rather than refused, so one bad value cannot stop coppercli starting.
        /// </summary>
        internal static void RepairOutOfRangeSettings(MachineSettings settings)
        {
            var defaults = new MachineSettings();

            foreach (var binding in SettingRanges.All)
            {
                string? refused = binding.Range.Check(binding.Read(settings));
                if (refused == null)
                {
                    continue;
                }

                Logger.Log("Settings: {0} Using the default instead.", refused);
                binding.Write(settings, binding.Read(defaults));
            }
        }

        private static string MigrateSettings(string json)
        {
            bool migrated = false;
            foreach (var (old, @new) in SettingsMigrations)
            {
                if (json.Contains($"\"{old}\"") && !json.Contains($"\"{@new}\""))
                {
                    json = json.Replace($"\"{old}\"", $"\"{@new}\"");
                    Logger.Log($"Settings migration: {old} → {@new}");
                    migrated = true;
                }
            }

            if (migrated)
            {
                try
                {
                    AtomicFile.WriteAllText(GetSettingsPath(), json);
                    Logger.Log("Settings migration: saved migrated file");
                }
                catch
                {
                    // Not fatal: the next load migrates again.
                }
            }

            return json;
        }

        public static void SaveSettings()
        {
            try
            {
                var path = GetSettingsPath();
                var json = JsonSerializer.Serialize(AppState.Settings, AppState.JsonOptions);
                AtomicFile.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                // Shown, not only logged: the setting looks changed on screen and is gone
                // at the next launch.
                Logger.Log("SaveSettings failed: {0}", ex.Message);
                AnsiConsole.MarkupLine($"[{ColorWarning}]{SettingsNotSaved}[/]");
            }
        }

        public static SessionState LoadSession()
        {
            try
            {
                var path = GetSessionPath();
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize<SessionState>(json) ?? new SessionState();
                }
            }
            catch (Exception ex)
            {
                RenameUnreadableFile(GetSessionPath(), ex);
            }
            return new SessionState();
        }

        public static void SaveSession()
        {
            try
            {
                var path = GetSessionPath();
                var json = JsonSerializer.Serialize(AppState.Session, AppState.JsonOptions);
                AtomicFile.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                Logger.Log("SaveSession failed: {0}", ex.Message);
            }
        }

        /// <summary>
        /// Moves a file we could not read aside so the next save does not overwrite it,
        /// and records why. Silently falling back to defaults would reset probe feeds,
        /// depths and the tool-setter position with nothing said.
        /// </summary>
        private static void RenameUnreadableFile(string path, Exception ex)
        {
            Logger.Log("Could not read {0} ({1}); using defaults", path, ex.Message);

            // Shown on screen, not only logged, because the log is off without --debug: the
            // operator is about to run with default probe feeds, depths and tool-setter
            // position.
            AnsiConsole.MarkupLine(
                $"[{ColorWarning}]Could not read {Markup.Escape(Path.GetFileName(path))} - " +
                $"using default settings. The unreadable file has been kept alongside it.[/]");

            try
            {
                if (File.Exists(path))
                {
                    File.Move(path, path + ".unreadable", overwrite: true);
                    Logger.Log("Moved unreadable file to {0}.unreadable", path);
                }
            }
            catch (Exception moveEx)
            {
                Logger.Log("Could not set aside {0}: {1}", path, moveEx.Message);
            }
        }

        public static void SaveProbeProgress()
        {
            var probePoints = AppState.ProbePoints;
            if (probePoints == null)
            {
                Logger.Log("SaveProbeProgress: ProbePoints is null, skipping");
                return;
            }

            try
            {
                var path = GetProbeAutoSavePath();
                Logger.Log("SaveProbeProgress: saving {0}/{1} probed to {2}",
                    probePoints.Progress, probePoints.TotalPoints, path);
                probePoints.Save(path);

                // `SessionRestore` offers the G-code back with the probe data, so record which
                // file was loaded when the probe started.
                if (string.IsNullOrEmpty(AppState.Session.ProbeSourceGCodeFile) &&
                    !string.IsNullOrEmpty(AppState.Session.LastLoadedGCodeFile))
                {
                    AppState.Session.ProbeSourceGCodeFile = AppState.Session.LastLoadedGCodeFile;
                    Logger.Log("SaveProbeProgress: set ProbeSourceGCodeFile={0}", AppState.Session.ProbeSourceGCodeFile);
                }

                SaveSession();
            }
            catch (Exception ex)
            {
                Logger.Log("SaveProbeProgress: failed - {0}", ex.Message);
            }
        }

        /// <returns>False if the file is still there, so no caller reports the data gone
        /// while it is still on disk.</returns>
        public static bool ClearProbeAutoSave()
        {
            var path = GetProbeAutoSavePath();

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Logger.Log("ClearProbeAutoSave: could not delete {0} - {1}", path, ex.Message);
                return false;
            }

            AppState.Session.ProbeSourceGCodeFile = null;
            SaveSession();
            return true;
        }

        /// <summary>
        /// Deletes the autosave once the map is written, so nothing offers the same map back
        /// as unsaved work.
        /// </summary>
        public static bool SaveProbeToFile(string newPath)
        {
            // The map the screens offered to save: the one in memory, or the autosave when
            // nothing is loaded. Reading the raw file state instead saved a map measured for
            // another board.
            var grid = AppState.CurrentProbeGrid;

            if (grid == null)
            {
                Logger.Log("SaveProbeToFile: nothing to save");
                return false;
            }

            try
            {
                var targetDir = Path.GetDirectoryName(newPath);
                if (!string.IsNullOrEmpty(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                grid.Save(newPath);

                // The autosave goes only once the grid is held in memory. Deleting it while
                // the map lived only in that file dropped the map from the job, and the Mill
                // button stopped refusing an unapplied map.
                if (AppState.ProbePoints == null && AppState.AdoptProbeGrid(grid) != null)
                {
                    Logger.Log("SaveProbeToFile: wrote {0}, keeping the autosave", newPath);
                    return true;
                }

                var autosavePath = GetProbeAutoSavePath();
                if (File.Exists(autosavePath))
                {
                    File.Delete(autosavePath);
                }

                Logger.Log($"SaveProbeToFile: wrote the height map to {newPath}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"SaveProbeToFile: failed - {ex.Message}");
                return false;
            }
        }
    }
}
