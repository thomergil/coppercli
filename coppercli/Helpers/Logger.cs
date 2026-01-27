using System;
using System.IO;
using System.Reflection;

namespace coppercli.Helpers
{
    /// <summary>
    /// Simple file logger for debugging.
    /// </summary>
    public static class Logger
    {
        private const string LogFileName = "coppercli.log";
        private static readonly object _lock = new object();
        private static string? _logPath;

        public static bool Enabled { get; set; } = false;

        /// <summary>
        /// Returns the full path to the log file.
        /// </summary>
        public static string LogFilePath => LogPath;

        private static string LogPath
        {
            get
            {
                if (_logPath == null)
                {
                    // Use the directory where the executable is located
                    var exePath = Assembly.GetExecutingAssembly().Location;
                    var exeDir = Path.GetDirectoryName(exePath);
                    // Fall back to current directory if we can't get exe path (e.g., single-file publish)
                    if (string.IsNullOrEmpty(exeDir))
                    {
                        exeDir = AppContext.BaseDirectory;
                    }
                    _logPath = Path.Combine(exeDir!, LogFileName);
                }
                return _logPath;
            }
        }

        public static void Log(string message)
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                lock (_lock)
                {
                    var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    var line = $"[{timestamp}] {message}";
                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }
            }
            catch
            {
                // Silently ignore logging failures
            }
        }

        public static void Log(string format, params object[] args)
        {
            Log(string.Format(format, args));
        }

        public static void Clear()
        {
            try
            {
                if (File.Exists(LogPath))
                {
                    File.Delete(LogPath);
                }
            }
            catch
            {
                // Silently ignore
            }
        }
    }
}
