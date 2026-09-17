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
    /// Simple mock implementation of IMachine for unit tests.
    /// Allows direct control of all state - no simulation logic.
    /// Use for testing controller FSM transitions and event handling.
    /// </summary>
    public class MockMachine : IMachine, IDisposable
    {
        // =========================================================================
        // State (directly settable for tests)
        // =========================================================================

        public OperatingMode Mode { get; set; } = OperatingMode.Manual;
        /// <inheritdoc/>
        public string StatusSubState { get; set; } = string.Empty;

        public string Status { get; set; } = GrblProtocol.StatusIdle;

        public MockMachine()
        {
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

        /// <summary>Set false to simulate GRBL not answering $#.</summary>
        public bool WorkOffsetQuerySucceeds { get; set; } = true;
        public int WorkOffsetQueryCount { get; private set; }

        public Task<bool> RefreshWorkOffsetsAsync(int timeoutMs, CancellationToken ct = default)
        {
            WorkOffsetQueryCount++;
            return Task.FromResult(WorkOffsetQuerySucceeds);
        }
        public bool Connected { get; set; } = true;
        public bool IsHomed { get; set; }
        public bool IsHoming { get; set; }

        public long StatusReportCount { get; set; }

        // =========================================================================
        // File state
        // =========================================================================

        private List<string> _fileLines = new();
        public ReadOnlyCollection<string> File => _fileLines.AsReadOnly();
        public int FilePosition { get; set; }

        // =========================================================================
        // Command recording (for verification)
        // =========================================================================

        public List<string> SentCommands { get; } = new();

        // GRBL's door rules, shared with the other two doubles.
        private readonly DoorModel _door;
        public int FeedHoldCount { get; private set; }
        public int CycleStartCount { get; private set; }
        public int SoftResetCount { get; private set; }
        public int ProbeStartCount { get; private set; }
        public int ProbeStopCount { get; private set; }

        // =========================================================================
        // Probing state
        // =========================================================================

        public Vector3 LastProbePosMachine { get; set; } = new Vector3();

        // =========================================================================
        // IMachine implementation
        // =========================================================================

        /// <summary>
        /// Called with every line the machine is handed, so a test can change the mock's
        /// state in response to a command, the way the real machine does.
        /// </summary>
        public event Action<string>? LineSent;

        public void SendLine(string line)
        {
            SentCommands.Add(line);

            // $X clears an alarm, which is the other half of the soft reset below: the reset
            // alarms a busy machine and StopAndResetAsync unlocks it again.
            if (DoorModel.ClearsAlarm(line, Status))
            {
                SetStatus(GrblProtocol.StatusIdle, string.Empty);
            }

            ApplyRapidZ(line);
            LineSent?.Invoke(line);
        }

        /// <summary>A machine that takes the line and never gets there.</summary>
        public bool IgnoreMoves { get; set; }

        /// <summary>
        /// Follow a machine-coordinate rapid in Z, so a caller that waits for the tool to
        /// reach a height sees it get there. A line sent while the machine holds queues in
        /// GRBL's planner instead, so a retract sent at the door runs when the hold lifts.
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

        /// <summary>Set true to simulate a machine that will not begin streaming.</summary>
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

        /// <summary>
        /// A mock GRBL already holding at the door in the given substate, for tests that
        /// start in that state.
        /// </summary>
        public static MockMachine AtADoor(string subState)
        {
            var machine = new MockMachine();
            machine.SetStatus(GrblProtocol.StatusDoor, subState);
            return machine;
        }

        /// <summary>The enclosure is open and the machine is holding.</summary>
        public void SimulateDoorOpen() =>
            SetStatus(GrblProtocol.StatusDoor, GrblProtocol.DoorSubStateAjar);

        /// <summary>
        /// The enclosure is closed and the machine is still holding, waiting for a cycle
        /// start. A job start must recover from this state.
        /// </summary>
        public void SimulateDoorClosedAndHolding() =>
            SetStatus(GrblProtocol.StatusDoor, GrblProtocol.DoorSubStateClosed);

        /// <summary>GRBL is restoring from the park after a cycle start.</summary>
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
            StatusReportCount++;
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

        /// <summary>Set false for a machine GRBL will not open a probe cycle on.</summary>
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
        // Test helpers
        // =========================================================================

        /// <summary>Load G-code lines for the file.</summary>
        public void LoadFile(params string[] lines)
        {
            _fileLines = new List<string>(lines);
            FilePosition = 0;
        }

        /// <summary>
        /// Simulate a status as GRBL writes it on the wire, e.g. "Door:1", splitting it
        /// the way Machine does.
        /// </summary>
        public void SimulateStatusChange(string newStatus)
        {
            int colon = newStatus.IndexOf(':');
            SetStatus(
                colon < 0 ? newStatus : newStatus.Substring(0, colon),
                colon < 0 ? string.Empty : newStatus.Substring(colon + 1));
            StatusReceived?.Invoke($"<{newStatus}|MPos:0,0,0|WPos:0,0,0>");
        }

        /// <summary>Simulate mode change and fire event.</summary>
        public void SimulateModeChange(OperatingMode newMode)
        {
            Mode = newMode;
            OperatingModeChanged?.Invoke();
        }

        /// <summary>Simulate probe completion.</summary>
        public void SimulateProbeFinished(Vector3 position, bool success)
        {
            ProbeFinished?.Invoke(position, success);
        }

        /// <summary>Simulate GRBL refusing a command, the way a real controller does.</summary>
        public void SimulateRejection(int code, string command, string description = "")
        {
            CommandRejected?.Invoke(new GrblRejection(code, command, description));
        }

        /// <summary>Simulate an error.</summary>
        public void SimulateError(string message)
        {
            NonFatalException?.Invoke(message);
        }

        /// <summary>Simulate file progress (advance position).</summary>
        public void SimulateFileProgress(int newPosition)
        {
            FilePosition = newPosition;
            FilePositionChanged?.Invoke();
        }

        /// <summary>Reset all recorded commands and counts.</summary>
        public void ResetRecording()
        {
            SentCommands.Clear();
            FeedHoldCount = 0;
            CycleStartCount = 0;
            SoftResetCount = 0;
        }

        /// <summary>Check if a specific command was sent.</summary>
        public bool WasCommandSent(string command) => SentCommands.Contains(command);

        /// <summary>Check if a command matching a pattern was sent.</summary>
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
