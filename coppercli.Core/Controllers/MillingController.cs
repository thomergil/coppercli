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
            ControllerLog.Log(LogMillingStart);

            // A probe run that ended just before this leaves the machine in Probe mode, where
            // every setup command still goes through but FileStart refuses. Put back to Manual
            // up front, rather than finding out at the point of streaming.
            _machine.EnsureManualMode();

            await EnsureDoorClosedAsync(ct, Options.EnclosureConfirmed).ConfigureAwait(false);

            await SettleAsync(ct);

            // Decided after the settle, so the run homes if a restart during the settle lost the
            // position.
            if (Options.HomeFirst || !_machine.IsHomed)
            {
                await HomeIfNeededAsync(ct);
            }

            await SafetyRetractAsync(ct);

            await InitializeMachineAsync(ct);

            TransitionTo(ControllerState.Running);

            await MonitorMillingAsync(ct);

            // Cancellation must stop before CompleteAsync. Otherwise an abandoned tool
            // change attempts an invalid Paused -> Completing transition.
            ct.ThrowIfCancellationRequested();

            await CompleteAsync(ct);
        }

        protected override async Task CleanupAsync()
        {
            // A stop or an error while cutting may follow a crash or stall that skipped steps
            // GRBL does not detect; the next job then offers to home first. Cutting is the
            // Milling phase while the run streams or the operator has paused it: at a program
            // pause or a door prompt nothing was cutting, and Completing comes after the last
            // line. State still reads as it was here, because the run moves to Cancelled or
            // Failed only after this cleanup.
            if (Phase == MillingPhase.Milling
                && (State == ControllerState.Running || State == ControllerState.Paused))
            {
                _machine.NoteStoppedWhileCutting();
            }

            await StopAndLiftAsync(SafeClearanceZ, CancelRetractTimeoutMs).ConfigureAwait(false);
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
            MachineWait.GetDoorRefusal(_machine) ?? ErrorMillingDidNotStart;

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

            // Both callers reach here holding a machine this controller itself held - Resume
            // after its own feed hold, and the continue path after an M0/M1. Re-reading
            // Status to confirm that would define the same fact a second time, from a
            // reading that can still predate the hold; a cycle start to a machine that is
            // not holding does nothing.
            _machine.CycleStart();

            if (_machine.Mode == OperatingMode.Manual)
            {
                return _machine.FileStart();
            }

            return true;
        }

        /// <summary>
        /// Waits until the machine has read Idle without a break for IdleSettleMs, so the run
        /// does not start on a machine still finishing a jog or a move. At a door hold it asks
        /// the operator through EnsureDoorClosedAsync; any other state that will not settle (an
        /// alarm, sleep) is waited out until the settle timeout, then reported.
        /// </summary>
        private async Task SettleAsync(CancellationToken ct)
        {
            Phase = MillingPhase.Settling;
            ControllerLog.Log(LogSettlingPhase, IdleSettleMs);
            EmitProgress(new ProgressInfo(PhaseSettling, 0, MessageWaitingForIdle));

            // Bounded, because the machine can stay out of Idle indefinitely (a standing alarm,
            // sleep) and the run would sit in Settling with nothing reported.
            var settleDeadline = System.Diagnostics.Stopwatch.StartNew();

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // Restarted after the door prompt, so the operator's time at the door does
                // not count toward the timeout.
                if (MachineWait.IsDoor(_machine))
                {
                    await EnsureDoorClosedAsync(ct).ConfigureAwait(false);
                    settleDeadline.Restart();
                }

                int remainingMs = Options.SettleTimeoutMs - (int)settleDeadline.ElapsedMilliseconds;
                if (remainingMs <= 0)
                {
                    throw new InvalidOperationException(DescribeNotReady(_machine));
                }

                if (await MachineWait.WaitForSteadyIdleAsync(_machine, IdleSettleMs, remainingMs, ct)
                    .ConfigureAwait(false))
                {
                    break;
                }
            }

            ControllerLog.Log(LogSettlingComplete);
        }

        /// <summary>
        /// Why the machine did not settle. Never a door hold: the settle loop handles that.
        /// </summary>
        private static string DescribeNotReady(IMachine machine)
        {
            if (MachineWait.IsAlarm(machine))
            {
                return ErrorAlarmBeforeStart;
            }

            return MachineWait.IsUnavailable(machine) ? ErrorMachineNotResponding : ErrorMachineNotSettled;
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
                throw new InvalidOperationException(outcome.FailureMessage);
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
            if (!await MachineWait.WaitForStreamingAsync(_machine, MotionStartTimeoutMs, ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException(ErrorMillingDidNotStart);
            }

            ControllerLog.Log(LogFileStarted, _machine.Mode, _machine.FilePosition);

            // Set once the file is streaming, so a stop before any line is sent is not a stop
            // while cutting.
            Phase = MillingPhase.Milling;

            _pauseCts = new CancellationTokenSource();
            var steadyIdle = new SteadyIdle(IdleSettleMs);

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
                    if (steadyIdle.IsSteady(_machine))
                    {
                        ControllerLog.Log(LogMillingComplete);
                        break;
                    }
                }
                else
                {
                    steadyIdle.Interrupt();
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
