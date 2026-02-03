#nullable enable
using System;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Simple logging abstraction for controllers.
    /// Set LogAction at app startup to route to your logging system.
    /// </summary>
    public static class ControllerLog
    {
        /// <summary>
        /// Action to invoke for logging. Set this at application startup.
        /// If null, logs are discarded.
        /// </summary>
        public static Action<string>? LogAction { get; set; }

        /// <summary>Log a message.</summary>
        public static void Log(string message)
        {
            LogAction?.Invoke(message);
        }

        /// <summary>Log a formatted message.</summary>
        public static void Log(string format, params object[] args)
        {
            LogAction?.Invoke(string.Format(format, args));
        }
    }
}
