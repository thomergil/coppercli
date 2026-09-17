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

        /// <inheritdoc/>
        protected override IMachine Machine => _machine;

        // =========================================================================
        // State
        // =========================================================================

        private MillingPhase _phase = MillingPhase.NotStarted;
        private readonly object _phaseLock = new();
        private CancellationTokenSource? _pauseCts;

        // Snapshot of options at start (immutable during operation)
        private double _depthAdjustment;

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
            await EnsureDoorClosedAsync(ct).ConfigureAwait(false);

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
            // Undo the depth adjustment between the stop and the lift, so an aborted run
            // does not leave the Z origin shifted for the next one.
            await StopAndLiftAsync(
                    SafeClearanceZ, CancelRetractTimeoutMs, RestoreDepthAdjustmentAsync)
                .ConfigureAwait(false);
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
            // would strand that shift in the origin, and the next run would measure its
            // own adjustment from a zero that had already moved.
        }

        public override void Pause()
        {
            if (State != ControllerState.Running)
            {
                throw new InvalidOperationException(
                    string.Format(ErrorCannotPause, State));
            }

            _machine.FeedHold();

            // Phase is left alone: it names the step of work, which Resume reads to decide
            // whether the M0 after an M6 is redundant. ControllerState carries paused.
            // Transition before cancelling: cancelling wakes the monitor loop, which reads
            // IsPaused immediately.
            TransitionTo(ControllerState.Paused);
            _pauseCts?.Cancel();
        }

        public override void Resume()
        {
            if (ResumeIsBlocked())
            {
                return;
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
                throw new InvalidOperationException(DescribeRestartFailure());
            }

            _pauseCts = new CancellationTokenSource();
            Phase = MillingPhase.Milling;
            TransitionTo(ControllerState.Running);
        }

        /// <summary>
        /// Finds the note explaining a pause. pcb2gcode writes one as a comment on or just
        /// above the M0, so the prompt can say why the program stopped. Returns null when
        /// there is no comment.
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
        /// and blank lines it puts in between. The tool change has already prompted the
        /// operator, so that M0 would prompt a second time for the same thing.
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
        /// Why the stream would not restart. A door hold and a machine left in probe mode
        /// need different actions, so they get different messages.
        /// </summary>
        private string DescribeRestartFailure() =>
            MachineWait.IsDoor(_machine) ? ErrorDoorBlocksResume : ErrorMillingDidNotStart;

        /// <summary>
        /// Restart the stream after a Pause or an acknowledged M0/M1. Releases a feed hold,
        /// refuses a door hold, then restarts sending. Resume() and the M0/M1 continue path
        /// both use this, so there is one copy.
        /// </summary>
        private bool RestartStreaming()
        {
            // A machine holding at the door takes lines into its planner and runs them
            // when the hold is released, so refuse now rather than queueing them.
            if (MachineWait.IsDoor(_machine))
            {
                return false;
            }

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

        private async Task SettleAsync(CancellationToken ct)
        {
            Phase = MillingPhase.Settling;

            int settleSeconds = PostIdleSettleMs / OneSecondMs;
            int stableCount = 0;

            // Bounded, because readiness can stay false indefinitely (open door, standing
            // alarm) and this loop would sit in Settling with nothing reported.
            var settleDeadline = System.Diagnostics.Stopwatch.StartNew();

            ControllerLog.Log(LogSettlingPhase, settleSeconds);

            while (stableCount < settleSeconds && !ct.IsCancellationRequested)
            {
                // Handle the door, then restart the settle budget so the operator's time
                // does not count against it.
                if (MachineWait.IsDoor(_machine))
                {
                    await EnsureDoorClosedAsync(ct).ConfigureAwait(false);
                    settleDeadline.Restart();
                    stableCount = 0;
                }

                if (settleDeadline.ElapsedMilliseconds > Options.SettleTimeoutMs)
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
        /// Why the machine did not settle. Never a door hold: the settle loop handles that.
        /// </summary>
        private static string DescribeNotReady(IMachine machine)
        {
            return MachineWait.IsAlarm(machine) ? ErrorAlarmBeforeStart : ErrorMachineNotSettled;
        }

        private async Task HomeIfNeededAsync(CancellationToken ct)
        {
            Phase = MillingPhase.Homing;

            ControllerLog.Log(LogHomingStart);

            EmitProgress(new ProgressInfo(PhaseHoming, 0, MessageHoming));

            // MachineWait.HomeAsync is the only place that decides whether the machine
            // homed and sets IsHomed.
            var outcome = await MachineWait.HomeAsync(_machine, HomingTimeoutMs, ct);

            ControllerLog.Log("Homing: result={0}, status={1}, reason={2}",
                outcome.Success, _machine.Status, outcome.Reason ?? "(none given)");

            if (!outcome.Success)
            {
                // Include what the machine reported.
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

            ControllerLog.Log(LogSafetyRetract, SafeClearanceZ);
            bool retracted = await MachineWait.SafetyRetractZAsync(_machine, SafeClearanceZ, ZHeightWaitTimeoutMs, ct);

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
            Phase = MillingPhase.ConfiguringMachine;

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

            // The origin as the operator set it: whatever an earlier run left in there is
            // taken off first. The adjustment is always measured from the zero they touched
            // off, never from the last run's adjustment, so asking for 0.05 gives 0.05
            // however the run before it ended.
            double baselineZ = _machine.G54Offset.Z - _outstandingDepthAdjustment;
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
                ReportDepthAdjustmentNotRestored("machine did not report its offsets");
                return;
            }

            double restoredZ = _machine.G54Offset.Z - _outstandingDepthAdjustment;

            _machine.SendLine(Inv($"{CmdSetWorkOffset} Z{restoredZ:F3}"));
            await Task.Delay(CommandDelayMs).ConfigureAwait(false);

            // Confirm it landed before believing it. On the abort path GRBL has just been
            // soft-reset and may still be alarmed, in which case it rejects the write -
            // and forgetting the amount anyway would strand it in the origin for good.
            if (!await _machine.RefreshWorkOffsetsAsync(WorkOffsetQueryTimeoutMs).ConfigureAwait(false)
                || Math.Abs(_machine.G54Offset.Z - restoredZ) > WorkOffsetToleranceMm)
            {
                ReportDepthAdjustmentNotRestored("machine did not accept the new Z origin");
                return;
            }

            _outstandingDepthAdjustment = 0;
            ControllerLog.Log(LogDepthAdjustmentRestored, restoredZ);
        }

        /// <summary>
        /// Tells the operator the origin is still shifted. Logged only, the run reported
        /// itself finished and every later job cut at the wrong depth with nothing said.
        /// </summary>
        private void ReportDepthAdjustmentNotRestored(string why)
        {
            ControllerLog.Log("Depth adjustment NOT restored: {0}", why);

            EmitError(new ControllerError(
                string.Format(ErrorDepthAdjustmentNotRestored, _outstandingDepthAdjustment),
                null,
                IsFatal: false));
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
            // mode, for instance because a prior operation left it in Probe mode. The
            // completion check below cannot tell "never started" from "finished", since
            // both look like idle-and-not-running, so say so and stop here.
            _machine.FileGoto(0);

            if (!_machine.FileStart())
            {
                throw new InvalidOperationException(ErrorMillingDidNotStart);
            }

            await Task.Delay(CommandDelayMs, ct).ConfigureAwait(false);

            // Confirm GRBL entered the streaming state. FileStart returning true only means
            // the command was sent.
            if (!await WaitForStreamingAsync(ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException(ErrorMillingDidNotStart);
            }

            ControllerLog.Log(LogFileStarted, _machine.Mode, _machine.FilePosition);

            _pauseCts = new CancellationTokenSource();
            int stableIdleCount = 0;

            while (!ct.IsCancellationRequested)
            {
                // An alarm means GRBL stopped executing: a limit tripped, or a command was
                // rejected. Stop reporting progress.
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

                // The enclosure opened mid-cut. The resume controls answer a feed hold, and
                // this run is still Running, so neither screen can release it.
                if (MachineWait.IsDoor(_machine))
                {
                    await EnsureDoorClosedAsync(ct).ConfigureAwait(false);
                    continue;
                }

                // React to the stream having stopped mid-file (only once the machine is
                // idle - buffered commands complete). Reaching true EOF is handled above;
                // this is for M0/M1/M2/M30/M6, which stop the stream earlier than that. An
                // M6 never reaches GRBL - it is swallowed here - so the machine drains its
                // buffer and goes Idle. An M0 or M1 does reach GRBL, which treats it as a
                // feed hold and reports Hold, so demanding Idle would leave that pause
                // unanswered for the rest of the job.
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
                    await WaitWhilePausedAsync(ct).ConfigureAwait(false);
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
        /// Handle a stream that stopped mid-file, classifying the line that stopped it with
        /// the same GCodeParser.ClassifyPauseLine that Machine uses to pause there:
        ///   - M6: <see cref="HandleToolChangePause"/>.
        ///   - M0/M1: prompt the operator and wait
        ///     (<see cref="HandleOperatorPauseAsync"/>).
        ///   - M2/M30: the program is over, so the caller completes the run instead of
        ///     waiting for more lines.
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
            // controller that was never paused, which throws on an unhandled thread. Pause
            // first, so a subscriber always finds the state it expects.
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
        /// RequestUserInputAsync that ToolChangeController uses for its own prompts. That
        /// parks the run in WaitingForUserInput and puts it back, so there is no Paused
        /// window here for a subscriber to race.
        /// </summary>
        private async Task HandleOperatorPauseAsync(int prevLine, CancellationToken ct)
        {
            // The machine stays where the hold left it. A tool change can retract because it
            // tears the stream down and restarts it; a feed hold resumes the motion GRBL
            // still has buffered, from wherever the machine is. Retracting here and returning
            // would have to land on the same point to the micron or cut the rest of the pass
            // from the wrong place, so the tool stays put and the prompt says so.
            string? note = FindPauseNote(prevLine);
            string message = note == null
                ? OperatorPausePrompt
                : string.Format(OperatorPausePromptWithNote, note);

            // Report it on the progress line too. Without this the last thing either UI
            // received is "Milling", so a job waiting on the operator looks stalled.
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

            // The operator often opens the enclosure at an M0, so handle the door before
            // restarting rather than failing the job. They have just pressed Continue, so a
            // door already closed and holding needs no second question.
            await EnsureDoorClosedAsync(ct, operatorJustAgreed: true).ConfigureAwait(false);

            Phase = MillingPhase.Milling;

            if (!RestartStreaming())
            {
                // Without this the monitor loop finds the same stopped stream on its next
                // pass, classifies the same line again, and raises the same prompt every
                // time the operator continues.
                throw new InvalidOperationException(DescribeRestartFailure());
            }
        }

        private async Task CompleteAsync(CancellationToken ct)
        {
            TransitionTo(ControllerState.Completing);

            // Retract Z to safe height. The operator is about to reach in, so an unconfirmed
            // retract fails the run rather than reporting it finished.
            if (!await RetractToSafeZAsync(SafeClearanceZ, MoveCompleteTimeoutMs).ConfigureAwait(false))
            {
                throw new InvalidOperationException(ControllerConstants.ErrorSafetyRetractFailed);
            }

            // Stop all motion, clear GRBL's buffer, and home, so a bug elsewhere cannot leave
            // the machine executing commands. Without it a completed job can keep cutting from
            // commands still queued.
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
