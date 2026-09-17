#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using coppercli.Core.Communication;
using coppercli.Core.GCode;
using coppercli.Core.Util;
using static coppercli.Core.Communication.Machine;

namespace coppercli.Tests.Fakes
{
    /// <summary>
    /// Fake CNC machine that simulates realistic behavior.
    /// Processes commands, updates positions, and simulates timing.
    /// Use for integration tests or demo mode.
    /// </summary>
    public class FakeMachine : IMachine, IDisposable
    {
        // GRBL's door rules, shared with the other two doubles.
        private readonly DoorModel _door;

        public FakeMachine()
        {
            _door = new DoorModel(
                (state, subState) => SetStatus(
                    string.IsNullOrEmpty(subState) ? state : $"{state}:{subState}"),
                // GRBL returns to the state the door interrupted, which is Run for a file
                // that was streaming. Idle here would hide a caller that must re-assert a hold.
                () => Mode == OperatingMode.SendFile
                    ? GrblProtocol.StatusRun
                    : GrblProtocol.StatusIdle);
        }

        // =========================================================================
        // Configuration
        // =========================================================================

        /// <summary>Simulated rapid move speed (mm/s).</summary>
        public double RapidSpeed { get; set; } = 50.0;

        /// <summary>Simulated feed move speed (mm/s).</summary>
        public double FeedSpeed { get; set; } = 10.0;

        /// <summary>Status poll interval (ms).</summary>
        public int PollIntervalMs { get; set; } = 50;

        /// <summary>Simulated homing duration (ms).</summary>
        public int HomingDurationMs { get; set; } = 2000;

        /// <summary>Machine travel limits (mm, negative = toward workpiece).</summary>
        public Vector3 MinPosition { get; set; } = new Vector3(-300, -200, -100);
        public Vector3 MaxPosition { get; set; } = new Vector3(0, 0, 0);

        /// <summary>
        /// Mirrors MachineSettings.PauseFileOnHold (same default). Governs whether
        /// M0/M1/M2/M30 stop the stream - M6 always does, regardless of this setting,
        /// the same decoupling production's Machine.cs applies.
        /// </summary>
        public bool PauseFileOnHold { get; set; } = true;

        // =========================================================================
        // State
        // =========================================================================

        private OperatingMode _mode = OperatingMode.Manual;
        private string _status = "Idle";
        private Vector3 _machinePosition = new Vector3();
        private Vector3 _workOffset = new Vector3();
        private List<string> _fileLines = new();
        private int _filePosition;
        private bool _isHomed;
        private bool _isHoming;
        private CancellationTokenSource? _runCts;
        private Task? _runTask;
        private readonly object _stateLock = new();

        public OperatingMode Mode
        {
            get { lock (_stateLock) return _mode; }
            private set { lock (_stateLock) _mode = value; }
        }

        /// <inheritdoc/>
        public string StatusSubState { get; set; } = string.Empty;

        public string Status
        {
            get { lock (_stateLock) return _status; }
            private set { lock (_stateLock) _status = value; }
        }

        public Vector3 MachinePosition
        {
            get { lock (_stateLock) return _machinePosition; }
            private set { lock (_stateLock) _machinePosition = value; }
        }

        /// <summary>
        /// The combined work offset, as GRBL reports it in a status line: G54 plus any
        /// G92 or tool-length offset. Derived, so it is never accidentally identical to
        /// G54 - the distinction is the whole point of tracking both.
        /// </summary>
        public Vector3 WorkOffset
        {
            get { lock (_stateLock) return _workOffset + ExtraOffset; }
        }

        public Vector3 WorkPosition => MachinePosition - WorkOffset;

        public bool Connected { get; private set; } = true;

        public ReadOnlyCollection<string> File => _fileLines.AsReadOnly();

        public int FilePosition
        {
            get { lock (_stateLock) return _filePosition; }
            private set { lock (_stateLock) _filePosition = value; }
        }

        public bool IsHomed
        {
            get { lock (_stateLock) return _isHomed; }
            set { lock (_stateLock) _isHomed = value; }
        }

        public bool IsHoming
        {
            get { lock (_stateLock) return _isHoming; }
            set { lock (_stateLock) _isHoming = value; }
        }

