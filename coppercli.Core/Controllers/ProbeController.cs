#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using coppercli.Core.Communication;
using coppercli.Core.GCode;
using coppercli.Core.Util;
using static coppercli.Core.Util.GrblProtocol;
using static coppercli.Core.Controllers.ControllerConstants;
using static coppercli.Core.Util.GCodeFormat;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Controller for grid probing workflow.
    /// Handles creating grid, moving to points, probing, and recording results.
    /// Uses IMachine interface to enable unit testing with mocks.
    /// </summary>
    /// <remarks>
    /// <para><b>Probe Data Lifecycle - 4-State Model</b></para>
    ///
    /// <para>UI state is determined by the progress of the grid in hand: the one in memory,
    /// or the autosave when nothing is loaded and it describes the job in hand
    /// (<c>AppState.ReadUsableAutosave</c>). Autosave state determines Save vs Clear button
    /// behavior.</para>
    ///
    /// <para><b>State Machine (see ComputeProbeState):</b></para>
    /// <code>
    /// ┌─────────────────────────────────────────────────────────────────────────┐
    /// │  STATE     │ CONDITION              │ START BUTTON   │ SAVE/DISCARD    │
    /// ├────────────┼────────────────────────┼────────────────┼─────────────────┤
    /// │  none      │ no grid                │ disabled       │ disabled        │
    /// │  ready     │ grid, progress=0       │ [Start]        │ disabled        │
    /// │  partial   │ progress&gt;0, unmeasured  │ [Continue]     │ [Discard]*      │
    /// │  complete  │ every node measured    │ disabled       │ [Save]*/[Clear] │
    /// └─────────────────────────────────────────────────────────────────────────┘
    /// * Only if hasUnsavedData (a usable autosave exists)
    /// </code>
    ///
    /// <para><b>State Transitions:</b></para>
    /// <code>
    ///     ┌─────────────────┐
    ///     │ none            │  No grid in memory
    ///     │ [Start disabled]│
    ///     └────────┬────────┘
    ///              │ Setup Grid
    ///              ▼
    ///     ┌─────────────────┐
    ///     │ ready           │  Grid exists, progress=0
    ///     │ [Start enabled] │
    ///     └────────┬────────┘
    ///              │ Start Probing (first point creates autosave)
    ///              ▼
    ///     ┌─────────────────┐
    ///     │ partial         │◄────┐  0 &lt; progress &lt; total
    ///     │ [Continue]      │     │  (each point updates autosave)
    ///     └────────┬────────┘     │
    ///              │ Continue ────┘
    ///              │ All points probed
    ///              ▼
    ///     ┌─────────────────┐
    ///     │ complete        │  progress = total
    ///     │ [Save]/[Clear]  │
    ///     └────────┬────────┘
    ///              │ Save/Discard/Clear
    ///              ▼
    ///     ┌─────────────────┐
    ///     │ none            │
    ///     └─────────────────┘
    /// </code>
    ///
    /// <para><b>hasUnsavedData (determines Save vs Clear):</b></para>
    /// <list type="bullet">
    ///   <item>true: a usable autosave exists (data from probing) → Show Save/Discard</item>
    ///   <item>false: no usable autosave (loaded from file) → Show Clear</item>
    /// </list>
    ///
    /// <para><b>Implementation:</b></para>
    /// <list type="bullet">
    ///   <item><c>ComputeProbeState(grid)</c> - Returns state (none/ready/partial/complete).</item>
    ///   <item><c>AppState.ReadUsableAutosave()</c> - The autosave, when it was measured for
    ///   this file and origin. Answers hasUnsavedData and stands in when nothing is loaded.</item>
    ///   <item><c>Persistence.SaveProbeProgress()</c> - Updates autosave after each point.</item>
    ///   <item><c>Persistence.SaveProbeToFile(path)</c> - Moves autosave to user location.</item>
    ///   <item><c>Persistence.ClearProbeAutoSave()</c> - Deletes autosave.</item>
    /// </list>
    /// </remarks>
    public class ProbeController : ControllerBase, IProbeController
    {
        // =========================================================================
        // Dependencies
        // =========================================================================

        private readonly IMachine _machine;

        // =========================================================================
        // State
        // =========================================================================

        private ProbePhase _phase = ProbePhase.NotStarted;
        private readonly object _phaseLock = new();
        private ProbeGrid? _grid;
        private int _currentPointIndex;
        private TaskCompletionSource<(bool Success, Vector3 Position)>? _probeTcs;

        // =========================================================================
        // Properties
        // =========================================================================

        public ProbePhase Phase
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
                ControllerLog.Log(LogProbePhase, value);
                PhaseChanged?.Invoke(value);
            }
        }

        /// <summary>
        /// True while this run is tracing the grid outline. The trace moves the tool but
        /// measures nothing, so a progress display keys off the grid probe instead.
        /// </summary>
        public bool IsTracingOutline => IsActive && Phase == ProbePhase.TracingOutline;

        /// <inheritdoc/>
        public bool IsMeasuringGrid => IsActive && !IsTracingOutline;

        public ProbeGrid? Grid => _grid;

        public int PointsCompleted => _grid?.Progress ?? 0;

        public int TotalPoints => _grid?.TotalPoints ?? 0;

        public int CurrentPointIndex => _currentPointIndex;

        public ProbeOptions Options { get; set; } = new ProbeOptions();

        // =========================================================================
        // Events
        // =========================================================================

        public event Action<ProbePhase>? PhaseChanged;
        public event Action<int, Vector2, double>? PointCompleted;

        // =========================================================================
        // Constructor
        // =========================================================================

        /// <summary>
        /// Create a ProbeController.
        /// </summary>
        /// <param name="machine">Machine interface.</param>
        public ProbeController(IMachine machine)
        {
            _machine = machine ?? throw new ArgumentNullException(nameof(machine));
        }

        // =========================================================================
        // IProbeController implementation
        // =========================================================================

        public void LoadGrid(ProbeGrid grid)
        {
            if (State != ControllerState.Idle)
            {
                throw new InvalidControllerStateException(string.Format(ErrorCannotStart, State));
            }

            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _currentPointIndex = grid.Progress;

            ControllerLog.Log(LogProbeGridLoaded, grid.Progress, grid.TotalPoints);
        }

        public ProbeGrid? GetGrid() => _grid;

        // =========================================================================
        // IController implementation
        // =========================================================================

        protected override async Task RunAsync(CancellationToken ct)
        {
            if (_grid == null)
            {
                throw new InvalidOperationException(ErrorNoProbeGrid);
            }

            // A previous run may have skipped failed points, emptying the queue while
            // leaving holes in the map. Put those nodes back so this run can fill them.
            if (!_grid.HasCompleteData)
            {
                _grid.RequeueUnmeasuredPoints();
            }

            if (_grid.HasCompleteData)
            {
                ControllerLog.Log(LogProbeGridAlreadyComplete);
                TransitionTo(ControllerState.Running);
                TransitionTo(ControllerState.Completing);
                TransitionTo(ControllerState.Completed);
                return;
            }

            // Subscribe to probe events
            _machine.ProbeFinished += OnProbeFinished;

            try
            {
                // Optional: trace outline first
                if (Options.TraceOutline)
                {
                    Phase = ProbePhase.TracingOutline;
                    await TraceOutlineCoreAsync(ct);
                }

                // Safety retract to machine coords (truly safe height)
                Phase = ProbePhase.SafetyRetracting;
                bool retracted = await MachineWait.SafetyRetractZAsync(_machine, Constants.MillStartSafetyZ,
                    Constants.ZHeightWaitTimeoutMs, ct);

                ct.ThrowIfCancellationRequested();

                if (!retracted)
                {
                    // The next move is an XY rapid; without a confirmed retract it would
                    // drag the probe across the board.
                    throw new InvalidOperationException(ControllerConstants.ErrorSafetyRetractFailed);
                }

                // Move to first probe point at safe height
                SortPointsByDistance();
                if (!_grid.TryPeekNext(out var firstPoint))
                {
                    return;
                }

                var firstCoords = _grid.GetCoordinates(firstPoint.X, firstPoint.Y);
                Phase = ProbePhase.MovingToStart;
                _machine.SendLine(CmdAbsolute);
                _machine.SendLine(Inv($"{CmdRapidMove} X{firstCoords.X:F3} Y{firstCoords.Y:F3}"));
                await MachineWait.WaitForIdleAsync(_machine, Constants.MoveCompleteTimeoutMs, ct);

                // Descend to safe height (work coords)
                Phase = ProbePhase.Descending;
                await RaiseZToSafeHeightAsync(ct);

                TransitionTo(ControllerState.Running);

                // Probe all remaining points
                while (_grid.RemainingCount > 0 && !ct.IsCancellationRequested)
                {
                    await WaitWhilePausedAsync(ct);

                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    // Sort points by distance for optimal path
                    SortPointsByDistance();

                    if (!_grid.TryPeekNext(out var point))
                    {
                        break;
                    }

                    var coords = _grid.GetCoordinates(point.X, point.Y);
                    _currentPointIndex = _grid.Progress;

                    // Emit progress
                    EmitProgress(new ProgressInfo(
                        PhaseProbing,
                        (int)((_currentPointIndex / (double)_grid.TotalPoints) * 100),
                        string.Format(MessageProbeProgress, _currentPointIndex + 1, _grid.TotalPoints)));

                    // Move to point
                    Phase = ProbePhase.MovingToPoint;
                    await MoveToPointAsync(coords, ct);

                    Phase = ProbePhase.Probing;
                    var (success, position) = await ProbePointAsync(ct);

                    // Record result
                    Phase = ProbePhase.RecordingResult;
                    if (success)
                    {
                        // Judged before it is recorded. A reading we do not trust must
                        // not reach the map or the autosave, and the point must stay on
                        // the queue so resuming re-probes it rather than moving on with
                        // the bad height baked in.
                        var verdict = await RetractAndJudgeHeightAsync(point, position.Z, ct);

                        if (verdict == HeightVerdict.Cancelled)
                        {
                            break;
                        }

                        if (verdict == HeightVerdict.Remeasure)
                        {
                            // Nothing recorded, so the point is still queued and the next
                            // pass picks it up again.
                            continue;
                        }

                        _grid.RecordMeasurement(point.X, point.Y, position.Z);
                        PointCompleted?.Invoke(_currentPointIndex, coords, position.Z);
                        ControllerLog.Log(LogProbePointComplete, _currentPointIndex + 1, _grid.TotalPoints, position.Z);
                    }
                    else
                    {
                        ControllerLog.Log(LogProbePointFailed, _currentPointIndex + 1);

                        if (Options.AbortOnFail)
                        {
                            // Lift before giving up. Returning normally here skips
                            // ControllerBase's cleanup (it only runs on an exception),
                            // which would leave the tool resting on the board.
                            await RaiseZToSafeHeightAsync(ct);

                            EmitError(new ControllerError(ControllerConstants.ErrorProbeNoContact, null, true));
                            TransitionTo(ControllerState.Failed);
                            return;
                        }

                        // Skip this point: it stays unmeasured, so the map remains
                        // incomplete, and a later pass can retry it. Say so now, because
                        // applying the map will fail later and the reason is here.
                        _grid.SkipPoint(point.X, point.Y);
                        EmitError(new ControllerError(
                            string.Format(ControllerConstants.ErrorProbePointSkipped,
                                _currentPointIndex + 1, _grid.TotalPoints),
                            null,
                            IsFatal: false));

                        // The next move is an XY rapid, so the lift has to be confirmed
                        // before it, exactly as at the start of the run.
                        if (!await RaiseZToSafeHeightAsync(ct))
                        {
                            throw new InvalidOperationException(ControllerConstants.ErrorSafetyRetractFailed);
                        }
                    }
                }

                // Leave by exception rather than returning, so a cancelled run always
                // unwinds through CleanupAsync, which is where it stops and lifts.
                ct.ThrowIfCancellationRequested();

                // Final retract
                Phase = ProbePhase.FinalRetract;
                if (!await RaiseZToSafeHeightAsync(ct))
                {
                    // The operator is about to reach in, so an unconfirmed lift ends the run
                    // as failed rather than reporting a job that finished cleanly.
                    throw new InvalidOperationException(ControllerConstants.ErrorSafetyRetractFailed);
                }

                ControllerLog.Log(LogProbeComplete, _grid.TotalPoints);
                TransitionTo(ControllerState.Completing);
                TransitionTo(ControllerState.Completed);
            }
            finally
            {
                _machine.ProbeFinished -= OnProbeFinished;
            }
        }

        protected override async Task CleanupAsync()
        {
            ControllerLog.Log("ProbeController.CleanupAsync: starting, status={0}", _machine.Status);

            // Stop motion and clear GRBL's command buffer
            await MachineWait.StopAndResetAsync(_machine);

            // Then lift clear of the work. Every way a run ends reaches here, so this is the
            // one place a stopped probe retracts. Bounded on its own token, because the run's
            // is already cancelled and a stop must not appear to hang.
            using var lift = new CancellationTokenSource(Constants.CancelRetractTimeoutMs);
            bool clear;
            try
            {
                clear = await RaiseZToSafeHeightAsync(lift.Token);
            }
            catch (OperationCanceledException)
            {
                // The budget ran out mid-wait, so the tool is not confirmed clear.
                clear = false;
            }

            if (!clear)
            {
                EmitError(new ControllerError(ErrorStopRetractFailed, null, IsFatal: false));
            }

            ControllerLog.Log("ProbeController.CleanupAsync: done");
        }

        /// <inheritdoc/>
        protected override void ResetRunState()
        {
            lock (_phaseLock)
            {
                _phase = ProbePhase.NotStarted;
            }

            _probeTcs = null;

            // _grid and _currentPointIndex are deliberately NOT cleared. They describe
            // the grid, not the run: LoadGrid sets the index to the grid's progress so an
            // interrupted board resumes where it stopped, and both setup methods run
            // before StartAsync.
        }

        // =========================================================================
        // Probing workflow
        // =========================================================================

        /// <summary>
        /// Trace the probe grid outline (standalone operation).
        /// Sets phase to TracingOutline during trace and NotStarted when complete.
        /// </summary>
        public async Task TraceOutlineAsync(CancellationToken ct)
        {
            if (_grid == null)
            {
                return;
            }

            // A trace walks the tool around the board, so it is a run like any other.
            // Everything that asks whether the machine is busy reads this state.
            TransitionTo(ControllerState.Initializing);
            Phase = ProbePhase.TracingOutline;

            try
            {
                TransitionTo(ControllerState.Running);
                await TraceOutlineCoreAsync(ct);
                TransitionTo(ControllerState.Completing);
                TransitionTo(ControllerState.Completed);
            }
            catch (OperationCanceledException)
            {
                await CleanupAsync();
                TransitionTo(ControllerState.Cancelled);
                throw;
            }
            catch (Exception ex)
            {
                await CleanupAsync();
                EmitError(ex);
                TransitionTo(ControllerState.Failed);
                throw;
            }
            finally
            {
                Phase = ProbePhase.NotStarted;
            }
        }

        private async Task TraceOutlineCoreAsync(CancellationToken ct)
        {
            if (_grid == null)
            {
                return;
            }

            // Validate trace height before any movement
            if (Options.TraceHeight <= 0)
            {
                ControllerLog.Log("TraceOutline: REFUSED - trace height {0:F3} is not positive", Options.TraceHeight);
                EmitError(new ControllerError(
                    string.Format(ControllerConstants.ErrorTraceHeightUnsafe, Options.TraceHeight),
                    IsFatal: true));
                return;
            }

            double minX = _grid.Min.X;
            double minY = _grid.Min.Y;
            double maxX = _grid.Max.X;
            double maxY = _grid.Max.Y;

            ControllerLog.Log("TraceOutline: height={0:F3} feed={1:F0} grid=({2:F3},{3:F3})-({4:F3},{5:F3}) workZ={6:F3} machZ={7:F3}",
                Options.TraceHeight, Options.TraceFeed, minX, minY, maxX, maxY,
                _machine.WorkPosition.Z, _machine.MachinePosition.Z);

            // Safety retract to machine coords first
            ControllerLog.Log("TraceOutline: safety retract to machine Z={0:F1}", Constants.MillStartSafetyZ);
            if (!await MachineWait.SafetyRetractZAsync(_machine, Constants.MillStartSafetyZ,
                    Constants.ZHeightWaitTimeoutMs, ct))
            {
                ControllerLog.Log("TraceOutline: REFUSED - safety retract not confirmed");
                EmitError(new ControllerError(ControllerConstants.ErrorSafetyRetractFailed, null, IsFatal: true));
                return;
            }

            // Move to first corner at safe height
            ControllerLog.Log("TraceOutline: moving to first corner ({0:F3}, {1:F3})", minX, minY);
            _machine.SendLine(CmdAbsolute);
            _machine.SendLine(Inv($"{CmdRapidMove} X{minX:F3} Y{minY:F3}"));
            await MachineWait.WaitForIdleAsync(_machine, Constants.MoveCompleteTimeoutMs, ct);

            // Move to trace height (above work surface) and verify Z reached target
            ControllerLog.Log("TraceOutline: moving to trace height Z={0:F3} (workZ={1:F3} machZ={2:F3})",
                Options.TraceHeight, _machine.WorkPosition.Z, _machine.MachinePosition.Z);
            _machine.SendLine(CmdAbsolute);
            _machine.SendLine(Inv($"{CmdRapidMove} Z{Options.TraceHeight:F3}"));
            await MachineWait.WaitForZHeightAsync(_machine, Options.TraceHeight, Constants.MoveCompleteTimeoutMs, ct);
            await MachineWait.WaitForIdleAsync(_machine, Constants.MoveCompleteTimeoutMs, ct);
            ControllerLog.Log("TraceOutline: at trace height (workZ={0:F3} machZ={1:F3}), starting trace",
                _machine.WorkPosition.Z, _machine.MachinePosition.Z);

            // Trace remaining corners (skip first since we're already there)
            var corners = new[]
            {
                (maxX, minY),
                (maxX, maxY),
                (minX, maxY),
                (minX, minY)
            };

            foreach (var (x, y) in corners)
            {
                ct.ThrowIfCancellationRequested();

                ControllerLog.Log("TraceOutline: moving to ({0:F3}, {1:F3})", x, y);
                _machine.SendLine(CmdAbsolute);
                _machine.SendLine(Inv($"{CmdLinearMove} X{x:F3} Y{y:F3} F{Options.TraceFeed:F0}"));

                // Wait for status to change from Idle (motion started), then wait for Idle (motion complete)
                await MachineWait.WaitForStatusChangeAsync(_machine, StatusIdle, Constants.MotionStartTimeoutMs, ct);
                await MachineWait.WaitForIdleAsync(_machine, Constants.MoveCompleteTimeoutMs, ct);
            }
        }

        /// <returns>True once Z is confirmed at the safe height.</returns>
        private async Task<bool> RaiseZToSafeHeightAsync(CancellationToken ct)
        {
            _machine.SendLine(CmdAbsolute);
            _machine.SendLine(Inv($"{CmdRapidMove} Z{Options.SafeHeight:F3}"));

            // An alarm or an open door makes these return false in milliseconds with the
            // rapid still unexecuted, so the answer decides whether the tool is clear.
            bool reached = await MachineWait.WaitForZHeightAsync(
                _machine, Options.SafeHeight, Constants.ZHeightWaitTimeoutMs, ct);
            bool stopped = await MachineWait.WaitForIdleAsync(
                _machine, Constants.ZHeightWaitTimeoutMs, ct);

            return reached && stopped;
        }

        /// <summary>
        /// Lift off the point just probed, and hold the run if the height it reported
        /// does not agree with the board around it.
        ///
        /// The question is whether the tip stopped where the board is, and the height
        /// answers it directly. Timing the probe cannot: the retract and the rapid ahead
        /// of it are queued without waiting so GRBL can buffer them, and G38.2
        /// synchronises that buffer before it moves, so the reply's arrival measures all
        /// three together. The grid owns the comparison, since it owns both the lattice
        /// and the heights; this decides what the run does with the answer.
        ///
        /// How far to lift follows from that answer. A reading we trust keeps the short
        /// buffered retract, which is what makes the traverse smooth. A reading we do not
        /// stops the run for a person, and once someone has to reach into the machine,
        /// clearance matters and smoothness does not.
        /// </summary>
        private async Task<HeightVerdict> RetractAndJudgeHeightAsync(
            (int X, int Y) point, double measuredZ, CancellationToken ct)
        {
            double? deviation = _grid!.GetNeighbourDeviation(point.X, point.Y, measuredZ);

            if (Options.HeightDeviationTolerance <= 0
                || !deviation.HasValue
                || deviation.Value <= Options.HeightDeviationTolerance)
            {
                await RetractZAsync(measuredZ, ct);
                return HeightVerdict.Accepted;
            }

            // Confirmed, unlike the buffered retract above: nothing else will check the
            // tool is clear before the machine is left parked on the board. Someone is
            // about to be invited to reach into it, so an unconfirmed lift stops the job
            // rather than parking it on the workpiece.
            if (!await RaiseZToSafeHeightAsync(ct))
            {
                throw new InvalidOperationException(ControllerConstants.ErrorSafetyRetractFailed);
            }

            ControllerLog.Log(LogProbeHeightUnexpected, measuredZ, deviation.Value);
            EmitError(new ControllerError(
                string.Format(ControllerConstants.ErrorProbeHeightUnexpected, measuredZ, deviation.Value),
                null,
                IsFatal: false));

            // The operator can press Pause from a UI thread at any moment, and Paused has
            // no edge to itself. Tested and set under one lock, so a pause landing between
            // the two cannot fail the run just as they intervene.
            TryTransitionTo(ControllerState.Paused);

            await WaitWhilePausedAsync(ct);

            // Resuming says the operator dealt with whatever caused this, not that the
            // reading became good. Measure the point again.
            return ct.IsCancellationRequested ? HeightVerdict.Cancelled : HeightVerdict.Remeasure;
        }

        /// <summary>What the run does with a probed height.</summary>
        private enum HeightVerdict
        {
            /// <summary>Agrees with the board around it. Record it.</summary>
            Accepted,

            /// <summary>The operator was asked and has resumed. Probe the point again.</summary>
            Remeasure,

            /// <summary>Cancelled while the operator was deciding.</summary>
            Cancelled
        }

        private Task RetractZAsync(double currentZ, CancellationToken ct)
        {
            // Don't wait for idle - let GRBL buffer the retract with the next move for smooth motion
            double targetZ = Math.Max(currentZ + Options.MinimumHeight, Options.MinimumHeight);
            _machine.SendLine(CmdAbsolute);
            _machine.SendLine(Inv($"{CmdRapidMove} Z{targetZ:F3}"));
            return Task.CompletedTask;
        }

        private Task MoveToPointAsync(Vector2 coords, CancellationToken ct)
        {
            // Don't wait - let GRBL buffer this with the probe command for smooth motion
            _machine.SendLine(CmdAbsolute);
            _machine.SendLine(Inv($"{CmdRapidMove} X{coords.X:F3} Y{coords.Y:F3}"));
            return Task.CompletedTask;
        }

        private async Task<(bool Success, Vector3 Position)> ProbePointAsync(CancellationToken ct)
        {
            var probeTcs = new TaskCompletionSource<(bool, Vector3)>();
            _probeTcs = probeTcs;

            // Cancel the source this call created, not whatever the field points at by
            // the time cancellation arrives.
            using var registration = ct.Register(() => probeTcs.TrySetCanceled());

            _machine.ProbeStart();

            try
            {
                _machine.SendLine(CmdAbsolute);
                _machine.SendLine(Inv($"{CmdProbeToward} Z-{Options.MaxDepth:F3} F{Options.ProbeFeed:F1}"));

                return await MachineWait.AwaitReplyOrTimeoutAsync(probeTcs.Task,
                    Constants.ProbeReplyTimeoutMs, ControllerConstants.ErrorProbeTimeout, ct);
            }
            finally
            {
                _machine.ProbeStop();
            }
        }

        private void OnProbeFinished(Vector3 position, bool success)
        {
            _probeTcs?.TrySetResult((success, position));
        }


        // =========================================================================
        // Single point probing (standalone operation)
        // =========================================================================

        /// <summary>
        /// Perform a single Z probe at the current XY position.
        /// This is a standalone operation that does not affect controller state.
        /// </summary>
        public async Task<(bool Success, double ZPosition)> ProbeZSingleAsync(CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<(bool, Vector3)>();

            void OnProbeFinished(Vector3 pos, bool success)
            {
                tcs.TrySetResult((success, pos));
            }

            using var registration = ct.Register(() => tcs.TrySetCanceled());

            _machine.ProbeFinished += OnProbeFinished;
            try
            {
                _machine.ProbeStart();

                // Use relative mode so we probe DOWN from current position
                _machine.SendLine(CmdRelative);
                _machine.SendLine(Inv($"{CmdProbeToward} Z-{Options.MaxDepth:F3} F{Options.ProbeFeed:F1}"));
                _machine.SendLine(CmdAbsolute);

                var (success, position) = await MachineWait.AwaitReplyOrTimeoutAsync(tcs.Task,
                    Constants.ProbeReplyTimeoutMs, ControllerConstants.ErrorProbeTimeout, ct);

                ControllerLog.Log(LogProbeZSingle, success, position.Z);

                return (success, position.Z);
            }
            finally
            {
                _machine.ProbeFinished -= OnProbeFinished;
                _machine.ProbeStop();
            }
        }

        // =========================================================================
        // Helpers
        // =========================================================================

        private void SortPointsByDistance()
        {
            if (_grid == null)
            {
                return;
            }

            var currentPos = _machine.WorkPosition.GetXY();
            double xWeight = Options.XAxisWeight;

            _grid.OrderRemainingBy(p =>
            {
                var offset = _grid.GetCoordinates(p.X, p.Y) - currentPos;
                offset.X *= xWeight;
                return offset.Magnitude;
            });
        }

        // =========================================================================
        // Log message constants (extend ControllerConstants)
        // =========================================================================

        private const string LogProbeZSingle = "ProbeZSingle: success={0}, Z={1:F3}";
        private const string LogProbePhase = "Probe phase: {0}";
        private const string LogProbeGridCreated = "Probe grid created: {0}x{1} = {2} points";
        private const string LogProbeGridLoaded = "Probe grid loaded: {0}/{1} points complete";
        private const string LogProbeGridAlreadyComplete = "Probe grid already complete";
        private const string LogProbePointComplete = "Probe point {0}/{1} complete: Z={2:F3}";
        private const string LogProbePointFailed = "Probe point {0} failed";
        private const string LogProbeComplete = "Probing complete: {0} points";
        private const string LogProbeHeightUnexpected =
            "Probe height {0:F3} deviates {1:F3}mm from its measured neighbours";

        private const string ErrorNoProbeGrid = "No probe grid. Set one up before probing.";

        private const string PhaseProbing = "Probing";
        private const string MessageProbeProgress = "Point {0} of {1}";
    }
}
