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

        /// <summary>
        /// The number GRBL appends to a state, e.g. the "1" in "Door:1"; empty when the
        /// state carries none. Kept because it is the only thing distinguishing an open
        /// door from a closed one - see GrblProtocol.DoorSubState*.
        /// </summary>
        string StatusSubState { get; }
        Vector3 WorkPosition { get; }
        Vector3 MachinePosition { get; }
        Vector3 WorkOffset { get; }

        /// <summary>The G54 offset alone, as reported by $# - not the combined WCO.</summary>
        Vector3 G54Offset { get; }

        /// <summary>Whether the machine has been homed since connection.</summary>
        bool IsHomed { get; set; }

        /// <summary>Whether homing is currently in progress.</summary>
        bool IsHoming { get; set; }


        /// <summary>Monotonic count of status reports received.</summary>
        long StatusReportCount { get; }

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
        /// <summary>Begins streaming the loaded file. False if it could not start.</summary>
        bool FileStart();

        /// <summary>Returns to Manual mode if idling in Probe mode.</summary>
        void EnsureManualMode();
        void FileGoto(int line);

        // =========================================================================
        // Commands
        // =========================================================================

        void SendLine(string line);

        /// <summary>Requests GRBL's stored coordinate offsets and waits for the reply.
        /// False means <see cref="G54Offset"/> must not be relied on.</summary>
        System.Threading.Tasks.Task<bool> RefreshWorkOffsetsAsync(int timeoutMs, System.Threading.CancellationToken ct = default);
        void FeedHold();
        void CycleStart();
        void SoftReset();

        // =========================================================================
        // Events
        // =========================================================================

        event Action<string> StatusReceived;
        event Action<Vector3, bool> ProbeFinished;
        event Action<string> NonFatalException;

        /// <summary>Raised when GRBL refuses a command, with the reason.</summary>
        event Action<GrblRejection> CommandRejected;
        event Action<string> Info;
        event Action ConnectionStateChanged;
        event Action StatusChanged;
        event Action OperatingModeChanged;
        event Action FilePositionChanged;
    }
}
