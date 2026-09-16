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
        private static Machine _machine = null!;

        /// <summary>
        /// The active machine connection. Assigning a new machine rewires the connection-state
        /// subscription (below) so work-origin invalidation follows the machine that is live.
        /// </summary>
        public static Machine Machine
        {
            get => _machine;
            set
            {
                if (_machine != null)
                {
                    _machine.ConnectionStateChanged -= OnMachineConnectionStateChanged;
                }
                _machine = value;
                if (_machine != null)
                {
                    _machine.ConnectionStateChanged += OnMachineConnectionStateChanged;
                }
            }
        }

        /// <summary>
        /// A disconnect means the operator's asserted work origin can no longer be trusted: the
        /// machine may be repositioned or power-cycled before it returns. Invalidate the zero
        /// centrally here — mirroring how Core clears <c>IsHomed</c> inside <c>Machine.Disconnect</c> —
        /// so every disconnect path behaves identically.
        /// </summary>
        private static void OnMachineConnectionStateChanged()
        {
            if (_machine != null && !_machine.Connected)
            {
                ForgetWorkZero();
            }
        }

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
        }

        // Active controller tracking (only one can run at a time)

        // Loaded files
        public static GCodeFile? CurrentFile { get; set; }
        public static ProbeGrid? ProbePoints { get; set; }

        // State flags
        // AreProbePointsApplied has private setter - only ApplyProbeData() can set it to true.
        // LoadGCodeIntoMachine() always resets it to false.
        public static bool AreProbePointsApplied { get; private set; } = false;
        /// <summary>
        /// Whether the work origin is known. Written only through the three methods below,
        /// so every way it changes is named in one place.
        /// </summary>
        public static bool IsWorkZeroSet { get; private set; } = false;

        /// <summary>The machine was just zeroed, so the origin is known.</summary>
        public static void WorkZeroWasSet() => SetWorkZeroKnown(true, "zeroed on the machine");

        /// <summary>
        /// The operator vouched for an origin nobody just set: a zero remembered from the
        /// last session, or one GRBL keeps across a reconnect to the same machine.
        /// </summary>
        public static void TrustWorkZero(bool trusted) =>
            SetWorkZeroKnown(trusted, "trusted by the operator");

        /// <summary>The origin is no longer known, because the machine may have moved.</summary>
        public static void ForgetWorkZero() => SetWorkZeroKnown(false, "machine disconnected");

        private static void SetWorkZeroKnown(bool known, string why)
        {
            if (IsWorkZeroSet == known)
            {
                return;
            }

            IsWorkZeroSet = known;
            Logger.Log("AppState: IsWorkZeroSet = {0} ({1})", known, why);
        }
        /// <summary>
        /// Whether a grid probe is running, derived from the probe controller that owns
        /// that state, so no front end keeps a flag it could forget to clear.
        /// </summary>
        public static bool IsProbing => _probeController?.IsActive ?? false;

        /// <inheritdoc cref="Core.Controllers.IProbeController.IsTracingOutline"/>
        public static bool IsTracingOutline => _probeController?.IsTracingOutline ?? false;

        /// <inheritdoc cref="Core.Controllers.IProbeController.IsMeasuringGrid"/>
        public static bool IsMeasuringGrid => _probeController?.IsMeasuringGrid ?? false;
        public static bool SuppressErrors { get; set; } = false;
        public static bool MacroMode { get; set; } = false;

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
        /// <summary>
        /// Which jog preset is selected. Advance it with <see cref="CycleJogPreset"/> and
        /// read the preset itself from <see cref="CurrentJogMode"/>, so how the index wraps
        /// and what it points at are each stated once rather than at every menu that offers
        /// the key.
        /// </summary>
        public static int JogPresetIndex { get; private set; } = CliConstants.DefaultJogModeIndex;

        /// <summary>The preset the index selects.</summary>
        public static CliConstants.JogMode CurrentJogMode => CliConstants.JogModes[JogPresetIndex];

        /// <summary>Move to the next preset, wrapping at the end.</summary>
        public static void CycleJogPreset()
        {
            JogPresetIndex = (JogPresetIndex + 1) % CliConstants.JogModes.Length;
        }

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

            // Record which board is loaded here, not at each of the callers - a height
            // map's applicability is decided by comparing against this, and a caller
            // that does not set it makes every later answer wrong.
            if (!string.IsNullOrEmpty(file.FilePath))
            {
                Session.LastLoadedGCodeFile = file.FilePath;
            }

            // And decide here what that means for any height map in hand, so every entry
            // point - menu, web, macro, session restore - behaves the same way.
            LastDiscardedProbeReason = DiscardInapplicableProbeData();

            Logger.Log($"LoadGCodeIntoMachine: loaded {file.FileName}, AreProbePointsApplied=false");
        }

        /// <summary>
        /// Loads a probe grid from a file, replacing any current grid. If a grid was already
        /// baked into the in-memory G-code, the original is reloaded first: ApplyProbeGrid is
        /// additive (Z += interpolated height), so applying a second grid without restoring the
        /// original would double the corrections and cut at the wrong depth. One definition
        /// for both the terminal and web "load probe file" paths, so each reloads first.
        /// </summary>
        public static ProbeGrid LoadProbeGridFromFile(string path)
        {
            if (AreProbePointsApplied && !string.IsNullOrEmpty(Session.LastLoadedGCodeFile) &&
                File.Exists(Session.LastLoadedGCodeFile))
            {
                LoadGCodeIntoMachine(GCodeFile.Load(Session.LastLoadedGCodeFile));
                Logger.Log("LoadProbeGridFromFile: reloaded original G-code before loading new probe grid");
            }
            var grid = ProbeGrid.Load(path);
            ProbePoints = grid;
            ResetProbeApplicationState();
            return grid;
        }

        /// <summary>
        /// Why the height map was dropped by the most recent load, or null if none was.
        /// The UI reports this; the decision itself is made in one place.
        /// </summary>
        internal static string? LastDiscardedProbeReason { get; private set; }

        /// <summary>
        /// Whether the height map in hand describes the job in hand.
        ///
        /// One question, answered by the map itself from the setup recorded on it, so
        /// every screen gives the same answer instead of each inferring one from a
        /// different corner of the session state.
        /// </summary>
        internal static ProbeApplicability GetProbeApplicability()
        {
            var grid = ProbePoints;

            if (grid == null)
            {
                return ProbeApplicability.DifferentFile;
            }

            return grid.GetApplicability(Session.LastLoadedGCodeFile ?? string.Empty,
                                         Machine?.G54Offset ?? Core.Util.Vector3.MinValue);
        }

        /// <summary>
        /// Drops a height map that does not describe the job in hand, so it cannot be
        /// announced or applied to the wrong board or the wrong origin. Returns a phrase
        /// naming why it went, or null if nothing was dropped.
        /// </summary>
        internal static string? DiscardInapplicableProbeData()
        {
            if (ProbePoints == null)
            {
                return null;
            }

            var applicability = GetProbeApplicability();

            if (applicability == ProbeApplicability.Applicable || applicability == ProbeApplicability.Unknown)
            {
                return null;
            }

            string why = applicability == ProbeApplicability.DifferentFile
                ? $"it was measured for {Path.GetFileName(ProbePoints.Context.SourceFile)}"
                : "the work origin has moved since it was measured";

            DiscardProbeData();
            Persistence.ClearProbeAutoSave();
            Logger.Log("DiscardInapplicableProbeData: dropped height map ({0})", applicability);

            return why;
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
            var grid = ProbeGrid.ForJob(fileMin, fileMax, margin, gridSize);

            // Stamped at creation from the machine's reported origin, so the map can later
            // say whether it still describes this job.
            grid.Context = new ProbeContext(
                Session.LastLoadedGCodeFile ?? string.Empty,
                Machine?.G54Offset ?? Core.Util.Vector3.MinValue);

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
            Logger.Log($"ApplyProbeData: CurrentFile={CurrentFile != null}, ProbePoints={ProbePoints != null}, NotProbed={ProbePoints?.RemainingCount ?? -1}, AreProbePointsApplied={AreProbePointsApplied}");

            // Adopt the autosave if that is where the map is. Applying is an operator
            // action, so it may take the data on; a status read may not.
            ProbePoints ??= ReadUsableAutosave();

            if (CurrentFile == null || ProbePoints == null || !ProbePoints.HasCompleteData)
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
        /// Adopts the autosave as the operator's current data when there is none in memory,
        /// along with the G-code it was measured for. Call it before reading ProbePoints on
        /// a path that may run before anything loaded them.
        /// </summary>
        public static void EnsureProbeDataLoaded()
        {
            // Already have probe data in memory
            if (ProbePoints != null)
            {
                return;
            }

            var candidate = ReadUsableAutosave();
            if (candidate == null)
            {
                return;
            }

            ProbePoints = candidate;
            ResetProbeApplicationState();
            Logger.Log("EnsureProbeDataLoaded: adopted the autosave");

            // Also load the G-Code file that was used when probe was created
            LoadProbeSourceGCode();
        }

        /// <summary>
        /// The autosave on disk, if it describes the job in hand. Reads the file and adopts
        /// nothing, so a status can ask without changing what the operator has. A map
        /// measured on another board, or before the origin moved, comes back null: it must
        /// never be announced as the operator's current data.
        /// </summary>
        public static ProbeGrid? ReadUsableAutosave()
        {
            var candidate = Persistence.ReadProbeAutoSave();
            if (candidate == null)
            {
                return null;
            }

            var applicability = candidate.GetApplicability(
                Session.LastLoadedGCodeFile ?? string.Empty,
                Machine?.G54Offset ?? Core.Util.Vector3.MinValue);

            if (applicability == ProbeApplicability.DifferentFile
                || applicability == ProbeApplicability.OriginMoved)
            {
                Logger.Log("ReadUsableAutosave: not applicable ({0})", applicability);
                return null;
            }

            return candidate;
        }

        /// <summary>
        /// The height map for the job in hand: the one loaded, or the autosave when nothing
        /// is loaded and it describes this job. Every gate that asks whether the operator has
        /// probe data reads this, so none of them can answer differently from the screen.
        /// </summary>
        public static ProbeGrid? CurrentProbeGrid => ProbePoints ?? ReadUsableAutosave();

        /// <summary>
        /// Deletes the autosave and drops the map in memory, in that order, so a delete that
        /// fails does not leave the operator told the data is gone while it is still there.
        /// </summary>
        /// <returns>False if the autosave is still on disk.</returns>
        public static bool DiscardProbeDataAndAutosave()
        {
            if (!Persistence.ClearProbeAutoSave())
            {
                return false;
            }

            DiscardProbeData();
            return true;
        }

        /// <summary>
        /// Force loads probe data from autosave, replacing any in-memory data.
        /// Used for explicit "Recover from Autosave" action in TUI and Web UI.
        /// Returns the loaded ProbeGrid, or throws if autosave doesn't exist or load fails.
        /// </summary>
        public static ProbeGrid ForceLoadProbeFromAutosave()
        {
            // The same test the status applies: recovering a map measured for another board
            // would hand the operator someone else's heights as their current data.
            ProbePoints = ReadUsableAutosave()
                ?? throw new InvalidOperationException(CliConstants.ProbeAutosaveNotApplicable);
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
