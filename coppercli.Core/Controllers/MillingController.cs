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
    /// The milling workflow: settle, home, retract, configure the machine, stream the file and
    /// catch the M6 in it. Both front ends drive this one controller and neither holds any part
    /// of the workflow itself.
    /// </summary>
    public class MillingController : ControllerBase, IMillingController
    {
        private readonly IMachine _machine;

        /// <inheritdoc/>
        protected override IMachine Machine => _machine;

        private MillingPhase _phase = MillingPhase.NotStarted;
        private readonly object _phaseLock = new();
        private CancellationTokenSource? _pauseCts;

        private double _depthAdjustment;

        // How much depth adjustment is sitting in GRBL's G54 Z and has not been taken back
        // out; 0 when the origin is clean. It describes the machine rather than the run, so
        // ResetRunState leaves it alone.
        private double _outstandingDepthAdjustment;

        private readonly HashSet<(double X, double Y)> _cuttingPathSet = new();
        private readonly List<(double X, double Y)> _cuttingPath = new();
        private readonly object _cuttingPathLock = new();
        private const double CuttingPathRoundingMm = 0.1;

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

        public MillingOptions Options { get; set; } = new();

        public event Action<ToolChangeInfo>? ToolChangeDetected;

        public MillingController(IMachine machine)
        {
            _machine = machine ?? throw new ArgumentNullException(nameof(machine));
        }

        protected override async Task RunAsync(CancellationToken ct)
        {
            // Snapshot settings at start. Everything else describing the run was cleared
            // by ResetRunState before StartAsync got here.
            _depthAdjustment = Options.DepthAdjustment;

            ControllerLog.Log(LogMillingStart, _depthAdjustment);

            // A probe run that ended just before this leaves the machine in Probe mode, where
            // every setup command still goes through but FileStart refuses. Put back to Manual
            // up front, rather than finding out at the point of streaming.
            _machine.EnsureManualMode();

            await EnsureDoorClosedAsync(ct).ConfigureAwait(false);

            await SettleAsync(ct);

            if (Options.RequireHoming)
            {
                await HomeIfNeededAsync(ct);
            }

            await SafetyRetractAsync(ct);

            await InitializeMachineAsync(ct);

            await ApplyDepthAdjustmentAsync(ct);

            TransitionTo(ControllerState.Running);
            Phase = MillingPhase.Milling;

            await MonitorMillingAsync(ct);

            // Cancellation must stop before CompleteAsync. Otherwise an abandoned tool
            // change attempts an invalid Paused -> Completing transition.
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

            // Keep _outstandingDepthAdjustment until GRBL's G54 Z is restored. Clearing
            // it here would make the next run use a shifted work origin.
        }

        public override void Pause()
        {
            if (State != ControllerState.Running)
            {
                throw new InvalidOperationException(
                    string.Format(ErrorCannotPause, State));
            }

            _machine.FeedHold();

            // Keep Phase for Resume's M0-after-M6 check; ControllerState records the pause.
            // Transition before cancelling because cancellation wakes the monitor loop.
            TransitionTo(ControllerState.Paused);
            _pauseCts?.Cancel();
        }

        public override void Resume()
        {
            if (ResumeIsBlocked())
            {
                return;
            }

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
        /// Finds the note explaining a pause: pcb2gcode writes one as a comment on or just
        /// above the M0, so the prompt can quote it. Returns null when there is no comment.
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
        /// Finds the M0 that pcb2gcode emits just after an M6, past the comment and blank
        /// lines it puts in between; the tool change already prompted, so that M0 would ask a
        /// second time. Returns -1 unless the next actual instruction is the M0, so a
        /// deliberate pause further down still stops the run.
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
        /// Releases a feed hold, refuses a door hold, then restarts sending. Resume() and the
        /// M0/M1 continue path both come through here, so the sequence is written once.
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
                // Restart the settle timeout after the operator handles the door.
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
                        // Door open, alarmed or still moving, so the settle count restarts.
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

            var outcome = await MachineWait.HomeAsync(_machine, HomingTimeoutMs, ct);

            ControllerLog.Log("Homing: result={0}, status={1}, reason={2}",
                outcome.Success, _machine.Status, outcome.Reason ?? "(none given)");

            if (!outcome.Success)
            {
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

            // Read G54 from GRBL before writing it with G10 L2 P1. WorkOffset includes
            // G92 and tool-length offsets; writing that combined value to G54 would
            // move the Z origin.
            bool offsetsKnown = await _machine.RefreshWorkOffsetsAsync(WorkOffsetQueryTimeoutMs, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            if (!offsetsKnown)
            {
                // Without a current G54 we would shift an origin we cannot see.
                throw new InvalidOperationException(ErrorWorkOffsetUnknown);
            }

            // The origin as the operator set it, with whatever an earlier run left in there
            // taken off first. The adjustment is measured from the zero they touched off, so
            // asking for 0.05 gives 0.05 however the run before it ended.
            double baselineZ = _machine.G54Offset.Z - _outstandingDepthAdjustment;
            _outstandingDepthAdjustment = _depthAdjustment;

            double newOffsetZ = baselineZ + _depthAdjustment;

            _machine.SendLine(Inv($"{CmdSetWorkOffset} Z{newOffsetZ:F3}"));
            await Task.Delay(CommandDelayMs, ct).ConfigureAwait(false);

            ControllerLog.Log(LogDepthAdjustment, baselineZ, newOffsetZ, _depthAdjustment);
        }

        /// <summary>
        /// Takes the depth adjustment back out of the Z origin; idempotent, so both the
        /// success path and the cleanup path may call it. It subtracts from the current G54
        /// rather than writing back the value captured at the start, because a tool change
        /// rewrites that same offset for the new tool's length and a snapshot would undo it.
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

            // A soft reset may leave GRBL in Alarm, where it rejects the offset write.
            // Keep the adjustment amount until the new G54 value is confirmed.
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
        /// Reports to the operator that the origin is still shifted. A log line is not enough:
        /// the run reports itself finished, and every later job would cut at the wrong depth.
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
                // MonitorMillingAsync just rewound to zero - is the evidence that lines were
                // consumed; without it a two-tool job with a short first section is reported
                // as never having started.
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
            // The completion check below cannot tell "never started" from "finished", since
            // both read as idle-and-not-running. A stream that does not begin - the machine is
            // not in Manual mode, for instance - is reported here instead.
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

                bool reachedEnd = _machine.FilePosition >= _machine.File.Count;
                bool isRunning = _machine.Mode == OperatingMode.SendFile;

                if (!isRunning && !IsPaused && reachedEnd)
                {
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

                // The enclosure opened mid-cut. The resume controls apply to a feed hold, and
                // this run is still Running, so neither screen can release it.
                if (MachineWait.IsDoor(_machine))
                {
                    await EnsureDoorClosedAsync(ct).ConfigureAwait(false);
                    continue;
                }

                // A stream stopped mid-file by M0/M1/M2/M30/M6, once the buffered commands
                // have run; true EOF is handled above. An M6 never reaches GRBL - it is
                // swallowed here - so the machine goes Idle, while an M0 or M1 does reach it
                // and reports Hold, so demanding Idle would leave that pause unanswered.
                bool stoppedAtPause = MachineWait.IsIdle(_machine) || MachineWait.IsHold(_machine);

                if (!isRunning && !IsPaused && !reachedEnd && stoppedAtPause)
                {
                    if (await HandlePausedStreamAsync(ct).ConfigureAwait(false))
                    {
                        break;
                    }
                }

                TrackCuttingPosition();

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
        /// Records the position only while Z is below the cutting threshold, rounded so a pass
        /// does not store thousands of near-identical points.
        /// </summary>
        private void TrackCuttingPosition()
        {
            var pos = _machine.WorkPosition;
            if (pos.Z >= MillCuttingDepthThreshold)
            {
                return;
            }

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
        /// Classifies the line that stopped the stream with the same
        /// GCodeParser.ClassifyPauseLine that Machine used to stop there. Returns true for
        /// M2/M30, where the program is over and the caller completes the run rather than
        /// waiting for more lines.
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
        /// Pauses, then raises ToolChangeDetected, so a subscriber finds the controller
        /// already paused. The run stays parked until Resume() or cancellation, so a subscriber
        /// may return at once and do the work elsewhere, as both front ends do.
        /// </summary>
        private void HandleToolChangePause(int prevLine)
        {
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

            // Pause before notifying subscribers; a subscriber may call Resume()
            // immediately and must see the Paused state.
            var pauseCts = _pauseCts;
            TransitionTo(ControllerState.Paused);
            ToolChangeDetected?.Invoke(info);

            // Cancel the captured source. A subscriber may have resumed inline and
            // installed a new source, which must remain active.
            pauseCts?.Cancel();
        }

        /// <summary>
        /// Ask the operator to continue or stop at M0/M1 through RequestUserInputAsync,
        /// also used for tool changes. It keeps state at WaitingForUserInput while open.
        /// </summary>
        private async Task HandleOperatorPauseAsync(int prevLine, CancellationToken ct)
        {
            // A feed hold resumes buffered motion from the tool's current position.
            // Keep the tool in place because a retract and return may shift the cut;
            // warn the operator that the tool is still down.
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
                // Use the external Stop cleanup path so retract and spindle-off run once.
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
