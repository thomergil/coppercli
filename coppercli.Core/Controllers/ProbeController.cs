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
    /// Drives a grid probe: create or load the grid, move to each point, probe, record.
    /// </summary>
    /// <remarks>
    /// <para><b>Probe data lifecycle.</b> What the screens offer follows how much of the
    /// current grid is measured - the grid in memory, or the autosave when nothing is loaded
    /// and it was measured for this file and origin (<c>AppState.ReadUsableAutosave</c>).
    /// <c>ProbeGrid.State</c>, and <c>ProbeGrid.StateOf(grid)</c> where there may be no grid,
    /// give that answer: none, ready, partial or complete. Nothing else computes it; the web
    /// server only names the wire spelling.</para>
    ///
    /// <para>Progress counts points taken off the queue, and a skipped probe takes one off
    /// too, so a map with a skipped point reaches progress = total without being complete. Ask
    /// whether every node is measured, never whether the count matches.</para>
    ///
    /// <para>The first probed point writes the autosave and every point after it updates it
    /// (<c>Persistence.SaveProbeProgress</c>). Whether a usable autosave exists is what decides
    /// Save/Discard against Clear: <c>Persistence.SaveProbeToFile(path)</c> writes the map to
    /// the operator's file, adopts it if it existed only in the autosave, then deletes the
    /// autosave, while <c>Persistence.ClearProbeAutoSave()</c> deletes it on its own.</para>
    /// </remarks>
    public class ProbeController : ControllerBase, IProbeController
    {
        private readonly IMachine _machine;

        /// <inheritdoc/>
        protected override IMachine Machine => _machine;

        private ProbePhase _phase = ProbePhase.NotStarted;
        private readonly object _phaseLock = new();
        private ProbeGrid? _grid;
        private int _currentPointIndex;
        private TaskCompletionSource<(bool Success, Vector3 Position)>? _probeTcs;

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
        /// The trace moves the tool but measures nothing, so progress displays read the grid
        /// probe instead. Derived from IsRunInProgress rather than IsActive, because a run
        /// waiting on the enclosure prompt still has the machine and must not read as finished.
        /// </summary>
        public bool IsTracingOutline => IsRunInProgress && Phase == ProbePhase.TracingOutline;

        /// <inheritdoc/>
        public bool IsMeasuringGrid => IsRunInProgress && !IsTracingOutline;

        public ProbeGrid? Grid => _grid;

        public int PointsCompleted => _grid?.Progress ?? 0;

        public int TotalPoints => _grid?.TotalPoints ?? 0;

        public int CurrentPointIndex => _currentPointIndex;

        public ProbeOptions Options { get; set; } = new ProbeOptions();

        public event Action<ProbePhase>? PhaseChanged;
        public event Action<int, Vector2, double>? PointCompleted;

        public ProbeController(IMachine machine)
        {
            _machine = machine ?? throw new ArgumentNullException(nameof(machine));
        }

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

            _machine.ProbeFinished += OnProbeFinished;

            try
            {
                if (Options.TraceOutline)
                {
                    Phase = ProbePhase.TracingOutline;
                    await TraceOutlineCoreAsync(ct);
                }

                await EnsureDoorClosedAsync(ct).ConfigureAwait(false);

                Phase = ProbePhase.SafetyRetracting;
                bool retracted = await MachineWait.SafetyRetractZAsync(_machine, Constants.SafeClearanceZ,
                    Constants.ZHeightWaitTimeoutMs, ct);

                ct.ThrowIfCancellationRequested();

                if (!retracted)
                {
                    // The next move is an XY rapid; without a confirmed retract it would
                    // drag the probe across the board. Name the door or the alarm when that
                    // is why the machine did not move.
                    throw new InvalidOperationException(
                        MachineWait.NeedsAttention(_machine)
                            ? ControllerConstants.ErrorMachineNotResponding
                            : ControllerConstants.ErrorSafetyRetractFailed);
                }

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

                Phase = ProbePhase.Descending;
                await RaiseZToSafeHeightAsync(ct);

                TransitionTo(ControllerState.Running);

                while (_grid.RemainingCount > 0 && !ct.IsCancellationRequested)
                {
                    await WaitWhilePausedAsync(ct);

                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    // The enclosure opened mid-probe. GRBL parks and every wait below
                    // fails, so handle the door before the next point.
                    if (MachineWait.IsDoor(_machine))
                    {
                        await RecoverFromDoorAsync(ct).ConfigureAwait(false);
                    }

                    SortPointsByDistance();

                    if (!_grid.TryPeekNext(out var point))
                    {
                        break;
                    }

                    var coords = _grid.GetCoordinates(point.X, point.Y);
                    _currentPointIndex = _grid.Progress;

                    EmitProgress(new ProgressInfo(
                        PhaseProbing,
                        (int)((_currentPointIndex / (double)_grid.TotalPoints) * 100),
                        string.Format(MessageProbeProgress, _currentPointIndex + 1, _grid.TotalPoints)));

                    bool success;
                    Vector3 position;
                    try
                    {
                        Phase = ProbePhase.MovingToPoint;
                        await MoveToPointAsync(coords, ct);

                        Phase = ProbePhase.Probing;
                        (success, position) = await ProbePointAsync(ct);
                    }
                    catch (TimeoutException) when (MachineWait.IsDoor(_machine))
                    {
                        // The enclosure opened part-way through this point, so GRBL parked
                        // and the reply never arrived. Handle the door, then probe this
                        // point again: nothing was recorded, so it is still queued.
                        await RecoverFromDoorAsync(ct).ConfigureAwait(false);
                        continue;
                    }

                    Phase = ProbePhase.RecordingResult;
                    if (success)
                    {
                        // Checked before it is recorded: a suspect reading must not reach
                        // the map or the autosave, and the point must stay queued so
                        // resuming re-probes it.
                        var outcome = await RetractAndCheckHeightAsync(point, position.Z, ct);

                        if (outcome == HeightOutcome.Cancelled)
                        {
                            break;
                        }

                        if (outcome == HeightOutcome.Remeasure)
                        {
                            // Nothing recorded, so the next pass picks this point up.
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
                        // incomplete, and a later pass can retry it. Reported now, because
                        // applying the map fails later and the reason is here.
                        _grid.SkipPoint(point.X, point.Y);
                        EmitError(new ControllerError(
                            string.Format(ControllerConstants.ErrorProbePointSkipped,
                                _currentPointIndex + 1, _grid.TotalPoints),
                            null,
                            IsFatal: false));

                        // The next move is an XY rapid, so confirm the retract first, as
                        // at the start of the run.
                        if (!await RaiseZToSafeHeightAsync(ct))
                        {
                            throw new InvalidOperationException(ControllerConstants.ErrorSafetyRetractFailed);
                        }
                    }
                }

                // Throw rather than return, so a cancelled run always unwinds through
                // CleanupAsync, which stops the machine and retracts.
                ct.ThrowIfCancellationRequested();

                Phase = ProbePhase.FinalRetract;
                if (!await RaiseZToSafeHeightAsync(ct))
                {
                    // The operator is about to reach in, so an unconfirmed retract fails
                    // the run rather than reporting it finished.
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

            // Retracts in work coordinates: a grid probe sets the frame it measures in, so
            // the run's own safe height is the right target.
            await StopAndLiftAsync(RaiseZToSafeHeightAsync, Constants.CancelRetractTimeoutMs)
                .ConfigureAwait(false);

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

        /// <summary>Runs on its own, without a grid probe around it.</summary>
        public async Task TraceOutlineAsync(CancellationToken ct)
        {
            if (_grid == null)
            {
                return;
            }

            // A trace moves the tool, so it takes the controller state like any other run
            // and every gate that asks whether the machine is busy sees it.
            TransitionTo(ControllerState.Initializing);
            Phase = ProbePhase.TracingOutline;

            try
            {
                // A trace moves the tool over the board, so the enclosure is settled before
                // it starts, as it is for a grid probe and a job.
                await EnsureDoorClosedAsync(ct).ConfigureAwait(false);

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

            if (Options.TraceHeight <= 0)
            {
                ControllerLog.Log("TraceOutline: REFUSED - trace height {0:F3} is not positive", Options.TraceHeight);

                // Thrown, not returned: returning here unwinds as a normal finish and the
                // run reports Completed for a trace that never ran.
                throw new InvalidOperationException(
                    string.Format(ControllerConstants.ErrorTraceHeightUnsafe, Options.TraceHeight));
            }

            double minX = _grid.Min.X;
            double minY = _grid.Min.Y;
            double maxX = _grid.Max.X;
            double maxY = _grid.Max.Y;

            ControllerLog.Log("TraceOutline: height={0:F3} feed={1:F0} grid=({2:F3},{3:F3})-({4:F3},{5:F3}) workZ={6:F3} machZ={7:F3}",
                Options.TraceHeight, Options.TraceFeed, minX, minY, maxX, maxY,
                _machine.WorkPosition.Z, _machine.MachinePosition.Z);

            ControllerLog.Log("TraceOutline: safety retract to machine Z={0:F1}", Constants.SafeClearanceZ);
            if (!await MachineWait.SafetyRetractZAsync(_machine, Constants.SafeClearanceZ,
                    Constants.ZHeightWaitTimeoutMs, ct))
            {
                // Thrown, not returned: returning unwinds as a normal finish and the run
                // reports Completed for a retract nobody confirmed.
                throw new InvalidOperationException(ControllerConstants.ErrorSafetyRetractFailed);
            }

            ControllerLog.Log("TraceOutline: moving to first corner ({0:F3}, {1:F3})", minX, minY);
            _machine.SendLine(CmdAbsolute);
            _machine.SendLine(Inv($"{CmdRapidMove} X{minX:F3} Y{minY:F3}"));
            await MachineWait.WaitForIdleAsync(_machine, Constants.MoveCompleteTimeoutMs, ct);

            ControllerLog.Log("TraceOutline: moving to trace height Z={0:F3} (workZ={1:F3} machZ={2:F3})",
                Options.TraceHeight, _machine.WorkPosition.Z, _machine.MachinePosition.Z);
            _machine.SendLine(CmdAbsolute);
            _machine.SendLine(Inv($"{CmdRapidMove} Z{Options.TraceHeight:F3}"));
            await MachineWait.WaitForZHeightAsync(_machine, Options.TraceHeight, Constants.MoveCompleteTimeoutMs, ct);
            await MachineWait.WaitForIdleAsync(_machine, Constants.MoveCompleteTimeoutMs, ct);
            ControllerLog.Log("TraceOutline: at trace height (workZ={0:F3} machZ={1:F3}), starting trace",
                _machine.WorkPosition.Z, _machine.MachinePosition.Z);

            // The first corner is left out; the move above already went there.
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

                // Both waits below give up the moment the machine parks at the door, and the
                // next corner would then be queued into a held machine and run when the hold
                // is released. Handle the enclosure first, and retract before moving again.
                if (MachineWait.IsDoor(_machine))
                {
                    await RecoverFromDoorAsync(ct).ConfigureAwait(false);
                }

                ControllerLog.Log("TraceOutline: moving to ({0:F3}, {1:F3})", x, y);
                _machine.SendLine(CmdAbsolute);
                _machine.SendLine(Inv($"{CmdLinearMove} X{x:F3} Y{y:F3} F{Options.TraceFeed:F0}"));

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
            // rapid unexecuted, so the return value decides whether the tool is clear.
            bool reached = await MachineWait.WaitForZHeightAsync(
                _machine, Options.SafeHeight, Constants.ZHeightWaitTimeoutMs, ct);
            bool stopped = await MachineWait.WaitForIdleAsync(
                _machine, Constants.ZHeightWaitTimeoutMs, ct);

            return reached && stopped;
        }

        /// <summary>
        /// Retract from the point just probed, and pause the run if the measured height
        /// disagrees with the neighboring nodes; the height is compared rather than the probe
        /// timing, because the retract and the rapid before it are queued without waiting and
        /// G38.2 drains that buffer before it moves, so the reply time covers all three.
        ///
        /// An accepted reading keeps the short buffered retract that makes the traverse fast; a
        /// rejected one goes to the safe height, because the operator is about to reach in.
        /// </summary>
        private async Task<HeightOutcome> RetractAndCheckHeightAsync(
            (int X, int Y) point, double measuredZ, CancellationToken ct)
        {
            double? deviation = _grid!.GetNeighborDeviation(point.X, point.Y, measuredZ);

            if (Options.HeightDeviationTolerance <= 0
                || !deviation.HasValue
                || deviation.Value <= Options.HeightDeviationTolerance)
            {
                await RetractZAsync(measuredZ, ct);
                return HeightOutcome.Accepted;
            }

            // Confirmed, unlike the buffered retract above: nothing else checks the tool
            // is clear before the operator is asked to reach in.
            if (!await RaiseZToSafeHeightAsync(ct))
            {
                throw new InvalidOperationException(ControllerConstants.ErrorSafetyRetractFailed);
            }

            ControllerLog.Log(LogProbeHeightUnexpected, measuredZ, deviation.Value);
            EmitError(new ControllerError(
                string.Format(ControllerConstants.ErrorProbeHeightUnexpected, measuredZ, deviation.Value),
                null,
                IsFatal: false));

            // The operator can press Pause from a UI thread at any time, and Paused has no
            // transition to itself. Tested and set under one lock so a pause landing between
            // the two cannot fail the run.
            TryTransitionTo(ControllerState.Paused);

            await WaitWhilePausedAsync(ct);

            // Resuming means the operator dealt with the cause, not that the reading was
            // good. Measure the point again.
            return ct.IsCancellationRequested ? HeightOutcome.Cancelled : HeightOutcome.Remeasure;
        }

        /// <summary>What the run does with a probed height.</summary>
        private enum HeightOutcome
        {
            /// <summary>Within tolerance of its neighbors. Record it.</summary>
            Accepted,

            /// <summary>The operator resumed. Probe the point again.</summary>
            Remeasure,

            /// <summary>Cancelled while the run was paused.</summary>
            Cancelled
        }

        /// <summary>
        /// A door park leaves the tool where GRBL restored it, at probe depth with the probe
        /// still touching. The loop's next moves are an XY rapid and another G38.2, so without
        /// this the probe is dragged across the board and the cycle starts from a triggered
        /// switch, which makes GRBL raise an alarm.
        /// </summary>
        private async Task RecoverFromDoorAsync(CancellationToken ct)
        {
            await EnsureDoorClosedAsync(ct).ConfigureAwait(false);

            if (!await RaiseZToSafeHeightAsync(ct))
            {
                throw new InvalidOperationException(ControllerConstants.ErrorSafetyRetractFailed);
            }
        }

        private Task RetractZAsync(double currentZ, CancellationToken ct)
        {
            // Deliberately not awaited, so GRBL buffers this retract with the next move and
            // the traverse stays smooth.
            double targetZ = Math.Max(currentZ + Options.MinimumHeight, Options.MinimumHeight);
            _machine.SendLine(CmdAbsolute);
            _machine.SendLine(Inv($"{CmdRapidMove} Z{targetZ:F3}"));
            return Task.CompletedTask;
        }

        private Task MoveToPointAsync(Vector2 coords, CancellationToken ct)
        {
            // Deliberately not awaited, so GRBL buffers this with the probe that follows.
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

            MachineWait.OpenProbeCycle(_machine);

            try
            {
                _machine.SendLine(CmdAbsolute);
                _machine.SendLine(Inv($"{CmdProbeToward} Z-{Options.MaxDepth:F3} F{Options.ProbeFeed:F1}"));

                return await MachineWait.AwaitReplyOrTimeoutAsync(probeTcs.Task,
                    Constants.ProbeReplyTimeoutMs, ControllerConstants.ErrorProbeTimeout, ct,
                    _machine);
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


        /// <inheritdoc/>
        public async Task<(bool Success, double ZPosition)> ProbeZSingleAsync(CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<(bool, Vector3)>();

            void OnProbeFinished(Vector3 pos, bool success)
            {
                tcs.TrySetResult((success, pos));
            }

            using var registration = ct.Register(() => tcs.TrySetCanceled());

            // Set once the move is on the machine, which is what decides whether there is
            // anything to stop.
            bool moveIsOut = false;

            // Opened before the try, so the ProbeStop in its finally cannot close a cycle
            // this call did not open.
            MachineWait.OpenProbeCycle(_machine);

            _machine.ProbeFinished += OnProbeFinished;
            try
            {
                // Relative, so the probe descends from wherever the tool is now.
                _machine.SendLine(CmdRelative);
                _machine.SendLine(Inv($"{CmdProbeToward} Z-{Options.MaxDepth:F3} F{Options.ProbeFeed:F1}"));
                _machine.SendLine(CmdAbsolute);
                moveIsOut = true;

                var (success, position) = await MachineWait.AwaitReplyOrTimeoutAsync(tcs.Task,
                    Constants.ProbeReplyTimeoutMs, ControllerConstants.ErrorProbeTimeout, ct,
                    _machine);

                ControllerLog.Log(LogProbeZSingle, success, position.Z);

                return (success, position.Z);
            }
            catch when (moveIsOut)
            {
                // GRBL still holds the probe move and will run it when the hold releases,
                // so closing the cycle below is not enough. Machine coordinates, because a
                // single probe starts from an arbitrary jog under whatever origin is set.
                await StopAndLiftAsync(Constants.SafeClearanceZ, Constants.CancelRetractTimeoutMs)
                    .ConfigureAwait(false);
                throw;
            }
            finally
            {
                _machine.ProbeFinished -= OnProbeFinished;
                _machine.ProbeStop();
            }
        }

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

        private const string LogProbeZSingle = "ProbeZSingle: success={0}, Z={1:F3}";
        private const string LogProbePhase = "Probe phase: {0}";
        private const string LogProbeGridCreated = "Probe grid created: {0}x{1} = {2} points";
        private const string LogProbeGridLoaded = "Probe grid loaded: {0}/{1} points complete";
        private const string LogProbeGridAlreadyComplete = "Probe grid already complete";
        private const string LogProbePointComplete = "Probe point {0}/{1} complete: Z={2:F3}";
        private const string LogProbePointFailed = "Probe point {0} failed";
        private const string LogProbeComplete = "Probing complete: {0} points";
        private const string LogProbeHeightUnexpected =
            "Probe height {0:F3} deviates {1:F3}mm from its measured neighbors";

        private const string ErrorNoProbeGrid = "No probe grid. Set one up before probing.";

        private const string PhaseProbing = "Probing";
        private const string MessageProbeProgress = "Point {0} of {1}";
    }
}
