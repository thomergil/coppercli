// Shared application state accessible to all menus

using coppercli.Core.Communication;
using coppercli.Core.GCode;
using coppercli.Core.Settings;
using coppercli.Core.Util;
using System.Text.Json;

namespace coppercli
{
    /// <summary>
    /// Shared application state accessible to all menus.
    /// This class holds the machine connection, settings, session state, and loaded files.
    /// </summary>
    internal static class AppState
    {
        // JSON serialization options (shared)
        public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        // Machine and settings
        public static Machine Machine { get; set; } = null!;
        public static MachineSettings Settings { get; set; } = null!;
        public static SessionState Session { get; set; } = null!;

        // Loaded files
        public static GCodeFile? CurrentFile { get; set; }
        public static ProbeGrid? ProbePoints { get; set; }

        // State flags
        public static bool AreProbePointsApplied { get; set; } = false;
        public static bool IsWorkZeroSet { get; set; } = false;
        public static bool Probing { get; set; } = false;
        public static bool SuppressErrors { get; set; } = false;
        public static bool SingleProbing { get; set; } = false;
        public static Action<Vector3, bool>? SingleProbeCallback { get; set; }

        // Jog state
        public static int JogPresetIndex { get; set; } = 1;  // Start at Normal
    }
}
