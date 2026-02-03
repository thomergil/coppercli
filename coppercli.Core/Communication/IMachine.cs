using System;
using System.Collections.ObjectModel;
using coppercli.Core.Util;

namespace coppercli.Core.Communication
{
    /// <summary>
    /// Interface for machine communication. Enables testing controllers
    /// without physical hardware by injecting mock implementations.
    /// </summary>
    public interface IMachine
    {
        // =========================================================================
        // Connection
        // =========================================================================

        bool Connected { get; }

        // =========================================================================
        // State
        // =========================================================================

        Machine.OperatingMode Mode { get; }
        string Status { get; }
        Vector3 WorkPosition { get; }
        Vector3 MachinePosition { get; }
        Vector3 WorkOffset { get; }

        /// <summary>Whether the machine has been homed since connection.</summary>
        bool IsHomed { get; set; }

        /// <summary>Whether homing is currently in progress.</summary>
        bool IsHoming { get; set; }

        /// <summary>Timestamp of last received status response from GRBL.</summary>
        DateTime LastStatusReceived { get; }

        // =========================================================================
        // Probing
        // =========================================================================

        /// <summary>Last probe position in machine coordinates.</summary>
        Vector3 LastProbePosMachine { get; }

        /// <summary>Start probe mode. Must be called before sending probe commands.</summary>
        void ProbeStart();

        /// <summary>Stop probe mode. Call after probing completes.</summary>
        void ProbeStop();

        // =========================================================================
        // File streaming
        // =========================================================================

        ReadOnlyCollection<string> File { get; }
        int FilePosition { get; }
        void FileStart();
        void FileGoto(int line);

        // =========================================================================
        // Commands
        // =========================================================================

        void SendLine(string line);
        void FeedHold();
        void CycleStart();
        void SoftReset();

        // =========================================================================
        // Events
        // =========================================================================

        event Action<string> StatusReceived;
        event Action<Vector3, bool> ProbeFinished;
        event Action<string> NonFatalException;
        event Action<string> Info;
        event Action ConnectionStateChanged;
        event Action StatusChanged;
        event Action OperatingModeChanged;
        event Action FilePositionChanged;
    }
}
