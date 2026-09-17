#nullable enable
using System;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// The one route Core has to a log, because it cannot reference the app project. The host
    /// sets `LogAction` at startup; while it is null every message is discarded.
    /// </summary>
    public static class ControllerLog
    {
        public static Action<string>? LogAction { get; set; }

        public static void Log(string message)
        {
            LogAction?.Invoke(message);
        }

        public static void Log(string format, params object[] args)
        {
            LogAction?.Invoke(string.Format(format, args));
        }
    }
}
