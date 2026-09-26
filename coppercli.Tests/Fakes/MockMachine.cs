#nullable enable
using System;
using System.Globalization;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using coppercli.Core.Communication;
using coppercli.Core.Util;
using static coppercli.Core.Communication.Machine;

namespace coppercli.Tests.Fakes
{
    /// <summary>
    /// An IMachine whose state a test sets directly. Nothing moves or changes on its own here
    /// apart from the door rules and the Z rapid in <see cref="ApplyRapidZ"/>; a test that needs
    /// a move to take time uses FakeMachine instead.
    /// </summary>
    public class MockMachine : IMachine, IDisposable
    {
        public OperatingMode Mode { get; set; } = OperatingMode.Manual;
        /// <inheritdoc/>
        public string StatusSubState { get; set; } = string.Empty;

        public string Status { get; set; } = GrblProtocol.StatusIdle;

        public MockMachine()
        {
            AnswerTo = line => DoorModel.Answer(line, Status);

            _door = new DoorModel(
                SetStatus,
                () => Mode == OperatingMode.SendFile
                    ? GrblProtocol.StatusRun
                    : GrblProtocol.StatusIdle);
        }
        public Vector3 WorkPosition { get; set; } = new Vector3();
        public Vector3 MachinePosition { get; set; } = new Vector3();
        public Vector3 WorkOffset { get; set; } = new Vector3();
        public Vector3 G54Offset { get; set; } = new Vector3();

        /// <summary>Set false for a machine that does not reply to $#.</summary>
        public bool WorkOffsetQuerySucceeds { get; set; } = true;
        public int WorkOffsetQueryCount { get; private set; }

        public Task<bool> RefreshWorkOffsetsAsync(int timeoutMs, CancellationToken ct = default)
        {
            WorkOffsetQueryCount++;
            return Task.FromResult(WorkOffsetQuerySucceeds);
        }
        public bool Connected { get; set; } = true;
        public bool IsHomed { get; set; }
        private int _homingCycles;
        public bool IsHoming => Volatile.Read(ref _homingCycles) > 0;
        public void BeginHoming() => Interlocked.Increment(ref _homingCycles);
        public void EndHoming() => Interlocked.Decrement(ref _homingCycles);

        /// <summary>GRBL answering the status poll, so a wait for a fresh reading ends the
        /// way it does on the machine. Set Answering false for a GRBL that has gone quiet.</summary>
        public StatusPoll Poll { get; } = new StatusPoll();

        public long StatusReportCount => Poll.Count;

        private List<string> _fileLines = new();
        public ReadOnlyCollection<string> File => _fileLines.AsReadOnly();
        public int FilePosition { get; set; }

        public List<string> SentCommands { get; } = new();

        // GRBL's door rules, shared with the other two doubles.
        private readonly DoorModel _door;
        public int FeedHoldCount { get; private set; }
        public int CycleStartCount { get; private set; }
        public int SoftResetCount { get; private set; }
        public int ProbeStartCount { get; private set; }
        public int ProbeStopCount { get; private set; }

        public Vector3 LastProbePosMachine { get; set; } = new Vector3();

        /// <summary>
        /// Raised for every line sent, so a test can change the mock's state in response to a
        /// command.
        /// </summary>
        public event Action<string>? LineSent;

        /// <inheritdoc/>
        public async Task<GrblReply> SendAsync(string line, int timeoutMs, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            GrblReply reply = Receive(line);
            if (reply.Answer == GrblAnswer.NoAnswer)
            {
                // The line was received and nothing comes back, so the caller waits out its
                // own timeout, as it would on a machine that received it and went quiet.
                await Task.Delay(timeoutMs, ct).ConfigureAwait(false);
            }

            return reply;
        }

        public void SendLine(string line) => Receive(line);

        /// <summary>
        /// Takes a line as GRBL would: records it, and acts on it only if GRBL would run it.
        /// Both ways of sending come through here.
        /// </summary>
        private GrblReply Receive(string line)
        {
            SentCommands.Add(line);

            GrblReply reply = AnswerTo(line);
            if (reply.Ran)
            {
                // $X clears the alarm the soft reset below raises on a busy machine.
                if (DoorModel.ClearsAlarm(line, Status))
                {
                    SetStatus(GrblProtocol.StatusIdle, string.Empty);
                }

                ApplyRapidZ(line);
            }

            LineSent?.Invoke(line);
            return reply;
        }

        /// <summary>
        /// What GRBL answers the lines it is sent: by default, <see cref="DoorModel.Answer"/>
        /// for this double's state. A test about the answer sets its own.
        /// </summary>
        public Func<string, GrblReply> AnswerTo { get; set; }

        /// <summary>Set true for a machine that accepts a move and never reaches the target.</summary>
        public bool IgnoreMoves { get; set; }

