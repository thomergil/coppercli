#nullable enable
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using coppercli.Core.Communication;
using static coppercli.Core.Communication.Machine;
using coppercli.Core.GCode;
using coppercli.Core.Util;
using static coppercli.Core.Util.Constants;
using static coppercli.Core.Util.GrblProtocol;
using static coppercli.Core.Controllers.ControllerConstants;
using static coppercli.Core.Util.GCodeFormat;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Controller for milling operations. Manages the complete milling workflow:
    /// settling, homing, safety retract, initialization, file streaming, and M6 detection.
    /// Both TUI and Web UI use this controller - logic is implemented here, not in UI.
    /// Uses IMachine interface to enable unit testing with mocks.
    /// </summary>
    public class MillingController : ControllerBase, IMillingController
    {
        // =========================================================================
        // Dependencies
        // =========================================================================

        private readonly IMachine _machine;

        // =========================================================================
        // State
        // =========================================================================

        private MillingPhase _phase = MillingPhase.NotStarted;
        private readonly object _phaseLock = new();
        private CancellationTokenSource? _pauseCts;

        // Snapshot of options at start (immutable during operation)
        private float _depthAdjustment;

        // How much depth adjustment is currently sitting in GRBL's G54 Z and has not
        // been taken back out again; 0 when the origin is clean. This describes the
        // machine, not the run, so it outlives both - see ResetRunState.
        private double _outstandingDepthAdjustment;

        // Cutting path tracking for visualization (rounded to avoid explosion of points)
        private readonly HashSet<(double X, double Y)> _cuttingPathSet = new();
        private readonly List<(double X, double Y)> _cuttingPath = new();
        private readonly object _cuttingPathLock = new();
        private const double CuttingPathRoundingMm = 0.1;  // Round to 0.1mm

        public MillingPhase Phase
        {
            get
            {
                lock (_phaseLock)
                {
                    return _phase;
                }
            }
            private set
            {
                lock (_phaseLock)
                {
                    _phase = value;
                }
                ControllerLog.Log(LogPhaseChange, GetType().Name, value);
            }
        }

        public int LinesCompleted => _machine.FilePosition;
        public int TotalLines => _machine.File.Count;

        public IReadOnlyList<(double X, double Y)> CuttingPath
        {
            get
            {
                lock (_cuttingPathLock)
                {
                    return _cuttingPath.ToArray();
                }
            }
        }

        // =========================================================================
        // Configuration
        // =========================================================================

        public MillingOptions Options { get; set; } = new();

        // =========================================================================
        // Events
        // =========================================================================

        public event Action<ToolChangeInfo>? ToolChangeDetected;

        // =========================================================================
        // Constructor
        // =========================================================================

        public MillingController(IMachine machine)
        {
            _machine = machine ?? throw new ArgumentNullException(nameof(machine));
        }

        // =========================================================================
        // IController implementation
        // =========================================================================

        protected override async Task RunAsync(CancellationToken ct)
        {
            // Snapshot settings at start. Everything else describing the run was cleared
            // by ResetRunState before StartAsync got here.
            _depthAdjustment = Options.DepthAdjustment;

            ControllerLog.Log(LogMillingStart, _depthAdjustment);

            // Start from a known mode. A probe run that ended just before this can leave
            // the machine in Probe mode, in which every setup command still goes through
            // but FileStart later refuses - so put it back to Manual up front rather than
            // discover the problem at the point of streaming.
            _machine.EnsureManualMode();

            // === ENCLOSURE ===
            await EnsureDoorClosedAsync(ct);

            // === SETTLING PHASE ===
            await SettleAsync(ct);

            // === HOMING (if needed) ===
            if (Options.RequireHoming)
            {
                await HomeIfNeededAsync(ct);
            }

            // === SAFETY RETRACT ===
            await SafetyRetractAsync(ct);

            // === INITIALIZE MACHINE STATE ===
            await InitializeMachineAsync(ct);

            // === APPLY DEPTH ADJUSTMENT ===
            await ApplyDepthAdjustmentAsync(ct);

            // === START MILLING ===
            TransitionTo(ControllerState.Running);
            Phase = MillingPhase.Milling;

            await MonitorMillingAsync(ct);

            // === COMPLETION ===
            // A cancelled run has not completed. Falling through to CompleteAsync would
            // try to move Paused -> Completing, which the FSM forbids, so an operator
            // who abandoned a tool change would be told the job failed.
            ct.ThrowIfCancellationRequested();

            await CompleteAsync(ct);
        }

        protected override async Task CleanupAsync()
        {
            // Always stop and reset to clear GRBL buffer and queues
            // (Mode may already be Manual if error occurred mid-file)
            await MachineWait.StopAndResetAsync(_machine);

            // Undo the depth adjustment now that motion has stopped, so an aborted run
            // does not leave the Z origin shifted for the next one.
            await RestoreDepthAdjustmentAsync();

            // Stop spindle and retract Z
            _machine.SendLine(CmdSpindleOff);
            _machine.SendLine(CmdAbsolute);
            _machine.SendLine(Inv($"{CmdMachineCoords} {CmdRapidMove} Z{ToolChangeClearanceZ:F1}"));
        }

        /// <inheritdoc/>
        protected override void ResetRunState()
        {
            lock (_cuttingPathLock)
            {
                _cuttingPathSet.Clear();
                _cuttingPath.Clear();
            }

            lock (_phaseLock)
            {
                _phase = MillingPhase.NotStarted;
            }

            _pauseCts?.Dispose();
            _pauseCts = null;

            _depthAdjustment = 0;

            // _outstandingDepthAdjustment is deliberately NOT cleared: it measures what is
            // still in GRBL's G54 Z, which no reset here can take back out. Clearing it
            // would strand that shift in the origin and cut the next job at the wrong
            // depth with nothing to say so.
        }

        public override void Pause()
        {
            if (State != ControllerState.Running)
            {
                throw new InvalidOperationException(
                    string.Format(ErrorCannotPause, State));
            }

            _machine.FeedHold();
            Phase = MillingPhase.Paused;

            // Transition before cancelling: cancelling is what wakes the monitor loop,
            // and the loop asks IsPaused the moment it wakes. Cancel first and it can
            // read the state one instruction before it changes, and stream on regardless.
            TransitionTo(ControllerState.Paused);
            _pauseCts?.Cancel();
        }

        public override void Resume()
        {
            if (State != ControllerState.Paused)
            {
                throw new InvalidOperationException(
                    string.Format(ErrorCannotResume, State));
            }

            // Skip M0 if resuming from tool change (pcb2gcode generates M6+M0 sequence)
            // The M0 is redundant since tool change already paused for user action
            if (Phase == MillingPhase.ToolChange)
            {
                int m0Line = FindRedundantM0(_machine.FilePosition);
                if (m0Line >= 0)
                {
                    ControllerLog.Log(LogSkippingM0, m0Line);
                    _machine.FileGoto(m0Line + 1);
                }
            }

            if (!RestartStreaming())
            {
                throw new InvalidOperationException(ErrorMillingDidNotStart);
            }

            _pauseCts = new CancellationTokenSource();
            Phase = MillingPhase.Milling;
            TransitionTo(ControllerState.Running);
        }

        /// <summary>
        /// Finds the note explaining a pause - pcb2gcode writes one as a comment on or
        /// just above the M0 - so the operator is told why they are being asked rather
        /// than only that they are. Returns null when the program left no explanation.
        /// </summary>
        private string? FindPauseNote(int pauseLine)
        {
            int from = Math.Max(0, pauseLine - PauseNoteSearchLines);

            for (int i = pauseLine; i >= from; i--)
            {
                string? comment = GCodeParser.ExtractToolName(_machine.File[i]);
                if (!string.IsNullOrWhiteSpace(comment))
                {
                    return comment.Trim();
                }
            }

            return null;
        }

        /// <summary>
        /// Finds the M0 that pcb2gcode emits just after an M6, looking past the comment
        /// and blank lines it puts in between. The tool change has already asked the
        /// operator to act, so that M0 would ask a second time for the same thing.
        ///
        /// Returns -1 unless the next actual instruction is the M0, so only a genuinely
        /// redundant one is skipped and a deliberate pause further down still stops.
        /// </summary>
        private int FindRedundantM0(int from)
        {
            int limit = Math.Min(_machine.File.Count, from + ToolChangeM0SearchLines);

            for (int i = Math.Max(from, 0); i < limit; i++)
            {
                string line = GCodeParser.StripComments(_machine.File[i]).Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                return GCodeParser.IsM0Line(line) ? i : -1;
            }

            return -1;
        }

        /// <summary>
        /// Makes GRBL send lines again after something stopped the stream mid-file: an
        /// explicit Pause, or an M0/M1 the operator just acknowledged. Resume() and the
        /// M0/M1 continue path both need exactly this - release a feed hold, then ask
        /// for the stream to restart - and nothing more, so it is one place rather than
        /// two copies that could drift apart.
        /// </summary>
        private bool RestartStreaming()
        {
            if (MachineWait.IsHold(_machine))
            {
                _machine.CycleStart();
            }

            if (_machine.Mode == OperatingMode.Manual)
            {
                return _machine.FileStart();
            }

            return true;
        }

        // =========================================================================
        // Workflow phases
        // =========================================================================

        /// <summary>
        /// Holds the job at a prompt until the enclosure is shut. GRBL refuses to home or
        /// move while the door is open, and it keeps holding after the door closes until
        /// something resumes it - so a job started against an open door would otherwise
        /// sit in a silent wait and then fail on a timeout that never named the door.
        ///
        /// The cycle start here is not software clearing a safety gate. The operator
        /// answered a prompt saying the door is shut; this carries out what they asked.
        /// Nothing resumes on its own, and the loop asks again if GRBL still disagrees.
        /// </summary>
        private async Task EnsureDoorClosedAsync(CancellationToken ct)
        {
            while (MachineWait.IsDoor(_machine))
            {
                Phase = MillingPhase.WaitingForOperator;

                string message = MachineWait.IsDoorOpen(_machine)
                    ? DoorOpenPrompt
                    : DoorHoldingPrompt;

                EmitProgress(new ProgressInfo(PhaseWaitingForOperator, 0, message));

                string response = await RequestUserInputAsync(
                    DoorPromptTitle,
                    message,
                    new[] { OptionContinue, OptionAbort },
                    ct).ConfigureAwait(false);

                if (response != OptionContinue)
                {
                    throw new OperationCanceledException();
                }

                if (MachineWait.IsDoorAwaitingResume(_machine))
                {
                    ControllerLog.Log("Door: operator confirmed closed, releasing the hold");
                    _machine.CycleStart();
                    await MachineWait.WaitForIdleAsync(_machine, DoorResumeTimeoutMs, ct)
                        .ConfigureAwait(false);
                }
            }
        }

        private async Task SettleAsync(CancellationToken ct)
        {
            Phase = MillingPhase.Settling;

            int settleSeconds = PostIdleSettleMs / OneSecondMs;
            int stableCount = 0;

            // Bounded: readiness can now stay false indefinitely (an open door, a
            // standing alarm), and without a deadline this loop would sit in "Settling"
            // for ever with nothing reported to the operator.
            var settleDeadline = System.Diagnostics.Stopwatch.StartNew();

            ControllerLog.Log(LogSettlingPhase, settleSeconds);

            while (stableCount < settleSeconds && !ct.IsCancellationRequested)
            {
                if (settleDeadline.ElapsedMilliseconds > SettleTimeoutMs)
                {
                    throw new InvalidOperationException(DescribeNotReady(_machine));
                }

                string statusBefore = _machine.Status;

                EmitProgress(new ProgressInfo(
                    PhaseSettling,
                    0,
                    MachineWait.IsIdle(_machine)
                        ? string.Format(MessageSettlingCountdown, settleSeconds - stableCount)
                        : MessageWaitingForIdle
                ));

                await Task.Delay(OneSecondMs, ct).ConfigureAwait(false);

                if (_machine.Status != statusBefore || !MachineWait.IsIdle(_machine))
                {
                    ControllerLog.Log(LogStatusChanged, statusBefore, _machine.Status);
                    if (!await MachineWait.EnsureMachineReadyAsync(_machine, IdleWaitTimeoutMs, ct))
                    {
                        // Door open, alarmed, or still moving - settling cannot proceed.
                        ControllerLog.Log(LogStatusChanged, statusBefore, _machine.Status);
                    }
                    stableCount = 0;
                }
                else
                {
                    stableCount++;
                }
            }

            ControllerLog.Log(LogSettlingComplete);
        }

        /// <summary>
        /// Names the actual reason the machine is not ready. "Clear any alarm" is wrong
        /// and confusing when what is really holding things up is an open enclosure.
        /// </summary>
        private static string DescribeNotReady(IMachine machine)
        {
            if (MachineWait.IsDoor(machine))
            {
                return ErrorMachineDoorOpen;
            }

            return MachineWait.IsAlarm(machine) ? ErrorMillingAlarm : ErrorMachineNotSettled;
        }

        private async Task HomeIfNeededAsync(CancellationToken ct)
        {
            Phase = MillingPhase.Homing;

            ControllerLog.Log(LogHomingStart);

            EmitProgress(new ProgressInfo(PhaseHoming, 0, MessageHoming));

            // Homing goes through MachineWait.HomeAsync - the single place that decides
            // whether the machine really homed and sets IsHomed. A second copy here used
            // to accept a rejected $H as success, and every G53 safety move afterwards
            // trusted that answer.
            var outcome = await MachineWait.HomeAsync(_machine, HomingTimeoutMs, ct);

            ControllerLog.Log("Homing: result={0}, status={1}, reason={2}",
                outcome.Success, _machine.Status, outcome.Reason ?? "(none given)");

            if (!outcome.Success)
            {
                // Say what the machine actually reported. "Homing failed" alone sends the
                // operator hunting for a fault that may not exist.
                throw new InvalidOperationException(outcome.Reason == null
                    ? ErrorHomingFailed
                    : string.Format(ErrorHomingFailedBecause, outcome.Reason));
            }

            ControllerLog.Log(LogHomingComplete);
        }

        private async Task SafetyRetractAsync(CancellationToken ct)
        {
            Phase = MillingPhase.Retracting;

            EmitProgress(new ProgressInfo(PhaseRetracting, 0, MessageRetracting));

            ControllerLog.Log(LogSafetyRetract, MillStartSafetyZ);
            bool retracted = await MachineWait.SafetyRetractZAsync(_machine, MillStartSafetyZ, ZHeightWaitTimeoutMs, ct);

            // A stop request is not a failure - let it surface as cancellation so the
            // operator is not told the retract went wrong when they pressed Stop.
            ct.ThrowIfCancellationRequested();

            if (!retracted)
            {
                // Everything after this is XY motion. If Z is not confirmed up, that
                // motion would drag the cutter across the workpiece.
                throw new InvalidOperationException(ErrorSafetyRetractFailed);
            }
        }

        private async Task InitializeMachineAsync(CancellationToken ct)
        {
            Phase = MillingPhase.Initializing;

            EmitProgress(new ProgressInfo(PhaseInitializing, 0, MessageInitializing));

            // Set absolute mode and XY plane
            _machine.SendLine(CmdAbsolute);
            _machine.SendLine(CmdPlaneXY);

            ControllerLog.Log(LogStateInit);

            await Task.Delay(CommandDelayMs, ct).ConfigureAwait(false);
        }

        private async Task ApplyDepthAdjustmentAsync(CancellationToken ct)
        {
            if (_depthAdjustment == 0)
            {
                ControllerLog.Log(LogNoDepthAdjustment);
                return;
            }

            // Ask GRBL for its stored offsets and read G54 specifically. WorkOffset is
            // the combined WCO (G54 + G92 + tool length offset), but we write back with
            // G10 L2 P1, which sets G54 alone - so restoring the combined figure into
            // the G54 slot would move the Z origin rather than put it back.
            bool offsetsKnown = await _machine.RefreshWorkOffsetsAsync(WorkOffsetQueryTimeoutMs, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            if (!offsetsKnown)
            {
                // Without a current G54 we would shift an origin we cannot see.
                throw new InvalidOperationException(ErrorWorkOffsetUnknown);
            }

            // Record the amount before writing it, so a run that dies between the two
            // still knows there is something to take back out.
            double baselineZ = _machine.G54Offset.Z;
            _outstandingDepthAdjustment = _depthAdjustment;

            double newOffsetZ = baselineZ + _depthAdjustment;

            _machine.SendLine(Inv($"{CmdSetWorkOffset} Z{newOffsetZ:F3}"));
            await Task.Delay(CommandDelayMs, ct).ConfigureAwait(false);

            ControllerLog.Log(LogDepthAdjustment, baselineZ, newOffsetZ, _depthAdjustment);
        }

        /// <summary>
        /// Takes the depth adjustment back out of the Z origin. Idempotent, and safe to
        /// call from both the success and the cleanup path.
        ///
        /// Subtracts from the CURRENT G54 rather than writing back the value captured at
        /// the start: a tool change during the job legitimately rewrites that same offset
        /// to compensate the new tool's length, and restoring an absolute snapshot would
        /// discard that compensation while reporting the job finished normally.
        /// </summary>
        private async Task RestoreDepthAdjustmentAsync()
        {
            if (_outstandingDepthAdjustment == 0)
            {
                return;
            }

            if (!await _machine.RefreshWorkOffsetsAsync(WorkOffsetQueryTimeoutMs).ConfigureAwait(false))
            {
                // Leave the amount recorded: it is still in the origin, and forgetting it
                // would leave the next run cutting against a shifted zero.
                ControllerLog.Log("Depth adjustment NOT restored: machine did not report its offsets");
                return;
            }

            double restoredZ = _machine.G54Offset.Z - _outstandingDepthAdjustment;

            _machine.SendLine(Inv($"{CmdSetWorkOffset} Z{restoredZ:F3}"));
            await Task.Delay(CommandDelayMs).ConfigureAwait(false);

            // Confirm it landed before believing it. On the abort path GRBL has just been
            // soft-reset and may still be alarmed, in which case it rejects the write -
            // and forgetting the amount anyway would strand it in the origin for good.
            if (!await _machine.RefreshWorkOffsetsAsync(WorkOffsetQueryTimeoutMs).ConfigureAwait(false)
                || Math.Abs(_machine.G54Offset.Z - restoredZ) > PositionToleranceMm)
            {
                ControllerLog.Log("Depth adjustment NOT restored: machine did not accept the new Z origin");
                return;
            }

            _outstandingDepthAdjustment = 0;
            ControllerLog.Log(LogDepthAdjustmentRestored, restoredZ);
        }

        /// <summary>
        /// Waits for evidence that the file actually began streaming. Sitting in
        /// SendFile, having consumed lines, and having run out of file are all starts;
        /// making no progress at all is the hang this guards against.
        /// </summary>
        private async Task<bool> WaitForStreamingAsync(CancellationToken ct)
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();

            while (elapsed.ElapsedMilliseconds < MotionStartTimeoutMs)
            {
                if (_machine.Mode == OperatingMode.SendFile)
                {
                    return true;
                }

                // A run can start and stop again between two polls: an M6 near the top of
                // the file is swallowed and pauses for the tool change, and a short file
                // simply finishes. Both leave SendFile behind, so the position - which
                // MonitorMillingAsync just rewound to zero - is what says lines were
                // consumed. Without this a two-tool job whose first section is short
                // enough is told it never started.
                if (_machine.FilePosition > 0)
                {
                    return true;
                }

                // An empty or fully-consumed file never enters SendFile - it is simply
                // already done, which is a valid outcome, not a hang.
                if (_machine.FilePosition >= _machine.File.Count)
                {
                    return true;
                }

                await Task.Delay(StatusPollIntervalMs, ct).ConfigureAwait(false);
            }

            return false;
        }

        private async Task MonitorMillingAsync(CancellationToken ct)
        {
            // Start file sending. If it does not begin - the machine is not in Manual
            // mode, for instance, because a prior operation left it in Probe mode - say
            // so and stop. The completion check below cannot tell "never started" from
            // "finished" (both look like idle-and-not-running), so an unstarted stream
            // used to sit at Idle for ever with nothing reported.
            _machine.FileGoto(0);

            if (!_machine.FileStart())
            {
                throw new InvalidOperationException(ErrorMillingDidNotStart);
            }

            await Task.Delay(CommandDelayMs, ct).ConfigureAwait(false);

            // Confirm GRBL actually entered the streaming state. FileStart returning true
            // means we asked; this is the machine agreeing.
            if (!await WaitForStreamingAsync(ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException(ErrorMillingDidNotStart);
            }

            ControllerLog.Log(LogFileStarted, _machine.Mode, _machine.FilePosition);

            _pauseCts = new CancellationTokenSource();
            int stableIdleCount = 0;

            while (!ct.IsCancellationRequested)
            {
                // An alarm means GRBL has stopped executing - a limit was tripped, or a
                // command was rejected. Without this the loop kept reporting progress on
                // a machine that had already stopped, and the job looked healthy.
                if (MachineWait.IsAlarm(_machine))
                {
                    ControllerLog.Log(LogMillingAlarm, _machine.Status);
                    throw new InvalidOperationException(ErrorMillingAlarm);
                }

                // Check for completion
                bool reachedEnd = _machine.FilePosition >= _machine.File.Count;
                bool isRunning = _machine.Mode == OperatingMode.SendFile;

                if (!isRunning && !IsPaused && reachedEnd)
                {
                    // Wait for stable idle to confirm completion
                    if (MachineWait.IsIdle(_machine))
                    {
                        stableIdleCount++;
                        if (stableIdleCount >= IdleSettleMs / StatusPollIntervalMs)
                        {
                            ControllerLog.Log(LogMillingComplete);
                            break;
                        }
                    }
                    else
                    {
                        stableIdleCount = 0;
                    }
                }
                else
                {
                    stableIdleCount = 0;
                }

                // React to the stream having stopped mid-file (only once the machine is
                // idle - buffered commands complete). Reaching true EOF is handled above;
                // this is for M0/M1/M2/M30/M6, which stop the stream earlier than that.
                // Idle or Hold. An M6 never reaches GRBL - it is swallowed here - so the
                // machine simply drains its buffer and goes Idle. An M0 or M1 does reach
                // GRBL, which treats it as a feed hold and reports Hold, so demanding
                // Idle would leave that pause unanswered for the rest of the job.
                bool stoppedAtPause = MachineWait.IsIdle(_machine) || MachineWait.IsHold(_machine);

                if (!isRunning && !IsPaused && !reachedEnd && stoppedAtPause)
                {
                    if (await HandlePausedStreamAsync(ct).ConfigureAwait(false))
                    {
                        break;
                    }
                }

                // Track cutting position for visualization
                TrackCuttingPosition();

                // Emit progress
                float pct = TotalLines > 0 ? (100f * LinesCompleted / TotalLines) : 0;
                EmitProgress(new ProgressInfo(
                    PhaseMilling,
                    pct,
                    string.Format(MessageMillingProgress, LinesCompleted, TotalLines),
                    LinesCompleted,
                    TotalLines
                ));

                // Snapshot the pause source for this iteration. Resume() replaces the
                // field, and a teardown clears it, so re-reading it between the link and
                // the catch filter can pair a delay with a different source - or with
                // none at all.
                var pauseCts = _pauseCts;
                if (pauseCts == null)
                {
                    break;
                }

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, pauseCts.Token);
                try
                {
                    await Task.Delay(StatusPollIntervalMs, linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (pauseCts.IsCancellationRequested)
                {
                    // Pause requested - wait until resumed
                    while (IsPaused && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(StatusPollIntervalMs, ct).ConfigureAwait(false);
                    }
                }
            }
        }

        /// <summary>
        /// Track current position if cutting (Z below threshold).
        /// Coordinates are rounded to avoid storing excessive points.
        /// </summary>
        private void TrackCuttingPosition()
        {
            var pos = _machine.WorkPosition;
            if (pos.Z >= MillCuttingDepthThreshold)
            {
                return;  // Not cutting
            }

            // Round to avoid explosion of nearly-identical points
            double x = Math.Round(pos.X / CuttingPathRoundingMm) * CuttingPathRoundingMm;
            double y = Math.Round(pos.Y / CuttingPathRoundingMm) * CuttingPathRoundingMm;
            var point = (x, y);

            lock (_cuttingPathLock)
            {
                if (_cuttingPathSet.Add(point))
                {
                    _cuttingPath.Add(point);
                }
            }
        }

        /// <summary>
        /// Reacts to the stream having stopped mid-file, by classifying the line that
        /// stopped it - the same classifier Machine used to decide the stream should
        /// pause there at all (GCodeParser.ClassifyPauseLine), so "what kind of pause is
        /// this" cannot mean two different things:
        ///   - M6: hands off to <see cref="HandleToolChangePause"/> - exactly today's
        ///     tool-change handling.
        ///   - M0/M1: the file asked the operator to look, not the code. Prompts and
        ///     waits (<see cref="HandleOperatorPauseAsync"/>).
        ///   - M2/M30: the program is over. Reported so the caller can let the run
        ///     complete normally instead of sitting here waiting for lines the file
        ///     never meant to run.
        /// Returns true once the run should be treated as complete (M2/M30).
        /// </summary>
        private async Task<bool> HandlePausedStreamAsync(CancellationToken ct)
        {
            int prevLine = _machine.FilePosition - 1;
            if (prevLine < 0 || prevLine >= _machine.File.Count)
            {
                return false;
            }

            string line = _machine.File[prevLine];
            var kind = GCodeParser.ClassifyPauseLine(line);

            switch (kind)
            {
                case GCodeNumbers.PauseMCode.ToolChange:
                    HandleToolChangePause(prevLine);
                    return false;

                case GCodeNumbers.PauseMCode.ProgramStop:
                case GCodeNumbers.PauseMCode.OptionalStop:
                    await HandleOperatorPauseAsync(prevLine, ct).ConfigureAwait(false);
                    return false;

                case GCodeNumbers.PauseMCode.ProgramEnd:
                    ControllerLog.Log(LogProgramEndDetected, prevLine);
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Detects an M6 tool change and pauses the controller:
        /// 1. Pauses, so the announcement finds the controller already paused
        /// 2. Fires ToolChangeDetected
        /// 3. A subscriber performs the tool change and calls Resume()
        /// 4. Resume() skips the redundant M0 and restarts the stream
        ///
        /// The run stays parked until Resume() or cancellation, so a subscriber is free
        /// to return at once and do the work elsewhere - both front ends do.
        /// </summary>
        private void HandleToolChangePause(int prevLine)
        {
            // Extract tool number and name from G-code (searches nearby lines for comments)
            var (toolNumber, toolName) = GCodeParser.FindToolInfo(_machine.File, prevLine);
            int toolNum = toolNumber ?? 0;

            ControllerLog.Log(LogM6Detected, prevLine, toolNum);

            var info = new ToolChangeInfo(
                toolNum,
                toolName,
                _machine.WorkPosition,
                prevLine
            );

            Phase = MillingPhase.ToolChange;

            // Announcing first leaves a window in which the run is still Running, and a
            // subscriber that finishes the tool change inside it calls Resume() on a
            // controller that was never paused, which throws from a thread with nobody to
            // catch it. Pause first, so a subscriber always finds the state it expects.
            var pauseCts = _pauseCts;
            TransitionTo(ControllerState.Paused);
            ToolChangeDetected?.Invoke(info);

            // Cancel the source this detection parked on. A subscriber that resumed
            // inline has already installed a fresh one, and cancelling that would leave
            // the monitor loop spinning on a pre-cancelled token for the rest of the job.
            pauseCts?.Cancel();
        }

        /// <summary>
        /// Detects an M0/M1 and prompts the operator to continue or stop, using the same
        /// RequestUserInputAsync primitive ToolChangeController uses for its own prompts
        /// - Running stays Running throughout (RequestUserInputAsync parks it in
        /// WaitingForUserInput and puts it back), so there is no Paused window here for a
        /// subscriber to race the way HandleToolChangePause has to guard against.
        /// </summary>
        private async Task HandleOperatorPauseAsync(int prevLine, CancellationToken ct)
        {
            Phase = MillingPhase.WaitingForOperator;

            // The machine stays exactly where the hold left it. A tool change can lift
            // clear because it tears the stream down and starts it again afterwards; a
            // feed hold resumes the motion GRBL still has buffered, from wherever the
            // machine is standing when it resumes. Lifting here and coming back would
            // have to land on the same point to the micron or cut the rest of the pass
            // from the wrong place, so the tool stays put and the prompt says so.
            string? note = FindPauseNote(prevLine);
            string message = note == null
                ? OperatorPausePrompt
                : string.Format(OperatorPausePromptWithNote, note);

            // Say so on the progress line too. Without this the last thing either UI was
            // told is "Milling", and a job waiting on a person looks like one that stalled.
            EmitProgress(new ProgressInfo(
                PhaseWaitingForOperator,
                TotalLines > 0 ? (100f * LinesCompleted / TotalLines) : 0,
                message,
                LinesCompleted,
                TotalLines));

            string response = await RequestUserInputAsync(
                OperatorPauseTitle,
                message,
                new[] { OptionContinue, OptionAbort },
                ct).ConfigureAwait(false);

            if (response != OptionContinue)
            {
                // The operator chose to stop rather than continue past the pause - end
                // the run through the same cancellation path an external Stop takes, so
                // cleanup (retract, spindle off) runs exactly once, from exactly one
                // place, whichever way the operator asked for it.
                throw new OperationCanceledException();
            }

            ControllerLog.Log(LogOperatorPauseContinued, prevLine);
            Phase = MillingPhase.Milling;

            if (!RestartStreaming())
            {
                // Without this the monitor loop finds the same stopped stream on its next
                // pass, classifies the same line again, and asks the operator the same
                // question for as long as they keep saying continue.
                throw new InvalidOperationException(ErrorMillingDidNotStart);
            }
        }

        private async Task CompleteAsync(CancellationToken ct)
        {
            TransitionTo(ControllerState.Completing);
            Phase = MillingPhase.Completing;

            // Retract Z to safe height
            _machine.SendLine(Inv($"{CmdMachineCoords} {CmdRapidMove} Z{MillCompleteZ:F1}"));
            await MachineWait.WaitForIdleAsync(_machine, MoveCompleteTimeoutMs, ct);

            // DEFENSE IN DEPTH: Stop all motion, clear GRBL buffer, and home to ensure
            // machine cannot continue executing commands even if there's a bug elsewhere.
            // This is critical safety - prevents runaway milling after completion.
            await MachineWait.SafeCompletionAsync(_machine, homeAfter: true, ct);

            // After the soft reset, not before it: a command queued beforehand would be
            // discarded by that reset and the Z origin would stay shifted.
            await RestoreDepthAdjustmentAsync();

            EmitProgress(new ProgressInfo(
                PhaseCompleting,
                100,
                MessageComplete,
                TotalLines,
                TotalLines
            ));

            TransitionTo(ControllerState.Completed);
        }
    }
}
