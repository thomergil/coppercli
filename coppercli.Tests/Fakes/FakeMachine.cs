#nullable enable
using System;
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
    /// Fake CNC machine that simulates realistic behavior.
    /// Processes commands, updates positions, and simulates timing.
    /// Use for integration tests or demo mode.
    /// </summary>
    public class FakeMachine : IMachine, IDisposable
    {
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

        public Vector3 WorkOffset
        {
            get { lock (_stateLock) return _workOffset; }
            private set { lock (_stateLock) _workOffset = value; }
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

        public Vector3 LastProbePosMachine { get; private set; } = new Vector3();

        // =========================================================================
        // Events
        // =========================================================================

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

        // =========================================================================
        // IMachine implementation
        // =========================================================================

        public void SendLine(string line)
        {
            _ = ProcessCommandAsync(line);
        }

        public void FileStart()
        {
            if (Mode != OperatingMode.Manual)
            {
                return;
            }

            _runCts = new CancellationTokenSource();
            Mode = OperatingMode.SendFile;
            OperatingModeChanged?.Invoke();

            _runTask = Task.Run(() => RunFileAsync(_runCts.Token));
        }

        public void FileGoto(int line)
        {
            FilePosition = Math.Clamp(line, 0, _fileLines.Count);
            FilePositionChanged?.Invoke();
        }

        public void FeedHold()
        {
            if (Status == "Run")
            {
                SetStatus("Hold:0");
            }
        }

        public void CycleStart()
        {
            if (Status.StartsWith("Hold"))
            {
                SetStatus("Run");
            }
            else if (Status.StartsWith("Door"))
            {
                SetStatus("Idle");
            }
        }

        public void SoftReset()
        {
            _runCts?.Cancel();
            Mode = OperatingMode.Manual;
            SetStatus("Alarm:1"); // Reset causes alarm, need unlock
            OperatingModeChanged?.Invoke();
        }

        public void ProbeStart()
        {
            // FakeMachine handles probing in ProcessProbeAsync
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

            // G-code commands
            if (line.StartsWith("G0") || line.StartsWith("G00"))
            {
                await ProcessMoveAsync(line, isRapid: true);
            }
            else if (line.StartsWith("G1") || line.StartsWith("G01"))
            {
                await ProcessMoveAsync(line, isRapid: false);
            }
            else if (line.StartsWith("G10"))
            {
                ProcessWorkOffsetCommand(line);
            }
            else if (line.StartsWith("G38"))
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

        private async Task SimulateMoveAsync(Vector3 target, double speed)
        {
            var start = MachinePosition;
            var distance = (target - start).Magnitude;

            if (distance < 0.001)
            {
                return;
            }

            SetStatus("Run");

            var durationMs = (int)(distance / speed * 1000);
            var steps = Math.Max(1, durationMs / PollIntervalMs);

            for (int i = 1; i <= steps; i++)
            {
                if (Status.StartsWith("Hold"))
                {
                    // Wait while held
                    while (Status.StartsWith("Hold"))
                    {
                        await Task.Delay(PollIntervalMs);
                    }
                }

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

        private void ProcessWorkOffsetCommand(string line)
        {
            // G10 L2 P1 Zvalue - set work offset
            if (line.Contains("L2") && TryParseAxis(line, "Z", out double z))
            {
                WorkOffset = new Vector3(WorkOffset.X, WorkOffset.Y, z);
            }
            // G10 L20 P0 - zero work offset at current position
            else if (line.Contains("L20"))
            {
                if (line.Contains("X"))
                {
                    WorkOffset = new Vector3(MachinePosition.X, WorkOffset.Y, WorkOffset.Z);
                }
                if (line.Contains("Y"))
                {
                    WorkOffset = new Vector3(WorkOffset.X, MachinePosition.Y, WorkOffset.Z);
                }
                if (line.Contains("Z"))
                {
                    WorkOffset = new Vector3(WorkOffset.X, WorkOffset.Y, MachinePosition.Z);
                }
            }
        }

        private async Task RunFileAsync(CancellationToken ct)
        {
            SetStatus("Run");

            while (FilePosition < _fileLines.Count && !ct.IsCancellationRequested)
            {
                while (Status.StartsWith("Hold") && !ct.IsCancellationRequested)
                {
                    await Task.Delay(PollIntervalMs, ct);
                }

                if (ct.IsCancellationRequested)
                {
                    break;
                }

                var line = _fileLines[FilePosition];

                // Check for M6 tool change
                if (line.Contains("M6") || line.Contains("M06"))
                {
                    Mode = OperatingMode.Manual;
                    OperatingModeChanged?.Invoke();
                    SetStatus("Idle");
                    return; // Pause for tool change
                }

                // Check for M0 pause
                if (System.Text.RegularExpressions.Regex.IsMatch(line, @"\bM0{1,2}\b"))
                {
                    Mode = OperatingMode.Manual;
                    OperatingModeChanged?.Invoke();
                    SetStatus("Idle");
                    return;
                }

                await ProcessCommandAsync(line);

                FilePosition++;
                FilePositionChanged?.Invoke();
            }

            Mode = OperatingMode.Manual;
            OperatingModeChanged?.Invoke();
            SetStatus("Idle");
        }

        // =========================================================================
        // Helpers
        // =========================================================================

        private void SetStatus(string status)
        {
            Status = status;
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
            WorkOffset = new Vector3(x, y, z);
        }

        /// <summary>Set machine position directly (for test setup).</summary>
        public void SetMachinePosition(double x, double y, double z)
        {
            MachinePosition = new Vector3(x, y, z);
        }

        /// <summary>Simulate door opening.</summary>
        public void SimulateDoorOpen()
        {
            SetStatus("Door:0");
        }

        /// <summary>Simulate alarm condition.</summary>
        public void SimulateAlarm(int code = 1)
        {
            SetStatus($"Alarm:{code}");
        }

        public void Dispose()
        {
            _runCts?.Cancel();
            _runCts?.Dispose();
        }
    }
}