        /// <summary>
        /// Follows a machine-coordinate rapid in Z, so a caller waiting for the tool to reach a
        /// height sees it arrive. A line sent while the machine holds queues in GRBL's planner
        /// instead, so a retract sent at the door only runs once the hold lifts.
        /// </summary>
        private void ApplyRapidZ(string line)
        {
            if (IgnoreMoves
                || DoorModel.Holding(Status)
                || !line.StartsWith(GrblProtocol.CmdMachineCoords, StringComparison.Ordinal))
            {
                return;
            }

            int z = line.IndexOf('Z');
            if (z < 0 || !double.TryParse(
                    line.AsSpan(z + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double target))
            {
                return;
            }

            MachinePosition = new Vector3(MachinePosition.X, MachinePosition.Y, target);
        }

        public bool RefuseFileStart { get; set; }

        public bool FileStart()
        {
            if (RefuseFileStart || Mode != OperatingMode.Manual)
            {
                return false;
            }

            Mode = OperatingMode.SendFile;
            OperatingModeChanged?.Invoke();
            return true;
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
            FilePosition = line;
            FilePositionChanged?.Invoke();
        }

        /// <inheritdoc cref="DoorModel.RestoreMs"/>
        public int DoorRestoreMs
        {
            get => _door.RestoreMs;
            set => _door.RestoreMs = value;
        }

        /// <inheritdoc cref="DoorModel.IgnoreCycleStart"/>
        public bool IgnoreCycleStart
        {
            get => _door.IgnoreCycleStart;
            set => _door.IgnoreCycleStart = value;
        }

        public void Dispose() => _door.Dispose();

        public void FeedHold()
        {
            FeedHoldCount++;
            if (DoorModel.FeedHoldApplies(Status))
            {
                SetStatus(GrblProtocol.StatusHold, string.Empty);
            }
        }

        public void CycleStart()
        {
            CycleStartCount++;

            if (Status.StartsWith(GrblProtocol.StatusHold))
            {
                SetStatus(GrblProtocol.StatusRun, string.Empty);
                return;
            }

            _door.CycleStart(Status, StatusSubState);
        }

        public static MockMachine AtADoor(string subState)
        {
            var machine = new MockMachine();
            machine.SetStatus(GrblProtocol.StatusDoor, subState);
            return machine;
        }

        public void SimulateDoorOpen() =>
            SetStatus(GrblProtocol.StatusDoor, GrblProtocol.DoorSubStateAjar);

        /// <summary>
        /// The enclosure reads closed and GRBL keeps holding until it takes a cycle start.
        /// </summary>
        public void SimulateDoorClosedAndHolding() =>
            SetStatus(GrblProtocol.StatusDoor, GrblProtocol.DoorSubStateClosed);

        /// <summary>GRBL is moving the parked axes back after a cycle start.</summary>
        public void SimulateDoorResuming() =>
            SetStatus(GrblProtocol.StatusDoor, GrblProtocol.DoorSubStateResuming);

        /// <summary>
        /// Writes state and substate together, the way Machine does. Set separately, a Hold
        /// could carry a leftover door substate, which GRBL never produces.
        /// </summary>
        private void SetStatus(string state, string subState)
        {
            Status = state;
            StatusSubState = subState;
            StatusChanged?.Invoke();
        }

        public void SoftReset()
        {
            SoftResetCount++;
            Mode = OperatingMode.Manual;

            SetStatus(
                DoorModel.ResetAlarms(Status) ? GrblProtocol.StatusAlarm + ":1" : GrblProtocol.StatusIdle,
                string.Empty);
            OperatingModeChanged?.Invoke();
        }

        public bool ProbeStartSucceeds { get; set; } = true;

        public bool ProbeStart()
        {
            ProbeStartCount++;
            return ProbeStartSucceeds;
        }

        public void ProbeStop()
        {
            ProbeStopCount++;
        }

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

        public void LoadFile(params string[] lines)
        {
            _fileLines = new List<string>(lines);
            FilePosition = 0;
        }

        /// <summary>
        /// Takes a status as GRBL writes it on the wire, e.g. "Door:1", and splits it the way
        /// Machine does.
        /// </summary>
        public void SimulateStatusChange(string newStatus)
        {
            int colon = newStatus.IndexOf(':');
            SetStatus(
                colon < 0 ? newStatus : newStatus.Substring(0, colon),
                colon < 0 ? string.Empty : newStatus.Substring(colon + 1));
            StatusReceived?.Invoke($"<{newStatus}|MPos:0,0,0|WPos:0,0,0>");
        }

        public void SimulateModeChange(OperatingMode newMode)
        {
            Mode = newMode;
            OperatingModeChanged?.Invoke();
        }

        public void SimulateProbeFinished(Vector3 position, bool success)
        {
            ProbeFinished?.Invoke(position, success);
        }


        public void SimulateError(string message)
        {
            NonFatalException?.Invoke(message);
        }

        public void SimulateFileProgress(int newPosition)
        {
            FilePosition = newPosition;
            FilePositionChanged?.Invoke();
        }

        public void ResetRecording()
        {
            SentCommands.Clear();
            FeedHoldCount = 0;
            CycleStartCount = 0;
            SoftResetCount = 0;
            ProbeStartCount = 0;
            ProbeStopCount = 0;
        }

        public bool WasCommandSent(string command) => SentCommands.Contains(command);

        /// <summary>Matches each line sent against a .NET regular expression.</summary>
        public bool WasCommandSentMatching(string pattern)
        {
            foreach (var cmd in SentCommands)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(cmd, pattern))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
