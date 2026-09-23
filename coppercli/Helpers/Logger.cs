using System;
using System.IO;

namespace coppercli.Helpers
{
    public static class Logger
    {
        private const string LogFileName = "coppercli.log";

        /// <summary>Replaces the extension of <see cref="LogFileName"/> for the kept copy.</summary>
        private const string PreviousLogExtension = ".previous.log";
        private static readonly object _lock = new object();
        private static string? _logPath;

        public static bool Enabled { get; set; } = false;

        public static string LogFilePath => LogPath;

        /// <summary>Where <see cref="Clear"/> keeps the session before this one.</summary>
        public static string PreviousLogFilePath => Path.ChangeExtension(LogPath, PreviousLogExtension);

        private static string LogPath
        {
            get
            {
                if (_logPath == null)
                {
                    // AppContext.BaseDirectory gives the executable's directory for both
                    // ordinary and single-file builds; Assembly.Location returns an empty
                    // string from a single-file app, which is how releases are published.
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

                    // WriteThrough so a crash cannot lose lines still held in the OS buffer.
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
                // A logging failure must not break the caller.
            }
        }

        public static void Log(string format, params object[] args)
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                Log(string.Format(format, args));
            }
            catch (Exception)
            {
                // string.Format runs before Log(string) can catch anything, so a placeholder
                // that does not match its arguments would reach the caller. A bare null
                // argument binds to the array itself, so it is not only FormatException.
                Log(format);
            }
        }

        /// <summary>
        /// Start a fresh log and keep the session before it, so a failure the operator
        /// restarts out of still has a log to send.
        /// </summary>
        public static void Clear()
        {
            try
            {
                lock (_lock)
                {
                    if (File.Exists(LogPath))
                    {
                        File.Move(LogPath, PreviousLogFilePath, overwrite: true);
                    }
                }
            }
            catch
            {
                // A failed rotation must not break the caller; the old log stays.
            }
        }
    }
}
