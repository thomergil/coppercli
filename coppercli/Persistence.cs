// Extracted from Program.cs - Settings and session persistence

using coppercli.Core.GCode;
using coppercli.Core.Settings;
using coppercli.Helpers;
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
        /// Probe data states. Single source of truth for both TUI and Web UI.
        /// </summary>
        public enum ProbeState
        {
            /// <summary>No autosave file exists.</summary>
            None,
            /// <summary>Autosave exists with incomplete data (NotProbed.Count > 0).</summary>
            Partial,
            /// <summary>Autosave exists with complete data (NotProbed.Count == 0).</summary>
            Complete
        }

        /// <summary>
        /// Gets the current probe state by checking the autosave file.
        /// This is the single source of truth for both TUI and Web UI.
        /// </summary>
        public static ProbeState GetProbeState()
        {
            var path = GetProbeAutoSavePath();
            if (!File.Exists(path))
            {
                Logger.Log("GetProbeState: no autosave file at {0}", path);
                return ProbeState.None;
            }

            try
            {
                var grid = ProbeGrid.Load(path);
                var state = grid.NotProbed.Count > 0 ? ProbeState.Partial : ProbeState.Complete;
                Logger.Log("GetProbeState: {0} at {1} ({2}/{3} probed, NotProbed.Count={4})",
                    state, path, grid.Progress, grid.TotalPoints, grid.NotProbed.Count);
                return state;
            }
            catch (Exception ex)
            {
                // Corrupted file - treat as none
                Logger.Log("GetProbeState: failed to load {0} - {1}", path, ex.Message);
                return ProbeState.None;
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
            catch
            {
                // Fall through to return default settings
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
                    File.WriteAllText(GetSettingsPath(), json);
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
                File.WriteAllText(path, json);
            }
            catch
            {
                // Silent failure for settings save
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
            catch
            {
                // Fall through to return default session
            }
            return new SessionState();
        }

        public static void SaveSession()
        {
            try
            {
                var path = GetSessionPath();
                var json = JsonSerializer.Serialize(AppState.Session, AppState.JsonOptions);
                File.WriteAllText(path, json);
            }
            catch
            {
                // Silent failure for session save
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
                AppState.Session.ProbeAutoSavePath = path;

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

        public static void ClearProbeAutoSave()
        {
            try
            {
                var path = GetProbeAutoSavePath();
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                AppState.Session.ProbeAutoSavePath = null;
                AppState.Session.ProbeSourceGCodeFile = null;
                SaveSession();
            }
            catch
            {
                // Silent failure
            }
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
                AppState.Session.ProbeAutoSavePath = null;
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
