// Extracted from Program.cs - Settings and session persistence

using coppercli.Core.Settings;
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

        public static MachineSettings LoadSettings()
        {
            try
            {
                var path = GetSettingsPath();
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize<MachineSettings>(json) ?? new MachineSettings();
                }
            }
            catch
            {
                // Fall through to return default settings
            }
            return new MachineSettings();
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
                return;
            }

            try
            {
                var path = GetProbeAutoSavePath();
                probePoints.Save(path);
                AppState.Session.ProbeAutoSavePath = path;
                SaveSession();
            }
            catch
            {
                // Silent failure for autosave
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
                SaveSession();
            }
            catch
            {
                // Silent failure
            }
        }
    }
}
