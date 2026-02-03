#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using coppercli.Core.Communication;
using coppercli.Core.GCode;
using coppercli.Core.Util;
using static coppercli.Core.Util.GrblProtocol;
using static coppercli.Core.Controllers.ControllerConstants;

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
    /// <para>UI state is determined by in-memory grid progress. Autosave state determines
    /// Save vs Clear button behavior.</para>
    ///
    /// <para><b>State Machine (based on grid.Progress):</b></para>
    /// <code>
    /// ┌─────────────────────────────────────────────────────────────────────────┐
    /// │  STATE     │ CONDITION              │ START BUTTON   │ SAVE/DISCARD    │
    /// ├────────────┼────────────────────────┼────────────────┼─────────────────┤
    /// │  none      │ no grid                │ disabled       │ disabled        │
    /// │  ready     │ grid, progress=0       │ [Start]        │ disabled        │
    /// │  partial   │ 0 &lt; progress &lt; total   │ [Continue]     │ [Discard]*      │
    /// │  complete  │ progress = total       │ disabled       │ [Save]*/[Clear] │
    /// └─────────────────────────────────────────────────────────────────────────┘
    /// * Only if hasUnsavedData (autosave exists)
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
    ///   <item>true: Autosave exists (data from probing) → Show Save/Discard</item>
    ///   <item>false: No autosave (loaded from file) → Show Clear</item>
    /// </list>
    ///
    /// <para><b>Implementation:</b></para>
    /// <list type="bullet">
    ///   <item><c>ComputeProbeState(grid)</c> - Returns state (none/ready/partial/complete).</item>
    ///   <item><c>Persistence.GetProbeState()</c> - Returns autosave state for hasUnsavedData.</item>
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
        private readonly Queue<long> _probeTimes = new();
        private readonly Stopwatch _probeStopwatch = new();

        private const int ProbeTimeWindowSize = 10;

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

        public void SetupGrid(Vector2 fileMin, Vector2 fileMax, double margin, double gridSize)
        {
            if (State != ControllerState.Idle)
            {
                throw new InvalidOperationException(string.Format(ErrorCannotStart, State));
            }

            var min = new Vector2(fileMin.X - margin, fileMin.Y - margin);
            var max = new Vector2(fileMax.X + margin, fileMax.Y + margin);

            _grid = new ProbeGrid(gridSize, min, max);
            _currentPointIndex = 0;
            _probeTimes.Clear();

            ControllerLog.Log(LogProbeGridCreated, _grid.SizeX, _grid.SizeY, _grid.TotalPoints);
        }

        public void LoadGrid(ProbeGrid grid)
        {
            if (State != ControllerState.Idle)
            {
                throw new InvalidOperationException(string.Format(ErrorCannotStart, State));
            }

            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _currentPointIndex = grid.Progress;
            _probeTimes.Clear();

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

            if (_grid.NotProbed.Count == 0)
            {
                ControllerLog.Log(LogProbeGridAlreadyComplete);
                Phase = ProbePhase.Complete;
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
                await MachineWait.SafetyRetractZAsync(_machine, Constants.MillStartSafetyZ,
                    Constants.ZHeightWaitTimeoutMs, ct);

                // Move to first probe point at safe height
                SortPointsByDistance();
                var firstPoint = _grid.NotProbed[0];
                var firstCoords = _grid.GetCoordinates(firstPoint);
                Phase = ProbePhase.MovingToStart;
                _machine.SendLine(CmdAbsolute);
                _machine.SendLine($"{CmdRapidMove} X{firstCoords.X:F3} Y{firstCoords.Y:F3}");
                await MachineWait.WaitForIdleAsync(_machine, Constants.MoveCompleteTimeoutMs, ct);

                // Descend to safe height (work coords)
                Phase = ProbePhase.Descending;
                await RaiseZToSafeHeightAsync(ct);

                TransitionTo(ControllerState.Running);

                // Probe all remaining points
                while (_grid.NotProbed.Count > 0 && !ct.IsCancellationRequested)
                {
                    // Check for pause
                    while (State == ControllerState.Paused && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(Constants.StatusPollIntervalMs, ct);
                    }

                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    // Sort points by distance for optimal path
                    SortPointsByDistance();

                    var point = _grid.NotProbed[0];
                    var coords = _grid.GetCoordinates(point);
                    _currentPointIndex = _grid.Progress;

                    // Emit progress
                    EmitProgress(new ProgressInfo(
                        PhaseProbing,
                        (int)((_currentPointIndex / (double)_grid.TotalPoints) * 100),
                        string.Format(MessageProbeProgress, _currentPointIndex + 1, _grid.TotalPoints)));

                    // Move to point
                    Phase = ProbePhase.MovingToPoint;
                    await MoveToPointAsync(coords, ct);

                    // Probe with timing
                    Phase = ProbePhase.Probing;
                    _probeStopwatch.Restart();
                    var (success, position) = await ProbePointAsync(ct);
                    _probeStopwatch.Stop();
                    long probeTimeMs = _probeStopwatch.ElapsedMilliseconds;

                    if (success)
                    {
                        // Check for slow probe (need at least one prior probe to compare)
                        if (_probeTimes.Count > 0 && Options.SlowProbeThreshold > 0)
                        {
                            double avgTime = GetAverageProbeTime();
                            if (probeTimeMs > avgTime * Options.SlowProbeThreshold)
                            {
                                ControllerLog.Log(LogSlowProbeDetected, probeTimeMs, avgTime, Options.SlowProbeThreshold);
                                EmitError(new ControllerError(
                                    string.Format(ErrorSlowProbe, probeTimeMs, avgTime),
                                    null,
                                    IsFatal: false));
                                TransitionTo(ControllerState.Paused);

                                // Wait for resume or cancel
                                while (State == ControllerState.Paused && !ct.IsCancellationRequested)
                                {
                                    await Task.Delay(Constants.StatusPollIntervalMs, ct);
                                }

                                if (ct.IsCancellationRequested)
                                {
                                    break;
                                }
                            }
                        }

                        // Track probe time (skip first - it comes from higher up and is slower)
                        bool isFirstProbe = _grid!.Progress == 0 && _probeTimes.Count == 0;
                        if (!isFirstProbe)
                        {
                            AddProbeTime(probeTimeMs);
                            ControllerLog.Log(LogProbeTime, probeTimeMs, GetAverageProbeTime());
                        }
                        else
                        {
                            ControllerLog.Log(LogProbeTimeFirstSkipped, probeTimeMs);
                        }
                    }

                    // Record result
                    Phase = ProbePhase.RecordingResult;
                    if (success)
                    {
                        _grid.AddPoint(point.Item1, point.Item2, position.Z);
                        _grid.NotProbed.RemoveAt(0);
                        PointCompleted?.Invoke(_currentPointIndex, coords, position.Z);
                        ControllerLog.Log(LogProbePointComplete, _currentPointIndex + 1, _grid.TotalPoints, position.Z);

                        // Retract after successful probe
                        await RetractZAsync(position.Z, ct);
                    }
                    else
                    {
                        ControllerLog.Log(LogProbePointFailed, _currentPointIndex + 1);

                        if (Options.AbortOnFail)
                        {
                            EmitError(new ControllerError(ControllerConstants.ErrorProbeNoContact, null, true));
                            Phase = ProbePhase.Failed;
                            TransitionTo(ControllerState.Failed);
                            return;
                        }

                        // Skip this point
                        _grid.NotProbed.RemoveAt(0);
                        await RaiseZToSafeHeightAsync(ct);
                    }
                }

                if (ct.IsCancellationRequested)
                {
                    Phase = ProbePhase.Cancelled;
                    return;
                }

                // Final retract
                Phase = ProbePhase.FinalRetract;
                await RaiseZToSafeHeightAsync(ct);

                Phase = ProbePhase.Complete;
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
            ControllerLog.Log("ProbeController.CleanupAsync: after StopAndReset, status={0}", _machine.Status);

            // Raise Z to safe height
            _machine.SendLine(CmdAbsolute);
            _machine.SendLine($"{CmdRapidMove} Z{Options.SafeHeight:F3}");
            await Task.Delay(Constants.CommandDelayMs);
            ControllerLog.Log("ProbeController.CleanupAsync: done");
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

            Phase = ProbePhase.TracingOutline;

            try
            {
                await TraceOutlineCoreAsync(ct);
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

            double minX = _grid.Min.X;
            double minY = _grid.Min.Y;
            double maxX = _grid.Max.X;
            double maxY = _grid.Max.Y;

            // Safety retract to machine coords first
            ControllerLog.Log("TraceOutline: safety retract to machine Z={0:F1}", Constants.MillStartSafetyZ);
            await MachineWait.SafetyRetractZAsync(_machine, Constants.MillStartSafetyZ,
                Constants.ZHeightWaitTimeoutMs, ct);

            // Move to first corner at safe height
            ControllerLog.Log("TraceOutline: moving to first corner ({0:F3}, {1:F3})", minX, minY);
            _machine.SendLine(CmdAbsolute);
            _machine.SendLine($"{CmdRapidMove} X{minX:F3} Y{minY:F3}");
            await MachineWait.WaitForIdleAsync(_machine, Constants.MoveCompleteTimeoutMs, ct);

            // Descend to trace height
            ControllerLog.Log("TraceOutline: descending to trace height Z={0:F3}", Options.TraceHeight);
            _machine.SendLine(CmdAbsolute);
            _machine.SendLine($"{CmdRapidMove} Z{Options.TraceHeight:F3}");
            await MachineWait.WaitForIdleAsync(_machine, Constants.MoveCompleteTimeoutMs, ct);
            ControllerLog.Log("TraceOutline: starting trace");

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
                _machine.SendLine($"{CmdLinearMove} X{x:F3} Y{y:F3} F{Options.TraceFeed:F0}");

                // Wait for status to change from Idle (motion started), then wait for Idle (motion complete)
                await MachineWait.WaitForStatusChangeAsync(_machine, StatusIdle, Constants.MotionStartTimeoutMs, ct);
                await MachineWait.WaitForIdleAsync(_machine, Constants.MoveCompleteTimeoutMs, ct);
            }
        }

        private async Task RaiseZToSafeHeightAsync(CancellationToken ct)
        {
            _machine.SendLine(CmdAbsolute);
            _machine.SendLine($"{CmdRapidMove} Z{Options.SafeHeight:F3}");
            await MachineWait.WaitForIdleAsync(_machine, Constants.ZHeightWaitTimeoutMs, ct);
        }

        private Task RetractZAsync(double currentZ, CancellationToken ct)
        {
            // Don't wait for idle - let GRBL buffer the retract with the next move for smooth motion
            double targetZ = Math.Max(currentZ + Options.MinimumHeight, Options.MinimumHeight);
            _machine.SendLine(CmdAbsolute);
            _machine.SendLine($"{CmdRapidMove} Z{targetZ:F3}");
            return Task.CompletedTask;
        }

        private Task MoveToPointAsync(Vector2 coords, CancellationToken ct)
        {
            // Don't wait - let GRBL buffer this with the probe command for smooth motion
            _machine.SendLine(CmdAbsolute);
            _machine.SendLine($"{CmdRapidMove} X{coords.X:F3} Y{coords.Y:F3}");
            return Task.CompletedTask;
        }

        private async Task<(bool Success, Vector3 Position)> ProbePointAsync(CancellationToken ct)
        {
            _probeTcs = new TaskCompletionSource<(bool, Vector3)>();

            using var registration = ct.Register(() => _probeTcs.TrySetCanceled());

            _machine.ProbeStart();

            try
            {
                _machine.SendLine(CmdAbsolute);
                _machine.SendLine($"{CmdProbeToward} Z-{Options.MaxDepth:F3} F{Options.ProbeFeed:F1}");

                return await _probeTcs.Task;
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
                _machine.SendLine($"{CmdProbeToward} Z-{Options.MaxDepth:F3} F{Options.ProbeFeed:F1}");
                _machine.SendLine(CmdAbsolute);

                var (success, position) = await tcs.Task;

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

            _grid.NotProbed.Sort((a, b) =>
            {
                var va = _grid.GetCoordinates(a) - currentPos;
                var vb = _grid.GetCoordinates(b) - currentPos;
                va.X *= xWeight;
                vb.X *= xWeight;
                return va.Magnitude.CompareTo(vb.Magnitude);
            });
        }

        private void AddProbeTime(long timeMs)
        {
            _probeTimes.Enqueue(timeMs);
            while (_probeTimes.Count > ProbeTimeWindowSize)
            {
                _probeTimes.Dequeue();
            }
        }

        private double GetAverageProbeTime()
        {
            if (_probeTimes.Count == 0)
            {
                return 0;
            }

            long sum = 0;
            foreach (long t in _probeTimes)
            {
                sum += t;
            }
            return sum / (double)_probeTimes.Count;
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
        private const string LogProbeTime = "Probe time: {0}ms (avg: {1:F0}ms)";
        private const string LogProbeTimeFirstSkipped = "First probe time: {0}ms (not included in average)";
        private const string LogSlowProbeDetected = "Slow probe detected: {0}ms > {1:F0}ms * {2:F2} threshold";

        private const string ErrorNoProbeGrid = "No probe grid configured. Call SetupGrid or LoadGrid first.";
        private const string ErrorSlowProbe = "Probe took {0}ms (avg: {1:F0}ms) - possible non-conductive surface";

        private const string PhaseProbing = "Probing";
        private const string MessageProbeProgress = "Point {0} of {1}";
    }
}
