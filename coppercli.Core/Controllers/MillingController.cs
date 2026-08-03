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
        private bool _isPaused;

        // Snapshot of options at start (immutable during operation)
        private float _depthAdjustment;

        // Z origin as it stood before this run applied the depth adjustment, so the
        // adjustment can be undone instead of accumulating across runs.
        private double _baselineWorkOffsetZ;
        private bool _depthAdjustmentApplied;

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
            // Snapshot settings at start
            _depthAdjustment = Options.DepthAdjustment;

            // Clear cutting path for new operation
            lock (_cuttingPathLock)
            {
                _cuttingPathSet.Clear();
                _cuttingPath.Clear();
            }

            ControllerLog.Log(LogMillingStart, _depthAdjustment);

            // Start from a known mode. A probe run that ended just before this can leave
            // the machine in Probe mode, in which every setup command still goes through
            // but FileStart later refuses - so put it back to Manual up front rather than
            // discover the problem at the point of streaming.
            _machine.EnsureManualMode();

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

            ResetPhase();
        }

        /// <summary>
        /// Resets internal state for a new operation. Called by both CleanupAsync and Reset.
        /// </summary>
        private void ResetPhase()
        {
            // Clear cutting path (new milling operation will start fresh)
            lock (_cuttingPathLock)
            {
                _cuttingPathSet.Clear();
                _cuttingPath.Clear();
            }

            Phase = MillingPhase.NotStarted;
        }

        /// <summary>
        /// Override Reset to also reset the phase to NotStarted.
        /// Base class only resets State to Idle.
        /// </summary>
        public override void Reset()
        {
            base.Reset();
            ResetPhase();
        }

        public override void Pause()
        {
            if (State != ControllerState.Running)
            {
                throw new InvalidOperationException(
                    string.Format(ErrorCannotPause, State));
            }

            _machine.FeedHold();
            _isPaused = true;
            _pauseCts?.Cancel();
            Phase = MillingPhase.Paused;
            TransitionTo(ControllerState.Paused);
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
                int currentLine = _machine.FilePosition;
                if (currentLine >= 0 && currentLine < _machine.File.Count)
                {
                    if (GCodeParser.IsM0Line(_machine.File[currentLine]))
                    {
                        ControllerLog.Log(LogSkippingM0, currentLine);
                        _machine.FileGoto(currentLine + 1);
                    }
                }
            }

            // Release feed hold if in Hold state
            if (MachineWait.IsHold(_machine))
            {
                _machine.CycleStart();
            }

            // Restart file sending if in Manual mode
            if (_machine.Mode == OperatingMode.Manual)
            {
                _machine.FileStart();
            }

            _isPaused = false;
            _pauseCts = new CancellationTokenSource();
            Phase = MillingPhase.Milling;
            TransitionTo(ControllerState.Running);
        }

        // =========================================================================
        // Workflow phases
        // =========================================================================

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

            // Remember where the Z origin was so RestoreDepthAdjustmentAsync can put it
            // back. Without that, a second run would read the already-shifted offset and
            // shift it again, cutting twice as deep as the operator asked for.
            _baselineWorkOffsetZ = _machine.G54Offset.Z;
            _depthAdjustmentApplied = true;

            double newOffsetZ = _baselineWorkOffsetZ + _depthAdjustment;

            _machine.SendLine(Inv($"{CmdSetWorkOffset} Z{newOffsetZ:F3}"));
            await Task.Delay(CommandDelayMs, ct).ConfigureAwait(false);

            ControllerLog.Log(LogDepthAdjustment, _baselineWorkOffsetZ, newOffsetZ, _depthAdjustment);
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
            if (!_depthAdjustmentApplied)
            {
                return;
            }

            if (!await _machine.RefreshWorkOffsetsAsync(WorkOffsetQueryTimeoutMs).ConfigureAwait(false))
            {
                // Leave the flag set: the adjustment is still in the origin, and saying
                // otherwise would let the next run stack another one on top.
                ControllerLog.Log("Depth adjustment NOT restored: machine did not report its offsets");
                return;
            }

            double restoredZ = _machine.G54Offset.Z - _depthAdjustment;

            _machine.SendLine(Inv($"{CmdSetWorkOffset} Z{restoredZ:F3}"));
            await Task.Delay(CommandDelayMs).ConfigureAwait(false);

            // Confirm it landed before believing it. On the abort path GRBL has just been
            // soft-reset and may still be alarmed, in which case it rejects the write -
            // and clearing the flag anyway would let the next run stack another
            // adjustment on top of one that was never taken out.
            if (!await _machine.RefreshWorkOffsetsAsync(WorkOffsetQueryTimeoutMs).ConfigureAwait(false)
                || Math.Abs(_machine.G54Offset.Z - restoredZ) > PositionToleranceMm)
            {
                ControllerLog.Log("Depth adjustment NOT restored: machine did not accept the new Z origin");
                return;
            }

            _depthAdjustmentApplied = false;
            ControllerLog.Log(LogDepthAdjustmentRestored, restoredZ);
        }

        /// <summary>
        /// Waits for the machine to actually enter file-streaming, or the file to prove
        /// empty. Either is a legitimate start; sitting in neither is the hang this
        /// guards against.
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

                if (!isRunning && !_isPaused && reachedEnd)
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

                // Check for M6 tool change (only when machine is idle - buffered commands complete)
                if (!isRunning && !_isPaused && !reachedEnd && MachineWait.IsIdle(_machine))
                {
                    CheckForToolChange();
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

                // Use combined cancellation token for pause support
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _pauseCts.Token);
                try
                {
                    await Task.Delay(StatusPollIntervalMs, linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_pauseCts.IsCancellationRequested)
                {
                    // Pause requested - wait until resumed
                    while (_isPaused && !ct.IsCancellationRequested)
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
        /// Detects M6 tool change and pauses the controller.
        /// When M6 is detected:
        /// 1. Fires ToolChangeDetected event (synchronous - handler may block)
        /// 2. Pauses controller
        /// 3. Caller handles tool change then calls Resume()
        /// 4. Resume() handles M0 skip and FileStart()
        /// </summary>
        private void CheckForToolChange()
        {
            int prevLine = _machine.FilePosition - 1;
            if (prevLine < 0 || prevLine >= _machine.File.Count)
            {
                return;
            }

            string line = _machine.File[prevLine];

            // Must agree with the recogniser Machine uses to intercept M6 on the way out
            // (GrblProtocol.M6Pattern, via GCodeParser). An anchored copy here used to
            // miss "T1 M6": Machine swallowed the line but this never paused, so the job
            // carried on cutting with the previous tool.
            if (!GCodeParser.IsM6Line(line))
            {
                return;
            }

            // Extract tool number and name from G-code (searches nearby lines for comments)
            var (toolNumber, toolName) = GCodeParser.FindToolInfo(_machine.File, prevLine);
            int toolNum = toolNumber ?? 0;

            ControllerLog.Log(LogM6Detected, prevLine, toolNum);

            // Emit tool change event (synchronous - handler may block for duration of tool change)
            var info = new ToolChangeInfo(
                toolNum,
                toolName,
                _machine.WorkPosition,
                prevLine
            );

            Phase = MillingPhase.ToolChange;
            ToolChangeDetected?.Invoke(info);

            // Pause controller - Resume() will be called when tool change is complete
            // Resume() handles M0 skip and FileStart()
            _isPaused = true;
            _pauseCts?.Cancel();
            TransitionTo(ControllerState.Paused);
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
