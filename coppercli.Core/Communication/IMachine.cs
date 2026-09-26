using System;
using System.Collections.ObjectModel;
using coppercli.Core.Util;

namespace coppercli.Core.Communication
{
    /// <summary>
    /// The machine operations a controller needs, so a controller can be tested against a
    /// stub instead of a connected machine.
    /// </summary>
    public interface IMachine
    {
        bool Connected { get; }

        Machine.OperatingMode Mode { get; }
        string Status { get; }

        /// <summary>
        /// The number GRBL appends to a state, such as the "1" in "Door:1"; empty when the
        /// state has no substate. It is the only thing that separates an open door from a
        /// closed one - see GrblProtocol.DoorSubState*.
        /// </summary>
        string StatusSubState { get; }
        Vector3 WorkPosition { get; }
        Vector3 MachinePosition { get; }
        Vector3 WorkOffset { get; }

        /// <summary>The G54 offset alone, as reported by $# - not the combined WCO.</summary>
        Vector3 G54Offset { get; }

        bool IsHomed { get; set; }

        /// <summary>
        /// True while any homing cycle runs. A Home sent during one is queued by GRBL and runs
        /// after it, so this stays true until the last <see cref="EndHoming"/>.
        /// </summary>
        bool IsHoming { get; }

        /// <summary>Only MachineWait.HomeAsync calls this, once per call, paired with EndHoming.</summary>
        void BeginHoming();

        void EndHoming();

        /// <summary>Monotonic count of status reports received.</summary>
        long StatusReportCount { get; }

        Vector3 LastProbePosMachine { get; }

        /// <summary>
        /// Takes the machine into Probe mode, which every probe move requires first.
        /// Returns false when it could not - not connected, or already in another mode -
        /// and no probe move may be sent then, because nothing would watch for the trigger.
        /// </summary>
        bool ProbeStart();

        void ProbeStop();

        ReadOnlyCollection<string> File { get; }
        int FilePosition { get; }
        /// <summary>Begins streaming the loaded file, and returns false when it could not
        /// start.</summary>
        bool FileStart();

        /// <summary>Returns to Manual mode only when idling in Probe mode; a file that is
        /// streaming is left alone.</summary>
        void EnsureManualMode();
        void FileGoto(int line);

        /// <summary>Queues a line for GRBL without waiting to hear what became of it.</summary>
        void SendLine(string line);

        /// <summary>
        /// Sends a line and completes with GRBL's answer to it. Read whether a command ran
        /// from this, not from <see cref="Status"/>: GRBL answers no status query during a
        /// homing cycle or while it restarts after a soft reset, so a status report read
        /// after a command can be older than the command.
        /// </summary>
        /// <param name="timeoutMs">
        /// How long GRBL has to answer. <see cref="GrblAnswer.Ok"/> says when that is for the
        /// line being sent.
        /// </param>
        /// <exception cref="OperationCanceledException">
        /// The caller canceled. A token already canceled puts nothing on the wire, so a
        /// cleanup path cannot start work it is about to report as refused.
        /// </exception>
        System.Threading.Tasks.Task<GrblReply> SendAsync(
            string line, int timeoutMs, System.Threading.CancellationToken ct = default);

        /// <summary>Requests GRBL's stored coordinate offsets and waits for the reply.
        /// False means <see cref="G54Offset"/> must not be relied on.</summary>
        System.Threading.Tasks.Task<bool> RefreshWorkOffsetsAsync(int timeoutMs, System.Threading.CancellationToken ct = default);
        void FeedHold();
        void CycleStart();

        /// <summary>
        /// Resets GRBL. Every line not yet answered is abandoned, and lines sent afterwards
        /// wait until GRBL is back from the reset.
        /// </summary>
        void SoftReset();

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
