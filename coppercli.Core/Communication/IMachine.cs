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
        /// True when a job stopped while it was cutting or paused mid-cut, by the operator or on
        /// an error, since the machine last homed; any assignment to <see cref="IsHomed"/>
        /// clears it. A stall or crash behind such a stop can skip steps GRBL does not detect, so
        /// the next job offers to home first.
        /// </summary>
        bool StoppedWhileCutting { get; }

        /// <summary>Records a stop while cutting on a homed machine; see <see cref="StoppedWhileCutting"/>.</summary>
        void NoteStoppedWhileCutting();

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

        /// <summary>GRBL's feed override, as a percentage of the feed the G-code asks for.</summary>
        int FeedOverride { get; }

        /// <summary>GRBL's rapid override, as a percentage of its top speeds.</summary>
        int RapidOverride { get; }

        /// <summary>
        /// GRBL's top speed on each axis in mm/min, from its settings as last listed by
        /// <see cref="RefreshSettingsAsync"/>; null until all three are listed, and again once
        /// disconnected.
        /// </summary>
        Vector3? TopSpeeds { get; }

        /// <summary>The lines loaded to stream: the same instance until the file is replaced.</summary>
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

        /// <summary>Requests GRBL's settings and waits for the reply. False means GRBL did not
        /// list them, and <see cref="TopSpeeds"/> holds what it listed before, if anything.</summary>
        System.Threading.Tasks.Task<bool> RefreshSettingsAsync(int timeoutMs, System.Threading.CancellationToken ct = default);
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
