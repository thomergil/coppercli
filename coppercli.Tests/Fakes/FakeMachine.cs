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
    /// Simulates motion and timing: a move takes wall-clock time and the position steps toward
    /// the target between polls. MockMachine sets state directly instead, so use this double
    /// only where a test needs a move to take time.
    /// </summary>
    public class FakeMachine : IMachine, IDisposable
    {
        // GRBL's door rules, shared with the other two doubles.
        private readonly DoorModel _door;

        public FakeMachine()
        {
            AnswerTo = line => DoorModel.Answer(line, Status);

            _door = new DoorModel(
                (state, subState) => SetStatus(
                    string.IsNullOrEmpty(subState) ? state : $"{state}:{subState}"),
                // GRBL returns to the state the door interrupted, which is Run for a file
                // that was streaming. Idle here would hide a caller that must re-assert a hold.
                () => Mode == OperatingMode.SendFile
                    ? GrblProtocol.StatusRun
                    : GrblProtocol.StatusIdle);
        }

        // mm/s
        public double RapidSpeed { get; set; } = 50.0;

        // mm/s
        public double FeedSpeed { get; set; } = 10.0;

        public int PollIntervalMs { get; set; } = 50;

        public int HomingDurationMs { get; set; } = 2000;

        // mm. A move past a limit is clamped, not rejected: this fake raises no limit alarm.
        public Vector3 MinPosition { get; set; } = new Vector3(-300, -200, -100);
        public Vector3 MaxPosition { get; set; } = new Vector3(0, 0, 0);

        /// <summary>
        /// Mirrors MachineSettings.PauseFileOnHold, including its default. It governs whether
        /// M0/M1/M2/M30 stop the stream; M6 stops it regardless, as production Machine.cs does.
        /// </summary>
        public bool PauseFileOnHold { get; set; } = true;

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
        /// The combined work offset as GRBL reports it in a status line: G54 plus
        /// <see cref="ExtraOffset"/>.
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

        /// <summary>GRBL answering the status poll, so a wait for a fresh reading ends the
        /// way it does on the machine.</summary>
        public StatusPoll Poll { get; } = new StatusPoll();

        public long StatusReportCount => Poll.Count;

        public Vector3 G54Offset { get; private set; } = new Vector3();

        /// <summary>Stands in for a live G92 or tool-length offset, so a test can make
        /// WorkOffset and G54Offset differ as they do on a real machine.</summary>
        public Vector3 ExtraOffset { get; set; } = new Vector3();

        public Task<bool> RefreshWorkOffsetsAsync(int timeoutMs, CancellationToken ct = default)
        {
            return Task.FromResult(true);
        }

        public Vector3 LastProbePosMachine { get; private set; } = new Vector3();

#pragma warning disable CS0067 // Event is never used (required by interface)
        public event Action<string>? StatusReceived;
        public event Action<Vector3, bool>? ProbeFinished;
        public event Action<string>? NonFatalException;
        public event Action<string>? Info;
        public event Action? ConnectionStateChanged;
        public event Action? StatusChanged;
        public event Action? OperatingModeChanged;
        public event Action? FilePositionChanged;
#pragma warning restore CS0067

        private readonly List<string> _sentCommands = new();

        /// <summary>
        /// Every line sent, in order. Returns a snapshot: the simulated run appends from its
        /// own task, so a test enumerating the live list would race it.
        /// </summary>
        public IReadOnlyList<string> SentCommands
        {
            get { lock (_stateLock) { return _sentCommands.ToArray(); } }
        }

        public void ClearSentCommands()
        {
            lock (_stateLock)
            {
                _sentCommands.Clear();
            }
        }

        public void SendLine(string line) => Receive(line);

        /// <summary>
        /// What GRBL answers the lines it is sent: by default, <see cref="DoorModel.Answer"/>
        /// for this double's state. A test about the answer sets its own.
        /// </summary>
        public Func<string, GrblReply> AnswerTo { get; set; }

        /// <summary>
        /// Takes a line as GRBL would: records it, and acts on it only if GRBL would run it.
        /// Both ways of sending come through here; only <see cref="SendAsync"/> waits.
        /// </summary>
        private (GrblReply Reply, Task Work) Receive(string line)
        {
            lock (_stateLock)
            {
                _sentCommands.Add(line);
            }

            GrblReply reply = AnswerTo(line);
            return (reply, reply.Ran ? ProcessCommandAsync(line) : Task.CompletedTask);
        }

        /// <inheritdoc/>
        public async Task<GrblReply> SendAsync(string line, int timeoutMs, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            var (reply, work) = Receive(line);

            try
            {
                if (reply.Answer == GrblAnswer.NoAnswer)
                {
                    // The line was taken and nothing comes back, so the caller waits out its
                    // own timeout, as it would on a machine that took it and went quiet.
                    await Task.Delay(timeoutMs, ct).ConfigureAwait(false);
                    return reply;
                }

                // GRBL answers once it has acted on the line, which for $H is the whole cycle.
                await work.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct).ConfigureAwait(false);
                return reply;
            }
            catch (TimeoutException)
            {
                return GrblReply.NoAnswer;
            }
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
            // A feed hold applies only while GRBL is running: at a door hold it is already
            // stopped, and overwriting the status would lose which door substate it was in.
            if (Status == GrblProtocol.StatusRun)
            {
                SetStatus($"{GrblProtocol.StatusHold}:0");
            }
        }

        public int CycleStartCount { get; private set; }

        public void CycleStart()
        {
            CycleStartCount++;

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
            // ProcessProbeAsync drives the probe from the G38 line, not from this call.
            return true;
        }

        public void ProbeStop()
        {
            // Nothing to stop: ProcessProbeAsync runs the probe to completion.
        }

        private async Task ProcessCommandAsync(string line)
        {
            line = line.Trim().ToUpperInvariant();

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

            // The motion word may be preceded by G53 ("this block is in machine coordinates"),
            // so dispatch on the word after that prefix. The whole line still goes down:
            // ProcessMoveAsync reads G53 from it.
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

            // GRBL drives the homing cycle from a loop that services no status query, so it
            // answers nothing until the cycle ends. A double that kept answering would let a
            // caller read the state word right through the cycle and never has to tell a
            // machine that is homing from one that refused the $H.
            Poll.Answering = false;

            try
            {
                await Task.Delay(HomingDurationMs);
            }
            finally
            {
                Poll.Answering = true;
            }

            // IsHomed is not set here. MachineWait.HomeAsync sets it, from GRBL's answer;
            // a double that set it too would let a homing test pass without that code.
            MachinePosition = new Vector3(0, 0, 0);
            SetStatus("Idle");
        }

        private async Task ProcessMoveAsync(string line, bool isRapid)
        {
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

            target = ClampToLimits(target);

            await SimulateMoveAsync(target, isRapid ? RapidSpeed : FeedSpeed);
        }

        private readonly CancellationTokenSource _disposing = new();

        /// <summary>
        /// Set true for a machine that accepts the line and then alarms instead of moving. A
        /// caller waiting for the tool to arrive sees the alarm before its timeout.
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
            // Every probe finds the surface; this fake never reports a probe that missed.
            var probeZ = WorkPosition.Z - 5.0;
            await SimulateMoveAsync(
                new Vector3(MachinePosition.X, MachinePosition.Y, probeZ + WorkOffset.Z),
                FeedSpeed / 10);

            LastProbePosMachine = MachinePosition;

            ProbeFinished?.Invoke(WorkPosition, true);
        }

        /// <summary>Writes G54 and the base work offset together. The fake applies no G92 or
        /// tool-length offset of its own, so the two stay equal until a test sets
        /// ExtraOffset.</summary>
        private void SetWorkOffset(Vector3 offset)
        {
            lock (_stateLock)
            {
                _workOffset = offset;
            }
            G54Offset = offset;
        }

        /// <summary>
        /// Set true for a machine that discards a work-offset write, as GRBL does while it is
        /// alarmed.
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

                // Matches production Machine.cs: M6 is recognized with GCodeParser.IsM6Line,
                // the anchored pattern rather than a substring check that also fires on
                // M60/M65, and is swallowed instead of sent on.
                bool isM6Line = GCodeParser.IsM6Line(line);
                if (!isM6Line)
                {
                    await ProcessCommandAsync(line);
                }

                // GCodeParser.ClassifyPauseLine is the classifier MillingController uses once
                // the stream has stopped, so this fake cannot pause on a line MillingController
                // would not recognize, or the reverse.
                var pauseKind = GCodeParser.ClassifyPauseLine(line);
                bool isPauseLine = pauseKind != GCodeNumbers.PauseMCode.None;
                bool shouldPause = isPauseLine && (isM6Line || PauseFileOnHold);

                // GRBL enters Hold on a program stop. It never receives an M6 (swallowed
                // above) and a program end leaves it idle, so only these two produce Hold.
                bool holdsOnPause = pauseKind == GCodeNumbers.PauseMCode.ProgramStop
                    || pauseKind == GCodeNumbers.PauseMCode.OptionalStop;

                // FilePosition advances past the line whether or not it paused here, as
                // production does, so MillingController's File[FilePosition - 1] lookup finds
                // the line that just ran rather than the one before it.
                FilePosition++;
                FilePositionChanged?.Invoke();

                if (shouldPause)
                {
                    Mode = OperatingMode.Manual;
                    OperatingModeChanged?.Invoke();

                    SetStatus(holdsOnPause ? "Hold:0" : "Idle");
                    return;
                }
            }

            Mode = OperatingMode.Manual;
            OperatingModeChanged?.Invoke();
            SetStatus("Idle");
        }

        /// <summary>
        /// Takes a status as GRBL writes it on the wire, e.g. "Door:1", and splits it the way
        /// <see cref="Machine"/> does. Left joined, the substate predicates read the wrong value.
        /// </summary>
        private void SetStatus(string status)
        {
            int colon = status.IndexOf(':');
            Status = colon < 0 ? status : status.Substring(0, colon);
            StatusSubState = colon < 0 ? string.Empty : status.Substring(colon + 1);
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

        public void LoadFile(params string[] lines)
        {
            _fileLines = new List<string>(lines);
            FilePosition = 0;
        }

        public void SetWorkOffset(double x, double y, double z)
        {
            SetWorkOffset(new Vector3(x, y, z));
        }

        public void SetMachinePosition(double x, double y, double z)
        {
            MachinePosition = new Vector3(x, y, z);
        }

        public void SimulateDoorOpen()
        {
            SetStatus($"{GrblProtocol.StatusDoor}:{GrblProtocol.DoorSubStateAjar}");
        }

        /// <summary>
        /// The enclosure reads closed and GRBL keeps holding until it takes a cycle start.
        /// </summary>
        public void SimulateDoorClosedAndHolding()
        {
            SetStatus($"{GrblProtocol.StatusDoor}:{GrblProtocol.DoorSubStateClosed}");
        }

        /// <summary>GRBL is moving the parked axes back after a cycle start.</summary>
        public void SimulateDoorResuming()
        {
            SetStatus($"{GrblProtocol.StatusDoor}:{GrblProtocol.DoorSubStateResuming}");
        }

        public void SimulateDoorReleased()
        {
            SetStatus(GrblProtocol.StatusIdle);
        }

        /// <summary>
        /// Reports a status the machine is no longer in, as a report sent just before a
        /// command reads once the command has changed things.
        /// </summary>
        public void SimulateStaleStatus(string status)
        {
            SetStatus(status);
        }

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
