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
    /// <summary>
    /// What <see cref="AppState.LoadGCodeIntoMachine"/> did.
    /// </summary>
    /// <param name="Refused">Why the file was not loaded, or null once it was.</param>
    /// <param name="MapDiscardedBecause">
    /// Why the height map in hand was dropped, or null if it was kept. Returned rather than
    /// stored, because two browser tabs load files on their own threads and a shared field
    /// would hand one load's reason to the other.
    /// </param>
    internal sealed record LoadOutcome(string? Refused, string? MapDiscardedBecause);

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
                ClearWorkZero();
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

        // Loaded files
        public static GCodeFile? CurrentFile { get; set; }
        public static ProbeGrid? ProbePoints { get; private set; }

        // AreProbePointsApplied has a private setter: only ApplyProbeData sets it true, and
        // ResetProbeApplicationState is the one place it goes back to false.
        public static bool AreProbePointsApplied { get; private set; } = false;
        /// <summary>
        /// Whether the work origin is known. Written only through the setters below.
        /// </summary>
        public static bool IsWorkZeroSet { get; private set; } = false;

        /// <summary>The machine was zeroed, so the origin is known.</summary>
        public static void MarkWorkZeroSet() => SetWorkZeroKnown(true, "zeroed on the machine");

        /// <summary>
        /// The operator confirmed an origin nobody set this session: one remembered from
        /// the last session, or one GRBL kept across a reconnect.
        /// </summary>
        public static void SetWorkZeroTrusted(bool trusted) =>
            SetWorkZeroKnown(trusted, "trusted by the operator");

        /// <summary>The origin is no longer known: the machine may have moved.</summary>
        public static void ClearWorkZero() => SetWorkZeroKnown(false, "machine disconnected");

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
        /// Whether a grid probe is running, read from the probe controller, so no front end
        /// keeps a flag of its own.
        /// </summary>
        public static bool IsProbing => _probeController?.IsActive ?? false;

        /// <summary>
        /// Whether any run owns the machine, parked at a prompt or not. Read from the backing
        /// fields, so asking does not create a controller.
        /// </summary>
        public static bool IsRunInProgress =>
            (_millingController?.IsRunInProgress ?? false)
            || (_probeController?.IsRunInProgress ?? false)
            || (_toolChangeController?.IsRunInProgress ?? false);

        /// <summary>Whether this zero moves the X or Y datum.</summary>
        public static bool ZeroTouchesXY(string axes)
        {
            string upper = axes.ToUpperInvariant();
            return upper.Contains('X') || upper.Contains('Y');
        }

        /// <summary>Whether this zero sets all three axes, so it establishes a full origin.</summary>
        public static bool ZeroIsFullOrigin(string axes)
        {
            string upper = axes.ToUpperInvariant();
            return upper.Contains('X') && upper.Contains('Y') && upper.Contains('Z');
        }

        /// <summary>
        /// Why the loaded file and the height map cannot change now, or null. A run streams
        /// from Machine.File with the map baked in, and tracks its place by line number.
        /// </summary>
        public static string? WhyTheFileCannotChange() =>
            IsRunInProgress ? CliConstants.ErrorFileChangeDuringRun : null;

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
        /// read the preset from <see cref="CurrentJogMode"/>, so the wrap-around and the
        /// lookup are each defined once.
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
        /// The one path that loads a file into the machine. ApplyProbeData rewrites the same
        /// file in place once a map is baked in; nothing else touches Machine.SetFile.
        ///
        /// Refused while a run is in progress: a run tracks its place in Machine.File by
        /// line number, and a new file resets that to the start.
        /// </summary>
        /// <returns>What the load did: see <see cref="LoadOutcome"/>.</returns>
        public static LoadOutcome LoadGCodeIntoMachine(GCodeFile file)
        {
            string? blocked = WhyTheFileCannotChange();
            if (blocked != null)
            {
                Logger.Log("LoadGCodeIntoMachine: {0}", blocked);
                return new LoadOutcome(blocked, null);
            }

            CurrentFile = file;
            Machine?.SetFile(file.GetGCode());
            ResetProbeApplicationState();

            // Record which board is loaded here, not at each of the callers - a height
            // map's applicability is decided by comparing against this, and a caller
            // that does not set it makes every later check wrong.
            if (!string.IsNullOrEmpty(file.FilePath))
            {
                Session.LastLoadedGCodeFile = file.FilePath;
            }

            // And decide here what that means for any height map in hand, so every entry
            // point - menu, web, macro, session restore - behaves the same way.
            string? mapDiscardedBecause = DiscardInapplicableProbeData();

            Logger.Log($"LoadGCodeIntoMachine: loaded {file.FileName}, AreProbePointsApplied=false");

            return new LoadOutcome(null, mapDiscardedBecause);
        }

        /// <summary>
        /// Loads a probe grid from a file, replacing any current grid. If a grid was already
        /// applied to the in-memory G-code, the original is reloaded first: ApplyProbeGrid
        /// adds to Z, so a second grid on top would double the corrections.
        /// </summary>
        /// <returns>
        /// The grid, or null with the reason it was refused. Refused before anything changes:
        /// without the reload, the next apply doubles the corrections.
        /// </returns>
        public static (ProbeGrid? Grid, string? Refused) LoadProbeGridFromFile(string path)
        {
            if (AreProbePointsApplied && !string.IsNullOrEmpty(Session.LastLoadedGCodeFile) &&
                File.Exists(Session.LastLoadedGCodeFile))
            {
                string? refused = LoadGCodeIntoMachine(GCodeFile.Load(Session.LastLoadedGCodeFile)).Refused;
                if (refused != null)
                {
                    return (null, refused);
                }

                Logger.Log("LoadProbeGridFromFile: reloaded original G-code before loading new probe grid");
            }

            var grid = ProbeGrid.Load(path);
            string? notAdopted = AdoptProbeGrid(grid);
            if (notAdopted != null)
            {
                return (null, notAdopted);
            }

            // A grid from a file is already saved, so anything in the autosave belongs to an
            // earlier one and would otherwise be offered as unsaved work.
            Persistence.ClearProbeAutoSave();
            return (grid, null);
        }

        /// <summary>
        /// Whether the height map in hand describes the job in hand.
        ///
        /// Decided by the map itself, from the setup recorded on it, so every screen gets
        /// the same result instead of inferring one from the session state.
        /// </summary>
        internal static ProbeApplicability GetProbeApplicability() =>
            DescribeApplicability(ProbePoints);

        /// <summary>
        /// The current setup: the file loaded and the origin the machine reports. A map is
        /// stamped with this when measured and compared against it afterwards.
        /// </summary>
        internal static ProbeContext CurrentSetup => new(
            Session.LastLoadedGCodeFile ?? string.Empty,
            Machine?.G54Offset ?? Core.Util.Vector3.MinValue);

        /// <summary>
        /// Whether <paramref name="grid"/> matches the current file and origin. No map
        /// counts as not matching.
        /// </summary>
        internal static ProbeApplicability DescribeApplicability(ProbeGrid? grid) =>
            grid == null
                ? ProbeApplicability.DifferentFile
                : grid.GetApplicability(CurrentSetup.SourceFile, CurrentSetup.WorkOrigin);

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

            if (applicability.IsUsable())
            {
                return null;
            }

            string why = GetInapplicableReason(applicability, ProbePoints.Context.SourceFile);

            DiscardProbeData();

            Persistence.ClearProbeAutoSave();
            Logger.Log("DiscardInapplicableProbeData: dropped height map ({0})", applicability);

            return why;
        }

        /// <summary>
        /// Why a height map does not describe the job in hand, as a phrase that finishes a
        /// sentence about it. Every screen that has to explain a dropped or refused map reads
        /// this, so none of them explains it differently.
        /// </summary>
        internal static string GetInapplicableReason(ProbeApplicability applicability, string measuredFor) =>
            applicability == ProbeApplicability.DifferentFile
                ? $"it was measured for {Path.GetFileName(measuredFor)}"
                : "the work origin has moved since it was measured";

        /// <summary>
        /// The one place the height map in hand changes. Swapping the map clears the applied
        /// flag, so a run streaming corrections would be left with AppState saying there are
        /// none, and the next apply would double them.
        /// </summary>
        /// <param name="grid">The new map, or null to have none.</param>
        /// <returns>Why the map was left alone, or null once it was replaced.</returns>
        public static string? AdoptProbeGrid(ProbeGrid? grid)
        {
            string? blocked = WhyTheFileCannotChange();
            if (blocked != null)
            {
                Logger.Log("AdoptProbeGrid: {0}", blocked);
                return blocked;
            }

            ProbePoints = grid;
            ResetProbeApplicationState();
            return null;
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
        /// Builds a new probe grid for the loaded job and takes it as the map in hand.
        /// </summary>
        /// <param name="fileMin">G-code file minimum bounds.</param>
        /// <param name="fileMax">G-code file maximum bounds.</param>
        /// <param name="margin">Margin to add around file bounds.</param>
        /// <param name="gridSize">Grid cell size.</param>
        /// <returns>The new grid, or null with the reason it was refused.</returns>
        public static (ProbeGrid? Grid, string? Refused) SetupProbeGrid(Vector2 fileMin, Vector2 fileMax, double margin, double gridSize)
        {
            var grid = ProbeGrid.ForJob(fileMin, fileMax, margin, gridSize);

            // Stamped at creation from the machine's reported origin, so the map can later
            // say whether it still describes this job.
            grid.Context = CurrentSetup;

            string? notAdopted = AdoptProbeGrid(grid);
            if (notAdopted != null)
            {
                return (null, notAdopted);
            }

            // A new grid starts with no measured points, so anything still on disk belongs
            // to an earlier one. The autosave is written again once probing records a point.
            Persistence.ClearProbeAutoSave();

            Logger.Log($"SetupProbeGrid: {grid.SizeX}x{grid.SizeY} = {grid.TotalPoints} points");
            return (grid, null);
        }

        /// <summary>
        /// Applies probe data to the current G-code file, adopting the autosave as the live
        /// map if none is in memory.
        /// </summary>
        /// <returns>Null once the map is applied, or the reason it was refused.</returns>
        public static string? ApplyProbeData()
        {
            // Applying rewrites Machine.File and resets its line count, so it is refused for
            // the same reason a load is.
            string? blocked = WhyTheFileCannotChange();
            if (blocked != null)
            {
                Logger.Log("ApplyProbeData: {0}", blocked);
                return blocked;
            }

            Logger.Log($"ApplyProbeData: CurrentFile={CurrentFile != null}, ProbePoints={ProbePoints != null}, NotProbed={ProbePoints?.RemainingCount ?? -1}, AreProbePointsApplied={AreProbePointsApplied}");

            // Adopt the autosave if that is where the map is, through the one adopter.
            // Applying is an operator action, so it may take the data on; a status read may
            // not. Assigned directly, this skipped ResetProbeApplicationState.
            if (ProbePoints == null && ReadUsableAutosave() is ProbeGrid autosave)
            {
                string? notAdopted = AdoptProbeGrid(autosave);
                if (notAdopted != null)
                {
                    Logger.Log("ApplyProbeData: {0}", notAdopted);
                    return notAdopted;
                }
            }

            if (CurrentFile == null || ProbePoints == null || !ProbePoints.HasCompleteData)
            {
                Logger.Log("ApplyProbeData: preconditions not met");
                return CliConstants.ErrorNoCompleteMapToApply;
            }

            // Applying twice would double the corrections.
            if (AreProbePointsApplied)
            {
                Logger.Log("ApplyProbeData: already applied");
                return null;
            }

            CurrentFile = CurrentFile.ApplyProbeGrid(ProbePoints);
            Machine.SetFile(CurrentFile.GetGCode());
            AreProbePointsApplied = true;
            Logger.Log("ApplyProbeData: applied successfully, AreProbePointsApplied=true");
            return null;
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
        /// <returns>Why the autosave was left alone, or null if it was adopted or there was none.</returns>
        public static string? EnsureProbeDataLoaded()
        {
            // Already have probe data in memory
            if (ProbePoints != null)
            {
                return null;
            }

            var candidate = ReadUsableAutosave();
            if (candidate == null)
            {
                return null;
            }

            string? notAdopted = AdoptProbeGrid(candidate);
            if (notAdopted != null)
            {
                return notAdopted;
            }

            Logger.Log("EnsureProbeDataLoaded: adopted the autosave");

            // Also load the G-code file that was used when probe was created
            LoadProbeSourceGCode();
            return null;
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

            var applicability = DescribeApplicability(candidate);

            if (!applicability.IsUsable())
            {
                Logger.Log("ReadUsableAutosave: not applicable ({0})", applicability);
                return null;
            }

            return candidate;
        }

        /// <summary>
        /// The height map for the job in hand: the one loaded, or the autosave when nothing
        /// is loaded and it matches this job. Every check for probe data reads this, so none
        /// of them can disagree with the screen.
        /// </summary>
        public static ProbeGrid? CurrentProbeGrid => ReadProbeGridAndAutosave().Grid;

        /// <summary>
        /// The map for this job and the usable autosave behind it, from one read of the
        /// file. The status builds its probe panel and its Save button from both, and two
        /// reads could return different maps.
        /// </summary>
        public static (ProbeGrid? Grid, ProbeGrid? UsableAutosave) ReadProbeGridAndAutosave()
        {
            var usableAutosave = ReadUsableAutosave();
            return (ProbePoints ?? usableAutosave, usableAutosave);
        }

        /// <summary>
        /// Takes the map out of the G-code and drops it, then deletes the saved copy. In that
        /// order, so a saved copy that will not delete is not reported as corrections stuck in
        /// the toolpath.
        /// </summary>
        /// <returns>The reason nothing was discarded, or null once it was.</returns>
        public static string? DiscardProbeDataAndAutosave()
        {
            // Asked before the autosave is deleted, so a refusal leaves both copies where
            // they are.
            string? blocked = WhyTheFileCannotChange();
            if (blocked != null)
            {
                Logger.Log("DiscardProbeDataAndAutosave: {0}", blocked);
                return blocked;
            }

            string? notDiscarded = DiscardProbeData();
            if (notDiscarded != null)
            {
                return notDiscarded;
            }

            return Persistence.ClearProbeAutoSave() ? null : CliConstants.ErrorAutosaveNotDeleted;
        }

        /// <summary>
        /// Takes the autosaved map as the operator's current data, replacing anything in
        /// memory. Behind Recover from Autosave in both front ends.
        /// </summary>
        /// <returns>
        /// The recovered map, or null with the reason: there is no autosave, it does not
        /// describe this job, or a run owns the file.
        /// </returns>
        public static (ProbeGrid? Grid, string? Refused) ForceLoadProbeFromAutosave()
        {
            // Told apart, because "there is none" and "there is one that does not fit" are
            // different things to the operator.
            var candidate = Persistence.ReadProbeAutoSave();
            if (candidate == null)
            {
                return (null, CliConstants.ProbeErrorNoAutosave);
            }

            // The same check the status applies: a map measured for another board must not
            // be recovered as this job's data.
            if (!DescribeApplicability(candidate).IsUsable())
            {
                return (null, CliConstants.ProbeAutosaveNotApplicable);
            }

            string? notAdopted = AdoptProbeGrid(candidate);
            if (notAdopted != null)
            {
                return (null, notAdopted);
            }

            LoadProbeSourceGCode();
            Logger.Log($"ForceLoadProbeFromAutosave: loaded {candidate.Progress}/{candidate.TotalPoints} points");
            return (candidate, null);
        }

        /// <summary>
        /// Checks if probe data exists but the source G-code file is missing.
        /// Used to show warnings in TUI and Web UI.
        /// </summary>
        public static bool IsProbeSourceGCodeMissing =>
            ProbePoints != null &&
            CurrentFile == null &&
            !string.IsNullOrEmpty(Session.ProbeSourceGCodeFile) &&
            !File.Exists(Session.ProbeSourceGCodeFile);

        /// <summary>
        /// Loads the G-code file the current probe data was measured for, so the map and the
        /// job it describes are in hand together.
        /// </summary>
        /// <returns>
        /// True once a file is loaded, including one that was already loaded. False when
        /// none is recorded, the recorded one is gone, or the load was refused.
        /// </returns>
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
                Logger.Log($"LoadProbeSourceGCode: G-code file missing: {Session.ProbeSourceGCodeFile}");
                return false;
            }

            try
            {
                var file = GCodeFile.Load(Session.ProbeSourceGCodeFile);
                string? refused = LoadGCodeIntoMachine(file).Refused;
                if (refused != null)
                {
                    Logger.Log("LoadProbeSourceGCode: {0}", refused);
                    return false;
                }

                Logger.Log($"LoadProbeSourceGCode: loaded G-code from {Session.ProbeSourceGCodeFile}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"LoadProbeSourceGCode: failed - {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// What the height map must become after the work zero changed.
        ///
        /// A Z zero reaches here during a run, because a tool change asks for one. Re-applying
        /// the map reloads the G-code and takes the program back to line 0, so during a run
        /// the file is left alone. An XY zero is refused earlier, in SetWorkZeroAndWait.
        /// </summary>
        /// <param name="axes">The axes string, such as "X0 Y0 Z0" or "Z0".</param>
        /// <returns>What it did, for the screen that reports it to the operator.</returns>
        public static WorkZeroOutcome HandleWorkZeroChange(string axes)
        {
            if (ZeroTouchesXY(axes))
            {
                // The map's coordinates move with the work origin, so the map goes.
                bool hadMap = CurrentProbeGrid != null;

                string? notDiscarded = DiscardProbeDataAndAutosave();
                if (notDiscarded == CliConstants.ErrorAutosaveNotDeleted)
                {
                    // The map came out of the G-code; only the saved copy is still there, so
                    // the file is right and there is nothing to reload.
                    Logger.Log("HandleWorkZeroChange: {0}", notDiscarded);
                    return WorkZeroOutcome.MapDiscarded;
                }

                if (notDiscarded != null)
                {
                    return WorkZeroOutcome.MapNotDiscarded;
                }

                if (!hadMap)
                {
                    return WorkZeroOutcome.NothingToDo;
                }

                Logger.Log("HandleWorkZeroChange: X/Y zeroed, probe data discarded");
                return WorkZeroOutcome.MapDiscarded;
            }

            if (IsRunInProgress)
            {
                Logger.Log("HandleWorkZeroChange: a run owns the loaded file, leaving it alone");
                return WorkZeroOutcome.FileLeftAlone;
            }

            if (AreProbePointsApplied && ProbePoints != null)
            {
                // The map's heights were measured against the old Z0, so it is reloaded and
                // baked in again against the new one.
                return ReapplyProbeGrid()
                    ? WorkZeroOutcome.MapReapplied
                    : WorkZeroOutcome.MapNotReapplied;
            }

            Logger.Log("HandleWorkZeroChange: Z-only zero, no probe grid to re-apply");
            return WorkZeroOutcome.NothingToDo;
        }

        /// <summary>
        /// Reloads original G-code and re-applies the probe grid.
        /// Used when Z0 changes and probe grid was already applied.
        /// </summary>
        /// <returns>True once the map is baked into the reloaded G-code.</returns>
        private static bool ReapplyProbeGrid()
        {
            if (ProbePoints == null || string.IsNullOrEmpty(Session.LastLoadedGCodeFile))
            {
                Logger.Log("ReapplyProbeGrid: no probe points or no source file, skipping");
                return false;
            }

            if (!File.Exists(Session.LastLoadedGCodeFile))
            {
                Logger.Log($"ReapplyProbeGrid: source file missing: {Session.LastLoadedGCodeFile}");
                return false;
            }

            try
            {
                var file = GCodeFile.Load(Session.LastLoadedGCodeFile);
                string? refused = LoadGCodeIntoMachine(file).Refused;
                if (refused != null)
                {
                    // The map stays baked into the streaming file. Applying it again would
                    // double the corrections.
                    Logger.Log("ReapplyProbeGrid: {0}", refused);
                    return false;
                }

                // Re-apply probe grid with new Z0 reference
                string? failed = ApplyProbeData();
                if (failed != null)
                {
                    Logger.Log("ReapplyProbeGrid: {0}", failed);
                    return false;
                }

                Logger.Log($"ReapplyProbeGrid: reloaded and re-applied probe grid");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"ReapplyProbeGrid: failed - {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Drops the map and puts the original G-code back if the map was applied to it.
        /// </summary>
        /// <returns>The reason nothing was discarded, or null once it was.</returns>
        public static string? DiscardProbeData()
        {
            if (ProbePoints == null && !AreProbePointsApplied)
            {
                return null;
            }

            // Asked before anything is cleared. The reload takes the map back out of the
            // G-code; clearing first and not reloading leaves the machine cutting a map
            // AppState says is gone.
            string? blocked = WhyTheFileCannotChange();
            if (blocked != null)
            {
                Logger.Log("DiscardProbeData: {0}", blocked);
                return blocked;
            }

            // The reload takes the map back out of the G-code, and it runs before AppState
            // forgets the map. A reload that fails then leaves the two still agreeing.
            if (AreProbePointsApplied)
            {
                string? notRemoved = RemoveMapFromLoadedGCode();
                if (notRemoved != null)
                {
                    return notRemoved;
                }
            }

            return AdoptProbeGrid(null);
        }

        /// <summary>
        /// Puts the original G-code back, so the map's corrections are no longer in what the
        /// machine would cut.
        /// </summary>
        /// <returns>The reason the map is still in the G-code, or null once it is out.</returns>
        private static string? RemoveMapFromLoadedGCode()
        {
            if (string.IsNullOrEmpty(Session.LastLoadedGCodeFile)
                || !File.Exists(Session.LastLoadedGCodeFile))
            {
                Logger.Log(
                    "RemoveMapFromLoadedGCode: source gone: {0}", Session.LastLoadedGCodeFile);
                return CliConstants.ErrorMapStuckInGCode;
            }

            try
            {
                return LoadGCodeIntoMachine(GCodeFile.Load(Session.LastLoadedGCodeFile)).Refused;
            }
            catch (Exception ex)
            {
                Logger.Log("RemoveMapFromLoadedGCode: {0}", ex.Message);
                return CliConstants.ErrorMapStuckInGCode;
            }
        }
    }
}
