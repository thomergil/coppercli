// Shared application state accessible to all menus

using coppercli.Core.Communication;
using coppercli.Core.Controllers;
using coppercli.Core.GCode;
using coppercli.Core.Settings;
using coppercli.Core.Util;
using coppercli.Helpers;
using System.Text.Json;
using HelperToolSetterConfig = coppercli.Helpers.ToolSetterConfig;
using ControllerToolSetterConfig = coppercli.Core.Controllers.ToolSetterConfig;

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

        // Controllers (singletons - created after Machine is initialized)
        private static MillingController? _millingController;
        public static MillingController Milling =>
            _millingController ??= new MillingController(Machine);

        private static ToolChangeController? _toolChangeController;
        public static ToolChangeController ToolChange =>
            _toolChangeController ??= new ToolChangeController(
                Machine,
                MachineProfiles.HasToolSetter,
                MachineProfiles.GetToolSetterPosition,
                () => ConvertToolSetterConfig(MachineProfiles.GetToolSetterConfig()));

        private static ProbeController? _probeController;
        public static ProbeController Probe =>
            _probeController ??= new ProbeController(Machine);

        /// <summary>
        /// Reset all controllers so they get recreated with the current Machine.
        /// Call this when Machine is replaced (e.g., after reconnect).
        /// </summary>
        public static void ResetControllers()
        {
            _millingController = null;
            _toolChangeController = null;
            _probeController = null;
            ActiveController = null;
        }

        // Active controller tracking (only one can run at a time)
        public static IController? ActiveController { get; set; }

        // Loaded files
        public static GCodeFile? CurrentFile { get; set; }
        public static ProbeGrid? ProbePoints { get; set; }

        // State flags
        // AreProbePointsApplied has private setter - only ApplyProbeData() can set it to true.
        // LoadGCodeIntoMachine() always resets it to false.
        public static bool AreProbePointsApplied { get; private set; } = false;
        public static bool IsWorkZeroSet { get; set; } = false;
        public static bool Probing { get; set; } = false;
        public static bool SuppressErrors { get; set; } = false;
        public static bool SingleProbing { get; set; } = false;
        public static bool MacroMode { get; set; } = false;
        public static Action<Vector3, bool>? SingleProbeCallback { get; set; }

        // Depth adjustment for re-milling (negative = deeper, positive = shallower)
        // Use the helper methods below to modify this value.
        public static double DepthAdjustment { get; private set; } = 0;

        /// <summary>
        /// Adjust depth to cut deeper (subtract increment, clamp to -max).
        /// </summary>
        public static void AdjustDepthDeeper()
        {
            DepthAdjustment = Math.Max(DepthAdjustment - CliConstants.DepthAdjustmentIncrement, -CliConstants.DepthAdjustmentMax);
        }

        /// <summary>
        /// Adjust depth to cut shallower (add increment, clamp to +max).
        /// </summary>
        public static void AdjustDepthShallower()
        {
            DepthAdjustment = Math.Min(DepthAdjustment + CliConstants.DepthAdjustmentIncrement, CliConstants.DepthAdjustmentMax);
        }

        /// <summary>
        /// Set depth adjustment to a specific value (clamped to valid range).
        /// </summary>
        public static void SetDepthAdjustment(double value)
        {
            DepthAdjustment = Math.Clamp(value, -CliConstants.DepthAdjustmentMax, CliConstants.DepthAdjustmentMax);
        }

        /// <summary>
        /// Reset depth adjustment to zero.
        /// </summary>
        public static void ResetDepthAdjustment()
        {
            DepthAdjustment = 0;
        }

        // Jog state
        public static int JogPresetIndex { get; set; } = 1;  // Start at Normal

        /// <summary>
        /// Loads G-code into the machine and resets probe application state.
        /// This is the ONLY way to load G-code into the machine (except ApplyProbeData).
        /// Ensures AreProbePointsApplied is always reset when new G-code is loaded.
        /// </summary>
        public static void LoadGCodeIntoMachine(GCodeFile file)
        {
            CurrentFile = file;
            Machine?.SetFile(file.GetGCode());
            AreProbePointsApplied = false;
            ResetDepthAdjustment();
            Logger.Log($"LoadGCodeIntoMachine: loaded {file.FileName}, AreProbePointsApplied=false");
        }

        /// <summary>
        /// Resets probe application state. Called when probe grid changes.
        /// </summary>
        public static void ResetProbeApplicationState()
        {
            Logger.Log($"ResetProbeApplicationState: was {AreProbePointsApplied}, setting to false");
            AreProbePointsApplied = false;
            ResetDepthAdjustment();
        }

        /// <summary>
        /// Sets up a new probe grid. Single source of truth for both TUI and Web UI.
        /// Creates in-memory grid and clears any stale autosave.
        /// Autosave is NOT created here - it's created when first probe point is recorded.
        /// </summary>
        /// <param name="fileMin">G-code file minimum bounds.</param>
        /// <param name="fileMax">G-code file maximum bounds.</param>
        /// <param name="margin">Margin to add around file bounds.</param>
        /// <param name="gridSize">Grid cell size.</param>
        /// <returns>The created ProbeGrid.</returns>
        public static ProbeGrid SetupProbeGrid(Vector2 fileMin, Vector2 fileMax, double margin, double gridSize)
        {
            var min = new Vector2(fileMin.X - margin, fileMin.Y - margin);
            var max = new Vector2(fileMax.X + margin, fileMax.Y + margin);

            var grid = new ProbeGrid(gridSize, min, max);
            ProbePoints = grid;
            ResetProbeApplicationState();

            // Clear any stale autosave - new grid starts fresh
            // Autosave is created when probing starts, not at setup
            Persistence.ClearProbeAutoSave();

            Logger.Log($"SetupProbeGrid: {grid.SizeX}x{grid.SizeY} = {grid.TotalPoints} points");
            return grid;
        }

        /// <summary>
        /// Applies probe data to the current G-code file.
        /// Returns true on success, false if preconditions not met.
        /// </summary>
        public static bool ApplyProbeData()
        {
            Logger.Log($"ApplyProbeData: CurrentFile={CurrentFile != null}, ProbePoints={ProbePoints != null}, NotProbed={ProbePoints?.NotProbed.Count ?? -1}, AreProbePointsApplied={AreProbePointsApplied}");

            if (CurrentFile == null || ProbePoints == null || ProbePoints.NotProbed.Count > 0)
            {
                Logger.Log("ApplyProbeData: preconditions not met, returning false");
                return false;
            }

            // Already applied - don't double-apply
            if (AreProbePointsApplied)
            {
                Logger.Log("ApplyProbeData: already applied, returning true");
                return true;
            }

            CurrentFile = CurrentFile.ApplyProbeGrid(ProbePoints);
            Machine.SetFile(CurrentFile.GetGCode());
            AreProbePointsApplied = true;
            Logger.Log("ApplyProbeData: applied successfully, AreProbePointsApplied=true");
            return true;
        }

        /// <summary>
        /// Convert from Helpers.ToolSetterConfig to Controllers.ToolSetterConfig.
        /// Returns null if input is null.
        /// </summary>
        private static ControllerToolSetterConfig? ConvertToolSetterConfig(HelperToolSetterConfig? config)
        {
            if (config == null)
            {
                return null;
            }

            return new ControllerToolSetterConfig
            {
                X = config.X,
                Y = config.Y,
                ProbeDepth = config.ProbeDepth,
                FastFeed = config.FastFeed,
                SlowFeed = config.SlowFeed,
                Retract = config.Retract
            };
        }

        /// <summary>
        /// Ensures probe data is loaded into memory if autosave exists.
        /// Single source of truth for both TUI and Web UI.
        /// Call this when you need to access ProbePoints and want to ensure
        /// any persisted data is loaded.
        /// Also loads the associated G-Code file if available.
        /// </summary>
        public static void EnsureProbeDataLoaded()
        {
            // Already have probe data in memory
            if (ProbePoints != null)
            {
                return;
            }

            var state = Persistence.GetProbeState();
            if (state == Persistence.ProbeState.None)
            {
                return;
            }

            // Load from autosave
            var autosavePath = Persistence.GetProbeAutoSavePath();
            try
            {
                ProbePoints = ProbeGrid.Load(autosavePath);
                ResetProbeApplicationState();
                Logger.Log($"EnsureProbeDataLoaded: loaded from autosave ({state})");

                // Also load the G-Code file that was used when probe was created
                LoadProbeSourceGCode();
            }
            catch (Exception ex)
            {
                Logger.Log($"EnsureProbeDataLoaded: failed - {ex.Message}");
            }
        }

        /// <summary>
        /// Force loads probe data from autosave, replacing any in-memory data.
        /// Used for explicit "Recover from Autosave" action in TUI and Web UI.
        /// Returns the loaded ProbeGrid, or throws if autosave doesn't exist or load fails.
        /// </summary>
        public static ProbeGrid ForceLoadProbeFromAutosave()
        {
            var autosavePath = Persistence.GetProbeAutoSavePath();
            ProbePoints = ProbeGrid.Load(autosavePath);
            ResetProbeApplicationState();
            LoadProbeSourceGCode();
            Logger.Log($"ForceLoadProbeFromAutosave: loaded {ProbePoints.Progress}/{ProbePoints.TotalPoints} points");
            return ProbePoints;
        }

        /// <summary>
        /// Checks if probe data exists but the source G-Code file is missing.
        /// Used to show warnings in TUI and Web UI.
        /// </summary>
        public static bool IsProbeSourceGCodeMissing =>
            ProbePoints != null &&
            CurrentFile == null &&
            !string.IsNullOrEmpty(Session.ProbeSourceGCodeFile) &&
            !File.Exists(Session.ProbeSourceGCodeFile);

        /// <summary>
        /// Loads the G-Code file associated with current probe data (if available).
        /// Called after loading probe data to ensure both are loaded together.
        /// Returns true if G-Code was loaded, false if not available or missing.
        /// </summary>
        public static bool LoadProbeSourceGCode()
        {
            if (CurrentFile != null)
            {
                return true; // Already have a file loaded
            }

            if (string.IsNullOrEmpty(Session.ProbeSourceGCodeFile))
            {
                return false; // No source file tracked
            }

            if (!File.Exists(Session.ProbeSourceGCodeFile))
            {
                Logger.Log($"LoadProbeSourceGCode: G-Code file missing: {Session.ProbeSourceGCodeFile}");
                return false;
            }

            try
            {
                var file = GCodeFile.Load(Session.ProbeSourceGCodeFile);
                LoadGCodeIntoMachine(file);
                Logger.Log($"LoadProbeSourceGCode: loaded G-Code from {Session.ProbeSourceGCodeFile}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"LoadProbeSourceGCode: failed - {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Handles probe grid state after work zero changes.
        /// Call this after setting work zero to ensure probe data remains valid.
        /// - XY zero: discards probe data (grid XY coordinates become invalid)
        /// - Z-only zero: reloads original G-code and re-applies probe grid
        ///   (grid values are relative to Z0, so must be re-applied with new Z0)
        /// </summary>
        /// <param name="axes">The axes string passed to ZeroWorkOffset (e.g., "X0 Y0 Z0" or "Z0").</param>
        public static void HandleWorkZeroChange(string axes)
        {
            var axesUpper = axes.ToUpperInvariant();
            bool zeroingXY = axesUpper.Contains("X") || axesUpper.Contains("Y");

            if (zeroingXY)
            {
                // XY change invalidates probe grid coordinates
                DiscardProbeData();
                Persistence.ClearProbeAutoSave();
                Logger.Log("HandleWorkZeroChange: X/Y zeroed, probe data discarded");
            }
            else if (AreProbePointsApplied && ProbePoints != null)
            {
                // Z-only change: re-apply probe grid to fresh G-code
                // The grid values were baked into the G-code with the old Z0 reference.
                // We need to reload the original G-code and re-apply with the new Z0.
                ReapplyProbeGrid();
                Logger.Log("HandleWorkZeroChange: Z-only zero, probe grid re-applied");
            }
            else
            {
                Logger.Log("HandleWorkZeroChange: Z-only zero, no probe grid to re-apply");
            }
        }

        /// <summary>
        /// Reloads original G-code and re-applies the probe grid.
        /// Used when Z0 changes and probe grid was already applied.
        /// </summary>
        private static void ReapplyProbeGrid()
        {
            if (ProbePoints == null || string.IsNullOrEmpty(Session.LastLoadedGCodeFile))
            {
                Logger.Log("ReapplyProbeGrid: no probe points or no source file, skipping");
                return;
            }

            if (!File.Exists(Session.LastLoadedGCodeFile))
            {
                Logger.Log($"ReapplyProbeGrid: source file missing: {Session.LastLoadedGCodeFile}");
                return;
            }

            try
            {
                // Reload original G-code (resets AreProbePointsApplied to false)
                var file = GCodeFile.Load(Session.LastLoadedGCodeFile);
                LoadGCodeIntoMachine(file);

                // Re-apply probe grid with new Z0 reference
                ApplyProbeData();
                Logger.Log($"ReapplyProbeGrid: reloaded and re-applied probe grid");
            }
            catch (Exception ex)
            {
                Logger.Log($"ReapplyProbeGrid: failed - {ex.Message}");
            }
        }

        /// <summary>
        /// Discards probe data and reloads the G-code file if probe data was applied.
        /// Called when XY work zero changes since probe grid XY coordinates become invalid.
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
                    var file = GCodeFile.Load(Session.LastLoadedGCodeFile);
                    LoadGCodeIntoMachine(file);
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