        private long _statusReportCount;
        public long StatusReportCount => Interlocked.Read(ref _statusReportCount);

        // Tracked separately from WorkOffset on purpose: collapsing the two would make
        // the G54-vs-combined-WCO distinction untestable, which is the bug this models.
        public Vector3 G54Offset { get; private set; } = new Vector3();

        /// <summary>Simulates a live G92 or tool-length offset, so WorkOffset and G54
        /// differ the way they do on a real machine.</summary>
        public Vector3 ExtraOffset { get; set; } = new Vector3();

        public Task<bool> RefreshWorkOffsetsAsync(int timeoutMs, CancellationToken ct = default)
        {
            return Task.FromResult(true);
        }

        public Vector3 LastProbePosMachine { get; private set; } = new Vector3();

        // =========================================================================
        // Events
        // =========================================================================

#pragma warning disable CS0067 // Event is never used (required by interface)
        public event Action<string>? StatusReceived;
        public event Action<Vector3, bool>? ProbeFinished;
        public event Action<string>? NonFatalException;
        public event Action<GrblRejection>? CommandRejected;
        public event Action<string>? Info;
        public event Action? ConnectionStateChanged;
        public event Action? StatusChanged;
        public event Action? OperatingModeChanged;
        public event Action? FilePositionChanged;
#pragma warning restore CS0067

        // =========================================================================
        // IMachine implementation
        // =========================================================================

        private readonly List<string> _sentCommands = new();

        /// <summary>
        /// Every line handed to the machine, in order. Returns a snapshot: commands are
        /// appended from the simulated worker, so handing out the live list would let a
        /// test enumerate it while it is being written.
        /// </summary>
        public IReadOnlyList<string> SentCommands
        {
            get { lock (_stateLock) { return _sentCommands.ToArray(); } }
        }

        /// <summary>Forgets recorded commands, for tests that measure one phase.</summary>
        public void ClearSentCommands()
        {
            lock (_stateLock)
            {
                _sentCommands.Clear();
            }
        }

        public void SendLine(string line)
        {
            lock (_stateLock)
            {
                _sentCommands.Add(line);
            }
            _ = ProcessCommandAsync(line);
        }

        public bool FileStart()
        {
            if (Mode != OperatingMode.Manual)
            {
                return false;
            }

            _runCts = new CancellationTokenSource();
            Mode = OperatingMode.SendFile;
            OperatingModeChanged?.Invoke();

            _runTask = Task.Run(() => RunFileAsync(_runCts.Token));
            return true;
        }

        /// <summary>Forces the operating mode, for tests that model leftover state.</summary>
        public void SimulateModeChange(OperatingMode newMode)
        {
            Mode = newMode;
            OperatingModeChanged?.Invoke();
        }

        public void EnsureManualMode()
        {
            if (Mode == OperatingMode.Probe)
            {
                Mode = OperatingMode.Manual;
            }
        }

        public void FileGoto(int line)
        {
            FilePosition = Math.Clamp(line, 0, _fileLines.Count);
            FilePositionChanged?.Invoke();
        }

        public void FeedHold()
        {
            // Only a running machine takes a feed hold. A door hold is already stopped, and
            // replacing its state would lose which door state it was in.
            if (Status == GrblProtocol.StatusRun)
            {
                SetStatus($"{GrblProtocol.StatusHold}:0");
            }
        }

        public void CycleStart()
        {
            if (IsHolding && !Status.StartsWith(GrblProtocol.StatusDoor))
            {
                SetStatus(GrblProtocol.StatusRun);
                return;
            }

            _door.CycleStart(Status, StatusSubState);
        }

        /// <inheritdoc cref="DoorModel.RestoreMs"/>
        public int DoorRestoreMs
        {
            get => _door.RestoreMs;
            set => _door.RestoreMs = value;
        }

        /// <inheritdoc cref="DoorModel.Holding"/>
        private bool IsHolding => DoorModel.Holding(Status);

