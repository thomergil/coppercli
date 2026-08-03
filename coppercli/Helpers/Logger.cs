using System;
using System.IO;

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
                    // The log sits next to the executable. AppContext.BaseDirectory gives
                    // that directory for both ordinary and single-file builds, whereas
                    // Assembly.Location returns an empty string from a single-file app -
                    // which is exactly how releases are published.
                    _logPath = Path.Combine(AppContext.BaseDirectory, LogFileName);
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
                    var bytes = System.Text.Encoding.UTF8.GetBytes(line + Environment.NewLine);

                    // Use WriteThrough to bypass OS buffering and write directly to disk.
                    // This ensures logs are available immediately (important for debugging crashes).
                    using var fs = new FileStream(
                        LogPath,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.Read,
                        bufferSize: 4096,
                        FileOptions.WriteThrough);
                    fs.Write(bytes, 0, bytes.Length);
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
