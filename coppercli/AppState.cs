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
        public static bool IsHomed { get; set; } = false;
        public static bool AreProbePointsApplied { get; set; } = false;
        public static bool IsWorkZeroSet { get; set; } = false;
        public static bool Probing { get; set; } = false;
        public static bool SuppressErrors { get; set; } = false;
        public static bool SingleProbing { get; set; } = false;
        public static bool MacroMode { get; set; } = false;
        public static Action<Vector3, bool>? SingleProbeCallback { get; set; }

        // Depth adjustment for re-milling (positive = deeper)
        public static double DepthAdjustment { get; set; } = 0;

        // Jog state
        public static int JogPresetIndex { get; set; } = 1;  // Start at Normal

        /// <summary>
        /// Starts probing mode. Sets state and calls machine.ProbeStart().
        /// </summary>
        public static void StartProbing()
        {
            Probing = true;
            Machine.ProbeStart();
        }

        /// <summary>
        /// Stops probing mode. Sets state and calls machine.ProbeStop().
        /// </summary>
        public static void StopProbing()
        {
            Probing = false;
            Machine.ProbeStop();
        }

        /// <summary>
        /// Resets probe application state. Called when G-code or probe grid changes.
        /// </summary>
        public static void ResetProbeApplicationState()
        {
            AreProbePointsApplied = false;
            DepthAdjustment = 0;
        }

        /// <summary>
        /// Applies probe data to the current G-code file.
        /// Returns true on success, false if preconditions not met.
        /// </summary>
        public static bool ApplyProbeData()
        {
            if (CurrentFile == null || ProbePoints == null || ProbePoints.NotProbed.Count > 0)
            {
                return false;
            }

            CurrentFile = CurrentFile.ApplyProbeGrid(ProbePoints);
            Machine.SetFile(CurrentFile.GetGCode());
            AreProbePointsApplied = true;
            return true;
        }

        /// <summary>
        /// Discards probe data and reloads the G-code file if probe data was applied.
        /// Called when work zero changes (Zero XYZ, Zero Z) since probe data is now invalid.
        /// Does nothing if no probe data exists.
        /// </summary>
        public static void DiscardProbeData()
        {
            // Nothing to discard
            if (ProbePoints == null && !AreProbePointsApplied)
            {
                return;
            }

            bool wasApplied = AreProbePointsApplied;
            ProbePoints = null;
            ResetProbeApplicationState();

            // If probe data was applied to the G-code, reload the original file
            if (wasApplied && !string.IsNullOrEmpty(Session.LastLoadedGCodeFile) &&
                File.Exists(Session.LastLoadedGCodeFile))
            {
                try
                {
                    CurrentFile = GCodeFile.Load(Session.LastLoadedGCodeFile);
                    Machine.SetFile(CurrentFile.GetGCode());
                    Helpers.Logger.Log("DiscardProbeData: Reloaded {0}", Session.LastLoadedGCodeFile);
                }
                catch (Exception ex)
                {
                    Helpers.Logger.Log("DiscardProbeData: Failed to reload file: {0}", ex.Message);
                }
            }
        }
    }
}
