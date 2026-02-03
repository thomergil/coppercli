#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    public class MockMachine : IMachine
    {
        // =========================================================================
        // State (directly settable for tests)
        // =========================================================================

        public OperatingMode Mode { get; set; } = OperatingMode.Manual;
        public string Status { get; set; } = "Idle";
        public Vector3 WorkPosition { get; set; } = new Vector3();
        public Vector3 MachinePosition { get; set; } = new Vector3();
        public Vector3 WorkOffset { get; set; } = new Vector3();
        public bool Connected { get; set; } = true;

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

        public void SendLine(string line)
        {
            SentCommands.Add(line);
        }

        public void FileStart()
        {
            Mode = OperatingMode.SendFile;
            OperatingModeChanged?.Invoke();
        }

        public void FileGoto(int line)
        {
            FilePosition = line;
            FilePositionChanged?.Invoke();
        }

        public void FeedHold()
        {
            FeedHoldCount++;
            Status = "Hold:0";
            StatusChanged?.Invoke();
        }

        public void CycleStart()
        {
            CycleStartCount++;
            if (Status.StartsWith("Hold"))
            {
                Status = "Run";
                StatusChanged?.Invoke();
            }
        }

        public void SoftReset()
        {
            SoftResetCount++;
            Mode = OperatingMode.Manual;
            Status = "Idle";
            OperatingModeChanged?.Invoke();
            StatusChanged?.Invoke();
        }

        public void ProbeStart()
        {
            ProbeStartCount++;
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

        /// <summary>Simulate a status change and fire event.</summary>
        public void SimulateStatusChange(string newStatus)
        {
            Status = newStatus;
            StatusChanged?.Invoke();
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
