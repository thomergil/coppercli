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
            _millingController ??= CreateMillingController();

        private static MillingController CreateMillingController()
        {
            var controller = new MillingController(Machine, new MillSectionsAndDepth());
            controller.StateChanged += state =>
            {
                if (state == ControllerState.Completed)
                {
                    DeleteStoredMapsForLoadedFile();
                }

                if (ControllerBase.IsFinishedState(state))
                {
                    ClearEndedSectionsAndDepth();
                }
            };
            return controller;
        }

        private static ToolChangeController? _toolChangeController;
        public static ToolChangeController ToolChange =>
            _toolChangeController ??= CreateToolChangeController();

        private static ToolChangeController CreateToolChangeController()
        {
            var controller = new ToolChangeController(
                Machine,
                MachineProfiles.HasToolSetter,
                MachineProfiles.GetToolSetterPosition,
                () => ConvertToolSetterConfig(MachineProfiles.GetToolSetterConfig()));

            // A stop whose tool change did not finish in time ends the milling run first, and the
            // file cannot change until this run ends too.
            controller.StateChanged += state =>
            {
                if (ControllerBase.IsFinishedState(state))
                {
                    ClearEndedSectionsAndDepth();
                }
            };
            return controller;
        }

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

        /// <summary>The G-code file as it was loaded, from the same build as <see cref="MachineFile"/>.</summary>
        public static GCodeFile? CurrentFile => _machineFileBuild?.Inputs.Source;

        /// <summary>
        /// What the machine's G-code is built from: the file as loaded, the applied map, the
        /// depth adjustment, and the part of the job chosen (null phases or sections mean all).
        /// The sections and depth apply up to <paramref name="SectionsAndDepthEnd"/>, a tool
        /// change of the machine's G-code counted from 1, once the operator has ended them there
        /// during a run; null applies them to the whole job.
        /// </summary>
        private sealed record JobInputs(
            GCodeFile Source, ProbeGrid? Map, double DepthAdjustment, BoardSections? Sections, ChosenPhases? Phases,
            int? SectionsAndDepthEnd)
        {
            /// <summary>The file as loaded, with nothing applied and every part of the job chosen.</summary>
            public static JobInputs WholeJob(GCodeFile source) =>
                new(source, Map: null, DepthAdjustment: 0, Sections: null, Phases: null, SectionsAndDepthEnd: null);

            /// <summary>Whether sections or a depth adjustment are chosen and apply to the whole job.</summary>
            public bool SectionsOrDepthApplyToTheEnd =>
                SectionsAndDepthEnd == null && (Sections != null || DepthAdjustment != 0);

            /// <summary>These inputs with the sections and depth adjustment gone from the whole job.</summary>
            public JobInputs WithoutSectionsAndDepth() =>
                this with { Sections = null, DepthAdjustment = 0, SectionsAndDepthEnd = null };
        }

        /// <summary>
        /// The G-code the machine streams, with the inputs it was built from and its version;
        /// PutFileOnMachine replaces all of them together. Every one of them is read from here,
        /// so none can disagree with the G-code the machine holds.
        /// </summary>
        private sealed record MachineFileBuild(JobInputs Inputs, GCodeFile File, long Version);

        private static volatile MachineFileBuild? _machineFileBuild;

        /// <summary>The last version PutFileOnMachine gave out, written under FileLock.</summary>
        private static long _lastMachineFileVersion;

        /// <summary>
        /// Held while the machine's G-code, or anything it is built from, changes: requests
        /// arrive on several threads, and a change must not interleave with another.
        /// </summary>
        private static readonly object FileLock = new();

        /// <summary>
        /// What the machine streams: the chosen phases of <see cref="CurrentFile"/> and the cuts
        /// in the chosen sections, with the depth adjustment and, once applied, the height map.
        /// </summary>
        public static GCodeFile? MachineFile => _machineFileBuild?.File;

        /// <summary>
        /// Changes whenever any input to the machine's G-code does (see JobInputs), and is 0 with
        /// no file loaded. A start names the version the operator checked and is refused if
        /// the version has changed since.
        /// </summary>
        public static long MachineFileVersion => _machineFileBuild?.Version ?? 0;

        public static ProbeGrid? ProbePoints { get; private set; }

        /// <summary>Whether the machine's G-code carries the height map.</summary>
        public static bool AreProbePointsApplied => _machineFileBuild?.Inputs.Map != null;

        /// <summary>A complete map is adopted but not yet applied to the G-code.</summary>
        public static bool HasCompleteMapNotApplied =>
            ProbePoints is { HasCompleteData: true } && !AreProbePointsApplied;

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
        /// The progress and time left of the milling run in progress (MillingController.Estimate),
        /// or null with no run in progress. Read from the backing field, so asking does not
        /// create a controller.
        /// </summary>
        public static JobEstimate? MillEstimate =>
            _millingController is { IsRunInProgress: true } milling ? milling.Estimate : null;

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
        /// line number; a machine still streaming ignores a new file.
        /// </summary>
        public static string? WhyTheFileCannotChange() =>
            IsRunInProgress || Machine?.Mode == Machine.OperatingMode.SendFile
                ? CliConstants.ErrorFileChangeDuringRun
                : null;

        /// <inheritdoc cref="Core.Controllers.IProbeController.IsTracingOutline"/>
        public static bool IsTracingOutline => _probeController?.IsTracingOutline ?? false;

        /// <inheritdoc cref="Core.Controllers.IProbeController.IsMeasuringGrid"/>
        public static bool IsMeasuringGrid => _probeController?.IsMeasuringGrid ?? false;
        public static bool SuppressErrors { get; set; } = false;
        public static bool MacroMode { get; set; } = false;

        /// <summary>
        /// Millimeters added to the Z of every cut below work zero, so a negative value cuts
        /// deeper; travel keeps its height. Back to zero when a file loads or the height map
        /// changes, so it carries over only between runs of the same job, and after a run in
        /// which the operator cleared it at a tool change (ClearEndedSectionsAndDepth).
        /// </summary>
        public static double DepthAdjustment => _machineFileBuild?.Inputs.DepthAdjustment ?? 0;

        /// <returns>Null once the depth has changed, or why it did not.</returns>
        public static string? AdjustDepthDeeper() => StepDepth(-CliConstants.DepthAdjustmentIncrement);

        /// <inheritdoc cref="AdjustDepthDeeper"/>
        public static string? AdjustDepthShallower() => StepDepth(CliConstants.DepthAdjustmentIncrement);

        /// <summary>
        /// Reads the depth under the lock that changes it, so two steps at once each count and
        /// a step cannot carry the last job's depth onto a file loaded in between.
        /// </summary>
        private static string? StepDepth(double step)
        {
            lock (FileLock)
            {
                return SetDepthAdjustment(DepthAdjustment + step);
            }
        }

        /// <summary>
        /// Clamped to DepthAdjustmentMax either way. Refused during a run, because the new
        /// depth rewrites the file the run is streaming.
        /// </summary>
        /// <inheritdoc cref="AdjustDepthDeeper"/>
        public static string? SetDepthAdjustment(double value)
        {
            if (!double.IsFinite(value))
            {
                return CliConstants.ErrorInvalidDepth;
            }

            double depth = Math.Round(
                Math.Clamp(value, -CliConstants.DepthAdjustmentMax, CliConstants.DepthAdjustmentMax),
                CliConstants.DepthAdjustmentDecimals);
            return ChangeJob(nameof(SetDepthAdjustment), inputs => (inputs with { DepthAdjustment = depth }, null));
        }

        /// <summary>
        /// The sections of the board the machine's G-code keeps, or null for the whole board.
        /// Resets to the whole board whenever the depth resets to 0 (see DepthAdjustment).
        /// </summary>
        public static BoardSections? MillSections => _machineFileBuild?.Inputs.Sections;

        /// <summary>
        /// Chooses the sections of the board a run mills; choosing none, or all of them, mills
        /// the whole board. Refused during a run, because the choice rewrites the file the run
        /// is streaming.
        /// </summary>
        /// <returns>Null once the sections are chosen, or why they were not.</returns>
        public static string? ChooseMillSections(int columns, int rows, IEnumerable<BoardCell> chosen)
        {
            var cells = chosen.ToList();
            Logger.Log("ChooseMillSections: {0}x{1}, cells {2}", columns, rows,
                string.Join(" ", cells.Select(cell => $"({cell.Column},{cell.Row})")));
            return ChangeJob(nameof(ChooseMillSections), inputs =>
            {
                var (sections, refused) = BoardSections.Choose(inputs.Source, columns, rows, cells);
                return (refused == null ? inputs with { Sections = sections } : null, refused);
            });
        }

        /// <summary>
        /// The phases of the job the machine's G-code keeps, or null for all of them. Resets to
        /// every phase when a file loads or the height map changes (ClearJobCorrections).
        /// </summary>
        public static ChosenPhases? MillPhases => _machineFileBuild?.Inputs.Phases;

        /// <summary>
        /// Chooses the phases a run mills, by number from 1; choosing all of them mills the whole
        /// job. Refused during a run, with no file loaded, for no phases or one not in the file,
        /// and when the job cannot be built without the phases cleared (see PartClip).
        /// </summary>
        /// <returns>Null once the phases are chosen, or why they were not.</returns>
        public static string? ChooseMillPhases(IEnumerable<int> numbers)
        {
            var chosen = numbers.ToList();
            Logger.Log("ChooseMillPhases: {0}", string.Join(", ", chosen));
            return ChangeJob(nameof(ChooseMillPhases), inputs =>
            {
                var (phases, refused) = ChosenPhases.Choose(inputs.Source, chosen);
                return (refused == null ? inputs with { Phases = phases } : null, refused);
            });
        }

        /// <summary>
        /// The cells of <paramref name="division"/> the loaded file cuts in the phases a run
        /// mills: the picture of the board both UIs draw to choose sections on.
        /// </summary>
        public static IReadOnlySet<BoardCell> CellsCut(BoardDivision division) =>
            WithFileLocked(() => CurrentFile?.CellsCut(division, MillPhases) ?? new HashSet<BoardCell>());

        /// <summary>
        /// The part of the job the machine's G-code holds, for the log: the phases and sections
        /// chosen, each "all" when it is the whole job.
        /// </summary>
        public static string DescribeJobPart() =>
            $"phases {MillPhases?.ToString() ?? "all"}, sections {MillSections?.ToString() ?? "all"}"
            + (_machineFileBuild?.Inputs.SectionsAndDepthEnd is int end ? $", sections and depth end at tool change {end}" : "");

        /// <summary>
        /// Rebuilds the machine's G-code from the inputs <paramref name="change"/> makes of the
        /// current ones, under FileLock. Refused during a run, because the new G-code rewrites
        /// the file the run is streaming, and with no file loaded.
        /// </summary>
        /// <param name="action">What is changing, for the log.</param>
        /// <param name="change">The new inputs, or null with the reason the change is refused.</param>
        /// <returns>Null once the G-code is rebuilt, or why it was not.</returns>
        private static string? ChangeJob(string action, Func<JobInputs, (JobInputs? Changed, string? Refused)> change)
        {
            lock (FileLock)
            {
                string? refused = WhyTheFileCannotChange();
                if (refused == null && _machineFileBuild is not MachineFileBuild)
                {
                    refused = CliConstants.ErrorNoFileLoaded;
                }

                if (refused == null)
                {
                    var (changed, notChanged) = change(_machineFileBuild!.Inputs);
                    refused = notChanged ?? PutFileOnMachine(changed!);
                }

                Logger.Log("{0}: {1}", action, refused ?? $"now {DescribeJobPart()}, depth {DepthAdjustment:F3}");
                return refused;
            }
        }

        /// <summary>
        /// Runs <paramref name="action"/> while nothing can change the machine's G-code, so a
        /// run started inside it streams exactly what was checked: StartAsync claims the run
        /// before it returns.
        /// </summary>
        internal static T WithFileLocked<T>(Func<T> action)
        {
            lock (FileLock)
            {
                return action();
            }
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
        /// The one way a G-code file is loaded. Refused while a run is in progress: a run
        /// tracks its place in Machine.File by line number, and a new file resets that to the
        /// start.
        /// </summary>
        /// <returns>What the load did: see <see cref="LoadOutcome"/>.</returns>
        public static LoadOutcome LoadGCodeIntoMachine(GCodeFile file)
        {
            lock (FileLock)
            {
                string? blocked = WhyTheFileCannotChange();
                if (blocked != null)
                {
                    Logger.Log("LoadGCodeIntoMachine: {0}", blocked);
                    return new LoadOutcome(blocked, null);
                }

                ClearJobCorrections(file);

                // Recorded here rather than at each caller, and under the lock with the file:
                // a height map's applicability is decided by comparing against this, so a
                // caller that forgot it, or a load in between, would make every later check
                // wrong.
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
        }

        /// <returns>The grid, or null with the reason it was refused.</returns>
        public static (ProbeGrid? Grid, string? Refused) LoadProbeGridFromFile(string path) =>
            AdoptProbeGridFromFile(ProbeGrid.Load(path), path);

        /// <summary>
        /// Adopts a grid already read from <paramref name="path"/>, so a caller adopts the grid
        /// it checked instead of reading the file again.
        /// </summary>
        /// <returns>The grid, or null with the reason it was refused.</returns>
        public static (ProbeGrid? Grid, string? Refused) AdoptProbeGridFromFile(ProbeGrid grid, string path)
        {
            string? notAdopted = AdoptProbeGrid(grid);
            if (notAdopted != null)
            {
                return (null, notAdopted);
            }

            // A grid from a file is already saved, so anything in the autosave belongs to an
            // earlier one and would otherwise be offered as unsaved work.
            Persistence.ClearProbeAutoSave();
            Persistence.RememberProbeFile(path);
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

            string? notDiscarded = DiscardProbeData();
            if (notDiscarded != null)
            {
                Logger.Log("DiscardInapplicableProbeData: kept the height map: {0}", notDiscarded);
                return null;
            }

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
        /// Changes the loaded height map and rebuilds the machine's G-code with no map applied
        /// and no depth adjustment. Refused during a run, because that rewrites the file the
        /// run is streaming.
        /// </summary>
        /// <param name="grid">The new map, or null to have none.</param>
        /// <returns>Why the map was left alone, or null once it was replaced.</returns>
        public static string? AdoptProbeGrid(ProbeGrid? grid)
        {
            lock (FileLock)
            {
                string? blocked = WhyTheFileCannotChange();
                if (blocked != null)
                {
                    Logger.Log("AdoptProbeGrid: {0}", blocked);
                    return blocked;
                }

                ProbePoints = grid;
                ClearJobCorrections(CurrentFile);
                return null;
            }
        }

        /// <summary>
        /// Loads <paramref name="source"/> with the job's corrections cleared: no map applied,
        /// no depth adjustment, the whole job. A new map or a new file describes a new job, and a
        /// depth, sections or phases picked for the last one do not carry over to it.
        /// </summary>
        private static void ClearJobCorrections(GCodeFile? source)
        {
            Logger.Log($"ClearJobCorrections: map applied was {AreProbePointsApplied}, depth was {DepthAdjustment:F3}, {DescribeJobPart()}");
            PutFileOnMachine(source == null ? null : JobInputs.WholeJob(source));
        }

        /// <summary>
        /// Builds the G-code the machine streams from <paramref name="inputs"/> and loads it into
        /// the machine, or unloads it for null. Callers check WhyTheFileCannotChange first,
        /// because this does not refuse during a run; the one exception is
        /// EndSectionsAndDepthAtTheToolChange, which passes the lines streamed. Every change to
        /// any input comes through here, under FileLock, so the machine's G-code cannot disagree
        /// with them.
        /// </summary>
        /// <param name="keepStreamed">
        /// The number of lines a run waiting at a tool change has already streamed; the new G-code
        /// must repeat those lines exactly, and the stream continues after them. 0 otherwise.
        /// </param>
        /// <returns>Null once loaded, or why the file cannot take the inputs.</returns>
        private static string? PutFileOnMachine(JobInputs? inputs, int keepStreamed = 0)
        {
            lock (FileLock)
            {
                if (inputs == null)
                {
                    _machineFileBuild = null;
                    Machine?.ClearFile();
                    return null;
                }

                var (file, refused) = BuildMachineFile(inputs);
                if (file == null)
                {
                    return refused;
                }

                var lines = file.GetGCode();
                if (keepStreamed > 0 && !lines.Take(keepStreamed).SequenceEqual(Machine.File.Take(keepStreamed)))
                {
                    Logger.Log("PutFileOnMachine: the new G-code differs in the {0} lines already streamed", keepStreamed);
                    return CliConstants.ErrorFileChangeDuringRun;
                }

                if (Machine?.SetFile(lines, keepStreamed) == false)
                {
                    Logger.Log("PutFileOnMachine: the machine refused the file, streaming from line {0}", keepStreamed);
                    return CliConstants.ErrorFileChangeDuringRun;
                }

                _machineFileBuild = new MachineFileBuild(inputs, file, ++_lastMachineFileVersion);
                return null;
            }
        }

        /// <summary>The G-code the machine streams for <paramref name="inputs"/>, or null with why the file cannot take them.</summary>
        private static (GCodeFile? File, string? Refused) BuildMachineFile(JobInputs inputs)
        {
            var (part, refused) = KeepChosenPart(inputs);
            if (part == null)
            {
                return (null, refused);
            }

            var file = part.OffsetCutDepth(inputs.DepthAdjustment, inputs.SectionsAndDepthEnd);
            return (inputs.Map == null ? file : file.ApplyProbeGrid(inputs.Map), null);
        }

        /// <summary>
        /// The chosen phases of the file, and in them the cuts in the chosen sections: the first
        /// step of <see cref="BuildMachineFile"/>, before the depth and the map move any cut.
        /// </summary>
        private static (GCodeFile? File, string? Refused) KeepChosenPart(JobInputs inputs)
        {
            var (source, map, depthAdjustment, sections, phases, sectionsAndDepthEnd) = inputs;

            if ((map != null || depthAdjustment != 0 || sections != null) && source.HasArcsOutsideXYPlane)
            {
                Logger.Log("BuildMachineFile: {0} has arcs outside the XY plane", source.FileName);
                return (null, Constants.ErrorArcsOutsideXYPlane);
            }

            if (sections == null && phases == null)
            {
                return (source, null);
            }

            var (kept, refused) = source.KeepPart(phases, sections, sectionsAndDepthEnd);
            if (kept == null)
            {
                Logger.Log($"BuildMachineFile: {refused}");
            }
            return (kept, refused);
        }

        /// <summary>
        /// The number, counted from 1, of the tool change the machine's stream stopped at, or
        /// null where the last line streamed is not a tool change. The parser refuses a tool
        /// change on a line the machine's M6 test would hold back with other words, so the count
        /// of M6 lines in the machine's G-code matches the count of tool change commands.
        /// </summary>
        private static int? ToolChangeTheStreamStoppedAt()
        {
            var lines = Machine.File;
            int streamed = Machine.FilePosition;
            return streamed > 0 && streamed <= lines.Count && GCodeParser.IsM6Line(lines[streamed - 1])
                ? lines.Take(streamed).Count(GCodeParser.IsM6Line)
                : null;
        }

        /// <summary>
        /// Whether sections or a depth adjustment are chosen for the whole job and the job cuts
        /// before the tool change the stream stopped at; MillingController then offers to end
        /// them there. The cuts are read before the depth adjustment and the map, which can lift
        /// a shallow cut above the surface.
        /// </summary>
        internal static bool SectionsOrDepthApplyAfterTheToolChange()
        {
            lock (FileLock)
            {
                return _machineFileBuild is { Inputs.SectionsOrDepthApplyToTheEnd: true } build
                    && ToolChangeTheStreamStoppedAt() is int toolChange
                    && KeepChosenPart(build.Inputs).File?.CutsBeforeToolChange(toolChange) == true;
            }
        }

        /// <summary>
        /// Rebuilds the machine's G-code so the sections and depth adjustment apply only up to the
        /// tool change a milling run waits at, with the map still applied, and continues the
        /// stream where it stopped. It is the one change to the file made during a run; the
        /// lines already sent must come out the same, so the run carries on where it stopped.
        /// </summary>
        /// <returns>Null once the rest of the job is rebuilt, or why it was not.</returns>
        internal static string? EndSectionsAndDepthAtTheToolChange()
        {
            lock (FileLock)
            {
                int streamed = Machine.FilePosition;
                if (_millingController is not { IsRunInProgress: true, Phase: MillingPhase.ToolChange }
                    || _machineFileBuild is not MachineFileBuild build
                    || ToolChangeTheStreamStoppedAt() is not int toolChange)
                {
                    Logger.Log("EndSectionsAndDepthAtTheToolChange: no milling run waits at a tool change ({0} lines streamed)", streamed);
                    return ControllerConstants.ErrorSectionsAndDepthNotEnded;
                }

                string? refused = PutFileOnMachine(build.Inputs with { SectionsAndDepthEnd = toolChange }, streamed);
                if (refused != null)
                {
                    Logger.Log("EndSectionsAndDepthAtTheToolChange: {0}", refused);
                    return ControllerConstants.ErrorSectionsAndDepthNotEnded;
                }

                Logger.Log("EndSectionsAndDepthAtTheToolChange: sections and depth end at tool change {0}; {1} lines, resuming at {2}",
                    toolChange, Machine.File.Count, streamed);
                return null;
            }
        }

        /// <summary>
        /// After a milling run that ended the sections and depth adjustment at a tool change,
        /// clears them from the whole job, as the operator chose, so the next run mills the
        /// whole board at the file's depth. Called when the milling run ends and when a tool
        /// change's run ends, whichever is last.
        /// </summary>
        private static void ClearEndedSectionsAndDepth()
        {
            if (_machineFileBuild?.Inputs.SectionsAndDepthEnd != null && _millingController?.IsRunInProgress != true)
            {
                ChangeJob(nameof(ClearEndedSectionsAndDepth), inputs => (inputs.WithoutSectionsAndDepth(), null));
            }
        }

        /// <summary>The milling controller's view of the job's sections and depth adjustment.</summary>
        private sealed class MillSectionsAndDepth : ISectionsAndDepth
        {
            public bool ApplyAfterTheToolChange() => SectionsOrDepthApplyAfterTheToolChange();

            public string? EndAtTheToolChange() => EndSectionsAndDepthAtTheToolChange();
        }

        /// <summary>Leaves no G-code loaded, as at startup. Tests only: it does not refuse during a run.</summary>
        internal static void UnloadFileForTest()
        {
            lock (FileLock)
            {
                ClearJobCorrections(null);
            }
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
            lock (FileLock)
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
                // not.
                if (ReadAutosaveNotYetAdopted() is ProbeGrid autosave)
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

                if (AreProbePointsApplied)
                {
                    Logger.Log("ApplyProbeData: already applied");
                    return null;
                }

                string? notPut = PutFileOnMachine(_machineFileBuild!.Inputs with { Map = ProbePoints });
                if (notPut != null)
                {
                    return notPut;
                }

                Logger.Log("ApplyProbeData: applied successfully, AreProbePointsApplied=true");
                return null;
            }
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
        public static ProbeGrid? ReadUsableAutosave() =>
            UsableForThisJob(Persistence.ReadProbeAutoSave());

        /// <summary>
        /// The usable autosave while no map is adopted: the one the session questions offer to
        /// keep until it is adopted.
        /// </summary>
        public static ProbeGrid? ReadAutosaveNotYetAdopted() =>
            ProbePoints == null ? ReadUsableAutosave() : null;

        /// <summary>
        /// A height map describes the board in the machine, and a finished mill leaves that
        /// board done, so the autosave and the saved map measured for the milled file are
        /// deleted. The map in memory stays, so the same job can run again this session.
        /// </summary>
        internal static void DeleteStoredMapsForLoadedFile()
        {
            if (IsMeasuredForLoadedFile(Persistence.ReadProbeAutoSave()))
            {
                Persistence.ClearProbeAutoSave();
            }

            if (IsMeasuredForLoadedFile(Persistence.ReadProbeGrid(Session.LastProbeFile)))
            {
                Persistence.DeleteRememberedProbeFile();
            }
        }

        /// <summary>
        /// Measured for the loaded file, from any origin. Unknown when the map recorded no
        /// file, so a map that may belong to another board is left alone.
        /// </summary>
        private static bool IsMeasuredForLoadedFile(ProbeGrid? grid) =>
            DescribeApplicability(grid) is ProbeApplicability.Applicable or ProbeApplicability.OriginMoved;

        /// <summary>
        /// The height map file last saved or loaded, read without adopting it. Null unless a
        /// G-code file is loaded, no map is adopted, and the map is complete, records its
        /// G-code file, and is usable for the loaded one.
        /// </summary>
        public static ProbeGrid? ReadSavedProbeGridForLoadedFile()
        {
            if (CurrentFile == null || ProbePoints != null)
            {
                return null;
            }

            var saved = Persistence.ReadProbeGrid(Session.LastProbeFile);
            return saved is { HasCompleteData: true, Context.IsKnown: true }
                ? UsableForThisJob(saved)
                : null;
        }

        /// <summary>
        /// The one check that a map read from disk fits the loaded job, for both the autosave
        /// and the saved map file. See rule <c>derived-artifact-records-its-context</c>.
        /// </summary>
        private static ProbeGrid? UsableForThisJob(ProbeGrid? candidate)
        {
            if (candidate == null)
            {
                return null;
            }

            var applicability = DescribeApplicability(candidate);
            if (!applicability.IsUsable())
            {
                Logger.Log("UsableForThisJob: not applicable ({0})", applicability);
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
        /// Drops the map, then deletes the saved copy. A saved copy that will not delete
        /// leaves the map dropped and returns ErrorAutosaveNotDeleted.
        /// </summary>
        /// <returns>The reason something was left, or null once both are gone.</returns>
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
            if (UsableForThisJob(candidate) == null)
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
        /// Drops the height map on an X or Y zero, which moves the map's coordinates off the
        /// board, and keeps it on a Z zero, because its heights are the copper's, measured from
        /// a zero touched off on that copper. During a run it leaves the file alone: a tool
        /// change asks for a Z zero, and SetWorkZeroAndWait refuses an X/Y zero before it gets
        /// here.
        /// </summary>
        /// <param name="axes">The axes string, such as "X0 Y0 Z0" or "Z0".</param>
        /// <returns>What it did, for the screen that reports it to the operator.</returns>
        public static WorkZeroOutcome HandleWorkZeroChange(string axes)
        {
            if (IsRunInProgress)
            {
                Logger.Log("HandleWorkZeroChange: a run owns the loaded file, leaving it alone");
                return WorkZeroOutcome.FileLeftAlone;
            }

            if (ZeroTouchesXY(axes))
            {
                bool hadMap = CurrentProbeGrid != null;

                // If only the saved copy failed to delete, the map is still dropped; any other
                // refusal leaves it applied to the file.
                string? notDiscarded = DiscardProbeDataAndAutosave();
                if (notDiscarded != null)
                {
                    Logger.Log("HandleWorkZeroChange: {0}", notDiscarded);
                    if (notDiscarded != CliConstants.ErrorAutosaveNotDeleted)
                    {
                        return WorkZeroOutcome.FileLeftAlone;
                    }
                }

                if (!hadMap)
                {
                    return WorkZeroOutcome.NothingToDo;
                }

                Logger.Log("HandleWorkZeroChange: X/Y zeroed, probe data discarded");
                return WorkZeroOutcome.MapDiscarded;
            }

            if (AreProbePointsApplied && ProbePoints != null)
            {
                Logger.Log("HandleWorkZeroChange: Z-only zero, height map stays applied");
                return WorkZeroOutcome.MapStillApplied;
            }

            Logger.Log("HandleWorkZeroChange: Z-only zero, no height map applied");
            return WorkZeroOutcome.NothingToDo;
        }

        /// <summary>
        /// Drops the map, and the machine's G-code is built again without it.
        /// </summary>
        /// <returns>The reason nothing was discarded, or null once it was.</returns>
        public static string? DiscardProbeData()
        {
            if (ProbePoints == null && !AreProbePointsApplied)
            {
                return null;
            }

            return AdoptProbeGrid(null);
        }
    }
}