        public void SoftReset()
        {
            _runCts?.Cancel();
            Mode = OperatingMode.Manual;
            SetStatus(DoorModel.ResetAlarms(Status)
                ? $"{GrblProtocol.StatusAlarm}:1"
                : GrblProtocol.StatusIdle);
            OperatingModeChanged?.Invoke();
        }

        public bool ProbeStart()
        {
            // FakeMachine handles probing in ProcessProbeAsync
            return true;
        }

        public void ProbeStop()
        {
            // FakeMachine handles probing in ProcessProbeAsync
        }

        // =========================================================================
        // Command processing
        // =========================================================================

        private async Task ProcessCommandAsync(string line)
        {
            line = line.Trim().ToUpperInvariant();

            // System commands
            if (line == "$H")
            {
                await HomeAsync();
                return;
            }

            if (line == "$X")
            {
                SetStatus("Idle");
                return;
            }

            // G-code commands.
            // The motion word may be preceded by G53 ("this block is in machine
            // coordinates"), so dispatch on the word after any such prefix while still
            // passing the whole line down - ProcessMoveAsync reads G53 from it.
            string dispatch = line.StartsWith("G53") ? line.Substring(3).TrimStart() : line;

            // G10 must be tested before G1, or "G10 L2 P1 Z..." dispatches as a move.
            if (dispatch.StartsWith("G10"))
            {
                ProcessWorkOffsetCommand(dispatch);
            }
            else if (dispatch.StartsWith("G0") || dispatch.StartsWith("G00"))
            {
                await ProcessMoveAsync(line, isRapid: true);
            }
            else if (dispatch.StartsWith("G1") || dispatch.StartsWith("G01"))
            {
                await ProcessMoveAsync(line, isRapid: false);
            }
            else if (dispatch.StartsWith("G38"))
            {
                await ProcessProbeAsync(line);
            }
        }

        private async Task HomeAsync()
        {
            SetStatus("Home");
            await Task.Delay(HomingDurationMs);

            MachinePosition = new Vector3(0, 0, 0); // Home is at machine zero
            _isHomed = true;
            SetStatus("Idle");
        }

        private async Task ProcessMoveAsync(string line, bool isRapid)
        {
            // Parse target position
            var target = MachinePosition;
            bool isMachineCoords = line.Contains("G53");

            if (TryParseAxis(line, "X", out double x))
            {
                target = new Vector3(isMachineCoords ? x : x + WorkOffset.X, target.Y, target.Z);
            }
            if (TryParseAxis(line, "Y", out double y))
            {
                target = new Vector3(target.X, isMachineCoords ? y : y + WorkOffset.Y, target.Z);
            }
            if (TryParseAxis(line, "Z", out double z))
            {
                target = new Vector3(target.X, target.Y, isMachineCoords ? z : z + WorkOffset.Z);
            }

            // Clamp to limits
            target = ClampToLimits(target);

            // Simulate move
            await SimulateMoveAsync(target, isRapid ? RapidSpeed : FeedSpeed);
        }

        private readonly CancellationTokenSource _disposing = new();

        /// <summary>
        /// A machine that takes the line and then alarms instead of moving, so a caller
        /// waiting for the tool to arrive finds out at once rather than waiting out its
        /// budget.
        /// </summary>
        public bool AlarmOnMove { get; set; }

        private async Task SimulateMoveAsync(Vector3 target, double speed)
        {
            var start = MachinePosition;
            var distance = (target - start).Magnitude;

            if (distance < 0.001)
            {
                return;
            }

            // Queued behind the hold, not executed. Reporting Run here would overwrite the
            // door status and move the tool with the enclosure open.
            while (IsHolding && !_disposing.IsCancellationRequested)
            {
                await Task.Delay(PollIntervalMs);
            }

            if (_disposing.IsCancellationRequested) { return; }

            if (AlarmOnMove)
            {
                SetStatus(GrblProtocol.StatusAlarm);
                return;
            }

            SetStatus("Run");

            var durationMs = (int)(distance / speed * 1000);
            var steps = Math.Max(1, durationMs / PollIntervalMs);

            for (int i = 1; i <= steps; i++)
            {
                while (IsHolding && !_disposing.IsCancellationRequested)
                {
                    await Task.Delay(PollIntervalMs);
                }

                if (_disposing.IsCancellationRequested) { return; }

                var t = (double)i / steps;
                MachinePosition = start + (target - start) * t;
                await Task.Delay(PollIntervalMs);
            }

            MachinePosition = target;
            SetStatus("Idle");
        }

