// Extracted from Program.cs - Settings and session persistence

using coppercli.Core.GCode;
using coppercli.Core.Settings;
using coppercli.Core.Util;
using coppercli.Helpers;
using Spectre.Console;
using System.Text.Json;
using static coppercli.CliConstants;

namespace coppercli
{
    /// <summary>
    /// Handles loading and saving of settings and session state.
    /// </summary>
    internal static class Persistence
    {
        /// <summary>
        /// Gets the application data directory, creating it if it doesn't exist.
        /// </summary>
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
        /// The autosave, parsed, or null when there is none or it cannot be read. A fresh
        /// object each time: callers adopt it as the live grid and probe into it, so a shared
        /// instance would make this method describe memory rather than the file.
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

        // =====================================================================
        // Settings migrations
        //
        // When a settings property is renamed in MachineSettings, add an entry
        // here so existing users' values are preserved on upgrade. Migrations
        // run once on load and rewrite the settings file with the new names.
        //
        // Format: (OldJsonPropertyName, NewJsonPropertyName)
        //
        // Migration history:
        //   v0.4.0  OutlineTraverseHeight → OutlineTraceHeight
        //   v0.4.0  OutlineTraverseFeed   → OutlineTraceFeed
        // =====================================================================
        private static readonly (string Old, string New)[] SettingsMigrations =
        {
            ("OutlineTraverseHeight", "OutlineTraceHeight"),
            ("OutlineTraverseFeed", "OutlineTraceFeed"),
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
                    return JsonSerializer.Deserialize<MachineSettings>(json) ?? new MachineSettings();
                }
            }
            catch (Exception ex)
            {
                QuarantineUnreadableFile(GetSettingsPath(), ex);
            }
            return new MachineSettings();
        }

        /// <summary>
        /// Applies settings migrations by renaming old JSON property names to their
        /// current names. This preserves user-configured values across upgrades when
        /// properties are renamed in MachineSettings. Only migrates when the old name
        /// exists and the new name doesn't (safe to run repeatedly). Rewrites the
        /// settings file after migration so it only runs once.
        /// </summary>
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
                    // Non-fatal: will re-migrate on next load
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
                // The user believes their settings were kept; say so if they were not.
                Logger.Log("SaveSettings failed: {0}", ex.Message);
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
                QuarantineUnreadableFile(GetSessionPath(), ex);
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
        private static void QuarantineUnreadableFile(string path, Exception ex)
        {
            Logger.Log("Could not read {0} ({1}); using defaults", path, ex.Message);

            // Said out loud, not only to a log that is off unless --debug: the operator
            // is about to run with default probe feeds, depths and tool-setter position
            // instead of their own.
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

                // Remember which G-Code file was loaded when this probe was created
                // This allows recovering the G-Code along with the probe data
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

        /// <returns>False if the file is still there, so no caller tells the operator the
        /// data is gone while it waits on disk to be offered again.</returns>
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
        /// Saves probe data by moving the autosave file to the user's chosen location.
        /// This is the only way to "save" probe data in the simplified model.
        /// After saving, the autosave is deleted and state returns to None.
        /// </summary>
        public static bool SaveProbeToFile(string newPath)
        {
            var autosavePath = GetProbeAutoSavePath();
            if (!File.Exists(autosavePath))
            {
                Logger.Log("SaveProbeToFile: no autosave file exists");
                return false;
            }

            try
            {
                // Ensure target directory exists
                var targetDir = Path.GetDirectoryName(newPath);
                if (!string.IsNullOrEmpty(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                // Move the autosave to user's location
                File.Move(autosavePath, newPath, overwrite: true);

                // Clear session autosave path (file no longer exists there)
                SaveSession();

                Logger.Log($"SaveProbeToFile: moved {autosavePath} to {newPath}");
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
