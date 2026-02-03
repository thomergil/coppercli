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
        // M6 detection patterns
        // =========================================================================

        private static readonly Regex M6Pattern = new(@"^\s*M0*6\s*T?(\d*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex M0Pattern = new(@"^\s*M0*0\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
            ApplyDepthAdjustment();

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

            // Stop spindle and retract Z
            _machine.SendLine(CmdSpindleOff);
            _machine.SendLine(CmdAbsolute);
            _machine.SendLine($"{CmdMachineCoords} {CmdRapidMove} Z{ToolChangeClearanceZ:F1}");

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
                    if (M0Pattern.IsMatch(_machine.File[currentLine]))
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

            ControllerLog.Log(LogSettlingPhase, settleSeconds);

            while (stableCount < settleSeconds && !ct.IsCancellationRequested)
            {
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
                    await MachineWait.EnsureMachineReadyAsync(_machine, IdleWaitTimeoutMs, ct);
                    stableCount = 0;
                }
                else
                {
                    stableCount++;
                }
            }

            ControllerLog.Log(LogSettlingComplete);
        }

        private async Task HomeIfNeededAsync(CancellationToken ct)
        {
            Phase = MillingPhase.Homing;

            ControllerLog.Log(LogHomingStart);

            // Start homing command
            _machine.IsHoming = true;
            _machine.SendLine(CmdHome);

            try
            {
                EmitProgress(new ProgressInfo(PhaseHoming, 0, MessageHoming));

                // GRBL doesn't respond to status queries during homing.
                // Wait for GRBL to start responding again (LastStatusReceived updates).
                var beforeHoming = DateTime.Now;
                ControllerLog.Log("Homing: waiting for GRBL to respond (lastStatus={0:HH:mm:ss.fff})", _machine.LastStatusReceived);

                var deadline = DateTime.Now.AddMilliseconds(HomingTimeoutMs);
                while (DateTime.Now < deadline && !ct.IsCancellationRequested)
                {
                    EmitProgress(new ProgressInfo(PhaseHoming, 0, MessageHoming));

                    if (_machine.LastStatusReceived > beforeHoming)
                    {
                        ControllerLog.Log("Homing: GRBL responding again (lastStatus={0:HH:mm:ss.fff})", _machine.LastStatusReceived);
                        break;
                    }
                    await Task.Delay(StatusPollIntervalMs, ct).ConfigureAwait(false);
                }

                // Wait for homing to complete: machine must be Idle for 1 second straight.
                ControllerLog.Log("Homing: waiting for stable idle, current={0}", _machine.Status);
                bool success = await MachineWait.WaitForStableIdleAsync(
                    _machine,
                    HomingTimeoutMs,
                    ct,
                    onPoll: () => EmitProgress(new ProgressInfo(PhaseHoming, 0, MessageHoming)));
                ControllerLog.Log("Homing: stable idle result={0}, current={1}", success, _machine.Status);

                if (!success)
                {
                    throw new InvalidOperationException(ErrorHomingFailed);
                }

                _machine.IsHomed = true;
                ControllerLog.Log(LogHomingComplete);
            }
            finally
            {
                _machine.IsHoming = false;
            }
        }

        private async Task SafetyRetractAsync(CancellationToken ct)
        {
            Phase = MillingPhase.Retracting;

            EmitProgress(new ProgressInfo(PhaseRetracting, 0, MessageRetracting));

            ControllerLog.Log(LogSafetyRetract, MillStartSafetyZ);
            await MachineWait.SafetyRetractZAsync(_machine, MillStartSafetyZ, ZHeightWaitTimeoutMs, ct);
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

        private void ApplyDepthAdjustment()
        {
            if (_depthAdjustment == 0)
            {
                ControllerLog.Log(LogNoDepthAdjustment);
                return;
            }

            // Get current work offset Z and add adjustment
            double currentOffsetZ = _machine.WorkOffset.Z;
            double newOffsetZ = currentOffsetZ + _depthAdjustment;

            _machine.SendLine($"{CmdSetWorkOffset} Z{newOffsetZ:F3}");

            ControllerLog.Log(LogDepthAdjustment, currentOffsetZ, newOffsetZ, _depthAdjustment);
        }

        private async Task MonitorMillingAsync(CancellationToken ct)
        {
            // Start file sending
            _machine.FileGoto(0);
            _machine.FileStart();
            await Task.Delay(CommandDelayMs, ct).ConfigureAwait(false);

            ControllerLog.Log(LogFileStarted, _machine.Mode, _machine.FilePosition);

            _pauseCts = new CancellationTokenSource();
            int stableIdleCount = 0;

            while (!ct.IsCancellationRequested)
            {
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
            var match = M6Pattern.Match(line);
            if (!match.Success)
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
            _machine.SendLine($"{CmdMachineCoords} {CmdRapidMove} Z{MillCompleteZ:F1}");
            await MachineWait.WaitForIdleAsync(_machine, MoveCompleteTimeoutMs, ct);

            // DEFENSE IN DEPTH: Stop all motion, clear GRBL buffer, and home to ensure
            // machine cannot continue executing commands even if there's a bug elsewhere.
            // This is critical safety - prevents runaway milling after completion.
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