        private async Task ProcessProbeAsync(string line)
        {
            // Simulate probe toward workpiece
            var probeZ = WorkPosition.Z - 5.0; // Simulate hitting surface 5mm down
            await SimulateMoveAsync(
                new Vector3(MachinePosition.X, MachinePosition.Y, probeZ + WorkOffset.Z),
                FeedSpeed / 10);

            // Store probe position in machine coordinates
            LastProbePosMachine = MachinePosition;

            ProbeFinished?.Invoke(WorkPosition, true);
        }

        /// <summary>Keeps G54 in step with the work offset. This fake models no G92 or
        /// tool-length offset, so the two are equal - but they are stored separately so a
        /// test can set ExtraOffset and make them differ, the way a real machine can.</summary>
        private void SetWorkOffset(Vector3 offset)
        {
            lock (_stateLock)
            {
                _workOffset = offset;
            }
            G54Offset = offset;
        }

        /// <summary>
        /// Set true for a machine that will not take a work-offset write, as GRBL does while
        /// it is alarmed.
        /// </summary>
        public bool RefuseWorkOffsetWrites { get; set; }

        private void ProcessWorkOffsetCommand(string line)
        {
            if (RefuseWorkOffsetWrites)
            {
                return;
            }

            // G10 L2 P1 Zvalue - set work offset
            if (line.Contains("L2") && TryParseAxis(line, "Z", out double z))
            {
                SetWorkOffset(new Vector3(G54Offset.X, G54Offset.Y, z));
            }
            // G10 L20 P0 - zero work offset at current position
            else if (line.Contains("L20"))
            {
                var updated = G54Offset;

                if (line.Contains("X"))
                {
                    updated = new Vector3(MachinePosition.X, updated.Y, updated.Z);
                }
                if (line.Contains("Y"))
                {
                    updated = new Vector3(updated.X, MachinePosition.Y, updated.Z);
                }
                if (line.Contains("Z"))
                {
                    updated = new Vector3(updated.X, updated.Y, MachinePosition.Z);
                }

                SetWorkOffset(updated);
            }
        }

        private async Task RunFileAsync(CancellationToken ct)
        {
            SetStatus("Run");

            while (FilePosition < _fileLines.Count && !ct.IsCancellationRequested)
            {
                while (IsHolding && !ct.IsCancellationRequested)
                {
                    await Task.Delay(PollIntervalMs, ct);
                }

                if (ct.IsCancellationRequested)
                {
                    break;
                }

                var line = _fileLines[FilePosition];

                // Faithful to production Machine.cs: M6 is recognised the same way
                // (GCodeParser.IsM6Line - the same anchored pattern, not a substring
                // check that would also fire on M60/M65) and swallowed rather than sent
                // onward; every other line is processed as normal.
                bool isM6Line = GCodeParser.IsM6Line(line);
                if (!isM6Line)
                {
                    await ProcessCommandAsync(line);
                }

                // A tool change is not a hold preference - PauseFileOnHold governs
                // whether M0/M1/M2/M30 stop the stream, but M6 always pauses regardless,
                // the same decoupling production applies in Machine.cs's SendFile loop.
                // Classified with GCodeParser.ClassifyPauseLine - the same classifier
                // MillingController uses to react once the stream has stopped - so this
                // fake cannot pause on a line MillingController would not recognise, or
                // vice versa.
                var pauseKind = GCodeParser.ClassifyPauseLine(line);
                bool isPauseLine = pauseKind != GCodeNumbers.PauseMCode.None;
                bool shouldPause = isPauseLine && (isM6Line || PauseFileOnHold);

                // GRBL answers a program stop with a feed hold. It never sees an M6 (that
                // is swallowed before sending) and a program end simply leaves it idle, so
                // only these two report Hold.
                bool holdsOnPause = pauseKind == GCodeNumbers.PauseMCode.ProgramStop
                    || pauseKind == GCodeNumbers.PauseMCode.OptionalStop;

                // FilePosition advances past the line whether or not it paused here -
                // production does the same - so MillingController's File[FilePosition -
                // 1] lookup finds the line that just ran, not the one before it.
                FilePosition++;
                FilePositionChanged?.Invoke();

                if (shouldPause)
                {
                    Mode = OperatingMode.Manual;
                    OperatingModeChanged?.Invoke();

                    SetStatus(holdsOnPause ? "Hold:0" : "Idle");
                    return; // Pause for tool change, operator prompt, or program end
                }
            }

            Mode = OperatingMode.Manual;
            OperatingModeChanged?.Invoke();
            SetStatus("Idle");
        }

