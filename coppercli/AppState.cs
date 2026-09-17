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
    /// What <see cref="AppState.LoadGCodeIntoMachine"/> did.
    /// </summary>
    /// <param name="Refused">Why the file was not loaded, or null once it was.</param>
    /// <param name="MapDiscardedBecause">
    /// Why the loaded height map was dropped, or null if it was kept. Returned rather than
    /// stored, because two browser tabs load files on their own threads and a shared field
    /// would hand one load's reason to the other.
    /// </param>
    internal sealed record LoadOutcome(string? Refused, string? MapDiscardedBecause);

    internal static class AppState
    {
        public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static Machine _machine = null!;

        /// <summary>
        /// Assigning a new machine moves the ConnectionStateChanged subscription with it, so
        /// the work origin is cleared for whichever machine is live.
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
        /// A disconnected machine may be repositioned or power-cycled before it returns, so the
        /// asserted work origin cannot be trusted. Clearing it here covers every disconnect
        /// path, as Core clears <c>IsHomed</c> inside <c>Machine.Disconnect</c>.
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

        // Created on first use, because Machine is assigned during startup.
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
        /// Call this when Machine is replaced, so the next read builds the controllers on the
        /// new one.
        /// </summary>
        public static void ResetControllers()
        {
            _millingController = null;
            _toolChangeController = null;
            _probeController = null;
        }

        public static GCodeFile? CurrentFile { get; set; }
        public static ProbeGrid? ProbePoints { get; private set; }

        // Only ApplyProbeData sets this true, and only ResetProbeApplicationState sets it
        // back to false.
        public static bool AreProbePointsApplied { get; private set; } = false;
        /// <summary>
        /// Whether the work origin is known, written only through the three setters below.
        /// </summary>
        public static bool IsWorkZeroSet { get; private set; } = false;

        public static void MarkWorkZeroSet() => SetWorkZeroKnown(true, "zeroed on the machine");

        /// <summary>
        /// The operator confirmed an origin nobody set this session: one remembered from
        /// the last session, or one GRBL kept across a reconnect.
        /// </summary>
        public static void SetWorkZeroTrusted(bool trusted) =>
            SetWorkZeroKnown(trusted, "trusted by the operator");

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
        /// Read from the probe controller, so no front end keeps a flag of its own.
        /// </summary>
        public static bool IsProbing => _probeController?.IsActive ?? false;

        /// <summary>
        /// True while any run is under way, parked at a prompt or not. Read from the backing
        /// fields, so asking does not create a controller.
        /// </summary>
        public static bool IsRunInProgress =>
            (_millingController?.IsRunInProgress ?? false)
            || (_probeController?.IsRunInProgress ?? false)
            || (_toolChangeController?.IsRunInProgress ?? false);

        public static bool ZeroTouchesXY(string axes)
        {
            string upper = axes.ToUpperInvariant();
            return upper.Contains('X') || upper.Contains('Y');
        }

        public static bool ZeroIsFullOrigin(string axes)
        {
            string upper = axes.ToUpperInvariant();
            return upper.Contains('X') && upper.Contains('Y') && upper.Contains('Z');
        }

        /// <summary>
        /// Why the loaded file and the height map cannot change now, or null. A run streams
        /// from Machine.File with the map's corrections already in it, and tracks its place by
        /// line number.
        /// </summary>
        public static string? WhyTheFileCannotChange() =>
            IsRunInProgress ? CliConstants.ErrorFileChangeDuringRun : null;

        /// <inheritdoc cref="Core.Controllers.IProbeController.IsTracingOutline"/>
        public static bool IsTracingOutline => _probeController?.IsTracingOutline ?? false;

        /// <inheritdoc cref="Core.Controllers.IProbeController.IsMeasuringGrid"/>
        public static bool IsMeasuringGrid => _probeController?.IsMeasuringGrid ?? false;
        public static bool SuppressErrors { get; set; } = false;
        public static bool MacroMode { get; set; } = false;

        // Negative cuts deeper, positive shallower.
        public static double DepthAdjustment { get; private set; } = 0;

        public static void AdjustDepthDeeper()
        {
            DepthAdjustment = Math.Max(DepthAdjustment - CliConstants.DepthAdjustmentIncrement, -CliConstants.DepthAdjustmentMax);
        }

        public static void AdjustDepthShallower()
        {
            DepthAdjustment = Math.Min(DepthAdjustment + CliConstants.DepthAdjustmentIncrement, CliConstants.DepthAdjustmentMax);
        }

        public static void SetDepthAdjustment(double value)
        {
            DepthAdjustment = Math.Clamp(value, -CliConstants.DepthAdjustmentMax, CliConstants.DepthAdjustmentMax);
        }

        public static void ResetDepthAdjustment()
        {
            DepthAdjustment = 0;
        }

        /// <summary>
        /// Advance it with <see cref="CycleJogPreset"/> and read the preset from
        /// <see cref="CurrentJogMode"/>, so the wrap-around and the lookup are each defined once.
        /// </summary>
        public static int JogPresetIndex { get; private set; } = CliConstants.DefaultJogModeIndex;

        public static CliConstants.JogMode CurrentJogMode => CliConstants.JogModes[JogPresetIndex];

        public static void CycleJogPreset()
        {
            JogPresetIndex = (JogPresetIndex + 1) % CliConstants.JogModes.Length;
        }

        /// <summary>
        /// The one path that loads a file into the machine, apart from ApplyProbeData, which
        /// rewrites the same file in place. Refused while a run is in progress: a run tracks
        /// its place in Machine.File by line number, and a new file resets that to the start.
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

            // Recorded here rather than at each caller: a height map's applicability is
            // decided by comparing against this, so a caller that forgot it would make every
            // later check wrong.
            if (!string.IsNullOrEmpty(file.FilePath))
            {
                Session.LastLoadedGCodeFile = file.FilePath;
            }

            // Decided here too, so the menu, the web UI, a macro and session restore all
            // treat the loaded map the same way.
            string? mapDiscardedBecause = DiscardInapplicableProbeData();

            Logger.Log($"LoadGCodeIntoMachine: loaded {file.FileName}, AreProbePointsApplied=false");

            return new LoadOutcome(null, mapDiscardedBecause);
        }

        /// <summary>
        /// A grid already applied to the in-memory G-code is taken back out first, by
        /// reloading the original: ApplyProbeGrid adds to Z, so a second grid on top of the
        /// first would double the corrections.
        /// </summary>
        /// <returns>The grid, or null with the reason it was refused.</returns>
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
        /// Whether the loaded height map describes the loaded job. Decided from the setup
        /// recorded on the map, so no screen infers it from the session state instead.
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
        /// Drops a height map that does not describe the loaded job, so it cannot be
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
        /// Why a height map does not describe the loaded job, as a phrase that finishes a
        /// sentence about it. Every screen that has to explain a dropped or refused map reads
        /// this, so none of them explains it differently.
        /// </summary>
        internal static string GetInapplicableReason(ProbeApplicability applicability, string measuredFor) =>
            applicability == ProbeApplicability.DifferentFile
                ? $"it was measured for {Path.GetFileName(measuredFor)}"
                : "the work origin has moved since it was measured";

        /// <summary>
        /// The one place the loaded height map changes. Swapping the map clears the applied
        /// flag, so it is refused during a run: the file being streamed would still hold the
        /// corrections, and the next apply would double them.
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

        public static void ResetProbeApplicationState()
        {
            Logger.Log($"ResetProbeApplicationState: was {AreProbePointsApplied}, setting to false");
            AreProbePointsApplied = false;
            ResetDepthAdjustment();
        }

        /// <returns>The new grid, or null with the reason it was refused.</returns>
        public static (ProbeGrid? Grid, string? Refused) SetupProbeGrid(Vector2 fileMin, Vector2 fileMax, double margin, double gridSize)
        {
            var grid = ProbeGrid.ForJob(fileMin, fileMax, margin, gridSize);

            // Stamped at creation from the machine's reported origin, so the map can later be
            // compared against the setup it was measured in.
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
        /// Adopts the autosave as the live map when none is in memory, then adds the map's
        /// corrections to the loaded G-code.
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

            // Applying is an operator action, so it may adopt the autosave; a status read may
            // not. Assigning ProbePoints directly here skipped ResetProbeApplicationState.
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

            LoadProbeSourceGCode();
            return null;
        }

        /// <summary>
        /// The autosave on disk, if it describes the loaded job; a map measured on another
        /// board, or before the origin moved, comes back null. Reads the file and adopts
        /// nothing, so a status can ask without changing what the operator has.
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
        /// The height map for the loaded job: the one loaded, or the autosave when nothing
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
            // Checked before the autosave is deleted, so a refusal leaves both copies where
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
        /// describe this job, or a run is streaming the file.
        /// </returns>
        public static (ProbeGrid? Grid, string? Refused) ForceLoadProbeFromAutosave()
        {
            // "There is none" and "there is one that does not fit" are different answers to
            // the operator, so they are told apart here.
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

        public static bool IsProbeSourceGCodeMissing =>
            ProbePoints != null &&
            CurrentFile == null &&
            !string.IsNullOrEmpty(Session.ProbeSourceGCodeFile) &&
            !File.Exists(Session.ProbeSourceGCodeFile);

        /// <returns>
        /// True once a file is loaded, including one that was already loaded. False when
        /// none is recorded, the recorded one is gone, or the load was refused.
        /// </returns>
        public static bool LoadProbeSourceGCode()
        {
            if (CurrentFile != null)
            {
                return true;
            }

            if (string.IsNullOrEmpty(Session.ProbeSourceGCodeFile))
            {
                return false;
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
        /// A Z zero reaches here during a run, because a tool change asks for one, and
        /// re-applying the map would reload the G-code and take the program back to line 0, so
        /// during a run the file is left alone. An XY zero is refused earlier, in
        /// SetWorkZeroAndWait.
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
                // The map's heights were measured against the old Z0, so the file is reloaded
                // and the map applied again against the new one.
                return ReapplyProbeGrid()
                    ? WorkZeroOutcome.MapReapplied
                    : WorkZeroOutcome.MapNotReapplied;
            }

            Logger.Log("HandleWorkZeroChange: Z-only zero, no probe grid to re-apply");
            return WorkZeroOutcome.NothingToDo;
        }

        /// <returns>True once the map has been applied to the reloaded G-code.</returns>
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
                    // The map's corrections stay in the file being streamed. Applying it again
                    // would double them.
                    Logger.Log("ReapplyProbeGrid: {0}", refused);
                    return false;
                }

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

            // Checked before anything is cleared: clearing first and then failing to reload
            // would leave the machine cutting corrections AppState no longer records.
            string? blocked = WhyTheFileCannotChange();
            if (blocked != null)
            {
                Logger.Log("DiscardProbeData: {0}", blocked);
                return blocked;
            }

            // The reload takes the map back out of the G-code, and runs before the map is
            // dropped from memory, so a failed reload leaves the two still agreeing.
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