        // =========================================================================
        // Helpers
        // =========================================================================

        /// <summary>
        /// Takes a status as GRBL writes it on the wire, e.g. "Door:1", and splits it the
        /// way <see cref="Machine"/> does. Left joined, the substate predicates return the
        /// wrong answer and a door test passes for the wrong reason.
        /// </summary>
        private void SetStatus(string status)
        {
            int colon = status.IndexOf(':');
            Status = colon < 0 ? status : status.Substring(0, colon);
            StatusSubState = colon < 0 ? string.Empty : status.Substring(colon + 1);
            Interlocked.Increment(ref _statusReportCount);
            StatusChanged?.Invoke();
            StatusReceived?.Invoke($"<{status}|MPos:{MachinePosition.X:F3},{MachinePosition.Y:F3},{MachinePosition.Z:F3}>");
        }

        private static bool TryParseAxis(string line, string axis, out double value)
        {
            value = 0;
            var match = System.Text.RegularExpressions.Regex.Match(
                line, $@"{axis}(-?\d+\.?\d*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (match.Success)
            {
                return double.TryParse(match.Groups[1].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out value);
            }
            return false;
        }

        private Vector3 ClampToLimits(Vector3 pos)
        {
            return new Vector3(
                Math.Clamp(pos.X, MinPosition.X, MaxPosition.X),
                Math.Clamp(pos.Y, MinPosition.Y, MaxPosition.Y),
                Math.Clamp(pos.Z, MinPosition.Z, MaxPosition.Z)
            );
        }

        // =========================================================================
        // Test helpers
        // =========================================================================

        /// <summary>Load G-code file content.</summary>
        public void LoadFile(params string[] lines)
        {
            _fileLines = new List<string>(lines);
            FilePosition = 0;
        }

        /// <summary>Set work offset directly (for test setup).</summary>
        public void SetWorkOffset(double x, double y, double z)
        {
            SetWorkOffset(new Vector3(x, y, z));
        }

        /// <summary>Set machine position directly (for test setup).</summary>
        public void SetMachinePosition(double x, double y, double z)
        {
            MachinePosition = new Vector3(x, y, z);
        }

        /// <summary>The enclosure is open and the machine is holding.</summary>
        public void SimulateDoorOpen()
        {
            SetStatus($"{GrblProtocol.StatusDoor}:{GrblProtocol.DoorSubStateAjar}");
        }

        /// <summary>
        /// The enclosure is closed and the machine is still holding, waiting for a cycle
        /// start. A job start must recover from this state.
        /// </summary>
        public void SimulateDoorClosedAndHolding()
        {
            SetStatus($"{GrblProtocol.StatusDoor}:{GrblProtocol.DoorSubStateClosed}");
        }

        /// <summary>GRBL is restoring from the park after a cycle start.</summary>
        public void SimulateDoorResuming()
        {
            SetStatus($"{GrblProtocol.StatusDoor}:{GrblProtocol.DoorSubStateResuming}");
        }

        /// <summary>The restore has finished and the machine is back under control.</summary>
        public void SimulateDoorReleased()
        {
            SetStatus(GrblProtocol.StatusIdle);
        }

        /// <summary>Simulate alarm condition.</summary>
        public void SimulateAlarm(int code = 1)
        {
            SetStatus($"Alarm:{code}");
        }

        public void Dispose()
        {
            _disposing.Cancel();
            _runCts?.Cancel();
            _runCts?.Dispose();
            _door.Dispose();
            _disposing.Dispose();
        }
    }
}
