using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using coppercli.Core.Controllers;
using coppercli.Core.GCode;
using coppercli.Core.Util;
using coppercli.Tests.Fakes;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// Tests for ProbeController workflow behavior.
    /// </summary>
    public class ProbeControllerTests
    {
        // =========================================================================
        // Test helpers
        // =========================================================================

        private static MockMachine CreateMockMachine()
        {
            return new MockMachine
            {
                Status = "Idle",
                Connected = true,
                MachinePosition = new Vector3(-50, -50, -5),
                WorkPosition = new Vector3(0, 0, 0),
                WorkOffset = new Vector3(-50, -50, -5)
            };
        }

        private static ProbeController CreateController(MockMachine machine)
        {
            return new ProbeController(machine);
        }

        // =========================================================================
        // Constructor tests
        // =========================================================================

        [Fact]
        public void Constructor_WithNullMachine_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new ProbeController(null!));
        }

        // =========================================================================
        // Initial state tests
        // =========================================================================

        [Fact]
        public void NewController_HasIdleState()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            Assert.Equal(ControllerState.Idle, controller.State);
            Assert.Equal(ProbePhase.NotStarted, controller.Phase);
        }

        [Fact]
        public void NewController_HasNoGrid()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            Assert.Null(controller.Grid);
            Assert.Equal(0, controller.PointsCompleted);
            Assert.Equal(0, controller.TotalPoints);
        }

        [Fact]
        public void NewController_HasDefaultOptions()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            Assert.NotNull(controller.Options);
            Assert.Equal(6.0, controller.Options.SafeHeight);
            Assert.Equal(10.0, controller.Options.MaxDepth);
            Assert.Equal(50.0, controller.Options.ProbeFeed);
        }

        // =========================================================================
        // Grid setup tests
        // =========================================================================

        [Fact]
        public void ForJob_CreatesGrid()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            controller.LoadGrid(ProbeGrid.ForJob(
                fileMin: new Vector2(0, 0),
                fileMax: new Vector2(100, 100),
                margin: 5.0,
                gridSize: 10.0));

            Assert.NotNull(controller.Grid);
            Assert.True(controller.TotalPoints > 0);
        }

        [Fact]
        public void ForJob_AppliesMargin()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            controller.LoadGrid(ProbeGrid.ForJob(
                fileMin: new Vector2(10, 10),
                fileMax: new Vector2(50, 50),
                margin: 5.0,
                gridSize: 10.0));

            var grid = controller.Grid;
            Assert.NotNull(grid);

            // Min should be fileMin - margin
            Assert.Equal(5.0, grid!.Min.X);
            Assert.Equal(5.0, grid.Min.Y);

            // Max should be fileMax + margin
            Assert.Equal(55.0, grid.Max.X);
            Assert.Equal(55.0, grid.Max.Y);
        }

        [Fact]
        public async Task LoadGrid_WhenNotIdle_Throws()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            controller.LoadGrid(ProbeGrid.ForJob(new Vector2(0, 0), new Vector2(10, 10), 1.0, 5.0));

            // StartAsync transitions out of Idle before it reaches its first await, so by
            // the time control returns here the controller is already running.
            using var cts = new CancellationTokenSource();
            var run = controller.StartAsync(cts.Token);
            try
            {
                Assert.NotEqual(ControllerState.Idle, controller.State);
                Assert.Throws<InvalidControllerStateException>(
                    () => controller.LoadGrid(ProbeGrid.ForJob(new Vector2(0, 0), new Vector2(10, 10), 1.0, 5.0)));
            }
            finally
            {
                cts.Cancel();
                await run;
            }
        }

        // =========================================================================
        // LoadGrid tests
        // =========================================================================

        [Fact]
        public void LoadGrid_SetsGrid()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            var grid = new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(100, 100));

            controller.LoadGrid(grid);

            Assert.Same(grid, controller.Grid);
            Assert.Equal(grid.TotalPoints, controller.TotalPoints);
        }

        [Fact]
        public void LoadGrid_WithNullGrid_Throws()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            Assert.Throws<ArgumentNullException>(() => controller.LoadGrid(null!));
        }

        [Fact]
        public void LoadGrid_WithPartiallyProbedGrid_SetsCorrectProgress()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            var grid = new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20));

            // Simulate some points already probed
            if (grid.TryPeekNext(out var point))
            {
                grid.RecordMeasurement(point.X, point.Y, -0.5);
            }

            controller.LoadGrid(grid);

            Assert.Equal(grid.Progress, controller.PointsCompleted);
            Assert.Equal(grid.TotalPoints, controller.TotalPoints);
        }

        // =========================================================================
        // GetGrid tests
        // =========================================================================

        [Fact]
        public void GetGrid_ReturnsNull_WhenNoGridSet()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            Assert.Null(controller.GetGrid());
        }

        [Fact]
        public void GetGrid_ReturnsSameGrid()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            controller.LoadGrid(ProbeGrid.ForJob(new Vector2(0, 0), new Vector2(50, 50), 2.0, 10.0));

            var grid = controller.GetGrid();
            Assert.Same(controller.Grid, grid);
        }

        // =========================================================================
        // Options tests
        // =========================================================================

        [Fact]
        public void Options_CanBeModified()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            controller.Options = new ProbeOptions
            {
                SafeHeight = 10.0,
                MaxDepth = 20.0,
                ProbeFeed = 100.0,
                AbortOnFail = false,
                TraceOutline = true
            };

            Assert.Equal(10.0, controller.Options.SafeHeight);
            Assert.Equal(20.0, controller.Options.MaxDepth);
            Assert.Equal(100.0, controller.Options.ProbeFeed);
            Assert.False(controller.Options.AbortOnFail);
            Assert.True(controller.Options.TraceOutline);
        }

        // =========================================================================
        // Phase enum tests
        // =========================================================================

        /// <summary>
        /// A phase names the step of work a run is on. Whether it is paused, waiting on a
        /// person, finishing, finished, cancelled or failed belongs to ControllerState,
        /// and naming it in both places lets the two be set apart and disagree.
        ///
        /// Matched on meaning rather than exact spelling, because a phase can name a state
        /// in different words: WaitingForOperator against WaitingForUserInput.
        /// </summary>
        [Fact]
        public void PhaseEnums_DoNotRestateTheRunLifecycle()
        {
            // The lifecycle state names, plus the spellings that mean the same standing
            // alone. WaitingForOperator is here because it said only "waiting on a
            // person", which is exactly what WaitingForUserInput says; WaitingForZeroZ
            // and WaitingForToolChange name which thing is awaited, so they are steps.
            var lifecycleNames = new HashSet<string>(Enum.GetNames(typeof(ControllerState)))
            {
                "Complete", "Finished", "Done", "Canceled", "Aborted", "Stopped",
                "WaitingForOperator", "WaitingForUser", "Pausing", "Resuming",
            };

            foreach (var enumType in new[]
                     { typeof(ProbePhase), typeof(MillingPhase), typeof(ToolChangePhase) })
            {
                foreach (string phaseName in Enum.GetNames(enumType))
                {
                    Assert.False(
                        lifecycleNames.Contains(phaseName),
                        $"{enumType.Name}.{phaseName} answers a lifecycle question. " +
                        "ControllerState owns that; derive it from the ControllerBase predicates.");
                }
            }
        }

        // =========================================================================
        // Event tests
        // =========================================================================

        /// <summary>
        /// Fires both events and asserts on what they carried. A trace height of zero is
        /// refused before any motion, so this reaches the phase changes and the error
        /// without needing a machine that moves.
        /// </summary>
        [Fact]
        public async Task RefusedTraceOutline_RaisesPhaseChangesAndAnError()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);
            controller.LoadGrid(ProbeGrid.ForJob(new Vector2(0, 0), new Vector2(20, 20), 1.0, 10.0));
            controller.Options.TraceHeight = 0;

            var phases = new List<ProbePhase>();
            ControllerError? error = null;
            controller.PhaseChanged += p => phases.Add(p);
            controller.ErrorOccurred += e => error = e;

            await controller.TraceOutlineAsync(CancellationToken.None);

            Assert.Contains(ProbePhase.TracingOutline, phases);
            Assert.Equal(ProbePhase.NotStarted, phases[^1]);

            Assert.NotNull(error);
            Assert.True(error!.IsFatal);

            // Built the same way the controller builds it, so the assertion does not
            // depend on which culture happens to be current.
            Assert.Equal(
                string.Format(ControllerConstants.ErrorTraceHeightUnsafe, 0.0),
                error.Message);
        }

        // =========================================================================
        // ProbeOptions tests
        // =========================================================================

        [Fact]
        public void ProbeOptions_HasReasonableDefaults()
        {
            var options = new ProbeOptions();

            Assert.Equal(6.0, options.SafeHeight);
            Assert.Equal(10.0, options.MaxDepth);
            Assert.Equal(50.0, options.ProbeFeed);
            Assert.Equal(1.0, options.MinimumHeight);
            Assert.True(options.AbortOnFail);
            Assert.Equal(1.0, options.XAxisWeight);
            Assert.Equal(6.0, options.TraceHeight);
            Assert.Equal(500.0, options.TraceFeed);
            Assert.False(options.TraceOutline);
        }

        // =========================================================================
        // ProbeGrid interaction tests
        // =========================================================================

        [Fact]
        public void CurrentPointIndex_StartsAtZero()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            controller.LoadGrid(ProbeGrid.ForJob(new Vector2(0, 0), new Vector2(50, 50), 2.0, 10.0));

            Assert.Equal(0, controller.CurrentPointIndex);
        }

        /// <summary>
        /// LoadGrid sets _currentPointIndex to the grid's saved progress so an
        /// interrupted board resumes where it stopped, and LoadGrid
        /// run before StartAsync - exactly when ResetRunState fires. _grid and
        /// _currentPointIndex are therefore excluded from ResetRunState: they describe
        /// the grid the operator loaded, not the run about to start.
        ///
        /// The capture below reads CurrentPointIndex at the first phase change RunAsync
        /// makes, before the probing loop gets anywhere near its own per-point
        /// reassignment of the field - the one place remaining that a wrongly-cleared
        /// index would still be visible. If ResetRunState cleared it, a half-probed
        /// board would report starting over from point zero rather than resuming.
        /// </summary>
        [Fact]
        public async Task StartingAPartiallyProbedGrid_ResumesFromItsSavedProgress()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            var grid = new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20));

            // Model an interrupted board: some points already measured, the rest still
            // queued.
            if (grid.TryPeekNext(out var point))
            {
                grid.RecordMeasurement(point.X, point.Y, -0.5);
            }

            controller.LoadGrid(grid);
            int expectedResumeIndex = grid.Progress;
            Assert.True(expectedResumeIndex > 0);

            int? indexAtRunStart = null;
            controller.PhaseChanged += phase =>
            {
                if (phase == ProbePhase.SafetyRetracting && indexAtRunStart == null)
                {
                    indexAtRunStart = controller.CurrentPointIndex;
                }
            };

            using var cts = new CancellationTokenSource();
            var run = controller.StartAsync(cts.Token);
            try
            {
                // StartAsync runs synchronously up to and past this phase change before
                // it can hit any await that would hand control back here, so the
                // capture above has already happened by this point.
                Assert.NotNull(indexAtRunStart);
            }
            finally
            {
                cts.Cancel();
                await run;
            }

            Assert.Equal(expectedResumeIndex, indexAtRunStart);
        }

        /// <summary>
        /// Without the lift, a stopped probe leaves the tip where the last descent put it,
        /// in the work. Every way a run ends goes through CleanupAsync, so the lift there
        /// covers the terminal, the web UI and a macro.
        /// </summary>
        [Fact]
        public async Task StoppingARun_LiftsTheToolToSafeHeight()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            // The fake reports this height already, so the lift confirms at once.
            controller.Options = new ProbeOptions { SafeHeight = machine.WorkPosition.Z };
            controller.LoadGrid(new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20)));

            var errors = new List<ControllerError>();
            controller.ErrorOccurred += errors.Add;

            using var cts = new CancellationTokenSource();
            var run = controller.StartAsync(cts.Token);
            cts.Cancel();
            await run;

            // The assertion checks order, because a lift queued before the soft reset is wiped
            // by it.
            string retract = GCodeFormat.Inv(
                $"{GrblProtocol.CmdRapidMove} Z{controller.Options.SafeHeight:F3}");
            int spindleOff = machine.SentCommands.IndexOf(GrblProtocol.CmdSpindleOff);
            int lift = machine.SentCommands.IndexOf(retract);
            Assert.True(spindleOff >= 0 && lift > spindleOff);
            Assert.DoesNotContain(errors, e => e.Message == ControllerConstants.ErrorStopRetractFailed);
        }

        /// <summary>
        /// A stop that cannot confirm the tool reached safe height says so, because the
        /// operator is about to reach into a machine that reports itself stopped.
        /// </summary>
        [Fact]
        public async Task StoppingARun_SaysSoWhenTheLiftCannotBeConfirmed()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            // The fake never moves, so the tool never reaches this height.
            controller.Options = new ProbeOptions { SafeHeight = machine.WorkPosition.Z + 10 };
            controller.LoadGrid(new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20)));

            var errors = new List<ControllerError>();
            controller.ErrorOccurred += errors.Add;

            using var cts = new CancellationTokenSource();
            var run = controller.StartAsync(cts.Token);
            cts.Cancel();
            await run;

            Assert.Contains(errors, e => e.Message == ControllerConstants.ErrorStopRetractFailed);
        }

        /// <summary>
        /// The trace moves the tool but measures nothing, so a progress display follows the
        /// grid probe instead. The controller holds the phase, so the controller answers whether
        /// it is tracing.
        /// </summary>
        [Fact]
        public async Task TracingTheOutline_ReportsItselfAsTracingRatherThanProbing()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);
            controller.LoadGrid(new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20)));

            // A trace height of 0 is refused, so the run sets the phase and returns without
            // waiting on a machine that never moves.
            controller.Options.TraceHeight = 0;

            bool tracingWhileRunning = false;
            controller.PhaseChanged += phase =>
            {
                if (phase == ProbePhase.TracingOutline)
                {
                    tracingWhileRunning = controller.IsTracingOutline;
                }
            };

            await controller.TraceOutlineAsync(CancellationToken.None);

            Assert.True(tracingWhileRunning);
            Assert.False(controller.IsTracingOutline);
        }

        /// <summary>
        /// Stop then start is the operator's ordinary loop. A stopped run must leave nothing
        /// claiming the machine, or the next start is refused as one already running.
        /// </summary>
        [Fact]
        public async Task StoppingARun_LeavesTheNextOneAbleToStart()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            // The fake reports this height already, so the lift confirms at once.
            controller.Options = new ProbeOptions { SafeHeight = machine.WorkPosition.Z };
            controller.LoadGrid(new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20)));

            using var cts = new CancellationTokenSource();
            var run = controller.StartAsync(cts.Token);
            cts.Cancel();
            await run;

            // The run ends itself; releasing only returns the controller to Idle. Asserting
            // both keeps this from passing on the repair alone.
            Assert.Equal(ControllerState.Cancelled, controller.State);

            await controller.ReleaseAsync();

            Assert.Equal(ControllerState.Idle, controller.State);
            Assert.False(controller.IsRunInProgress);
        }

        /// <summary>
        /// A grid probe reports itself as measuring and not tracing, so the progress window
        /// appears only for the run that has progress to show.
        /// </summary>
        [Fact]
        public async Task MeasuringTheGrid_ReportsItselfAsMeasuringRatherThanTracing()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);
            controller.LoadGrid(new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20)));

            // MockMachine records commands without moving, so set the height a machine that
            // obeyed the safety retract would report. The run then reaches the next phase.
            machine.MachinePosition = new Vector3(0, 0, Constants.MillStartSafetyZ);

            using var cts = new CancellationTokenSource();
            bool measuring = false;
            bool tracing = true;
            controller.PhaseChanged += phase =>
            {
                if (phase == ProbePhase.MovingToStart)
                {
                    measuring = controller.IsMeasuringGrid;
                    tracing = controller.IsTracingOutline;
                    cts.Cancel();
                }
            };

            await controller.StartAsync(cts.Token);

            Assert.True(measuring);
            Assert.False(tracing);
        }

        /// <summary>
        /// The run stops on a height that disagrees with the board around it and tells the
        /// operator to check for debris. Resuming means they dealt with it, not that the
        /// reading became good, so the reading must not reach the map, the autosave, or
        /// PointCompleted, and the point must stay queued to be measured again.
        /// </summary>
        [Fact]
        public async Task AHeightTheRunRefused_IsNotRecordedWhenTheOperatorResumes()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);
            controller.LoadGrid(ProbeGrid.ForJob(new Vector2(0, 0), new Vector2(10, 10), 0.0, 10.0));
            var grid = controller.Grid!;

            // MockMachine records commands without moving, so the Z waits would each sit
            // out their full timeout. Report the heights a machine that obeyed them would,
            // which is what the run is waiting to see.
            machine.MachinePosition = new Vector3(0, 0, Constants.MillStartSafetyZ);
            machine.WorkPosition = new Vector3(0, 0, controller.Options.SafeHeight);

            var recorded = new List<double>();
            controller.PointCompleted += (_, _, z) => { lock (recorded) { recorded.Add(z); } };

            // Three readings that agree, then one three millimeters away from them.
            double[] heights = { -0.50, -0.50, -0.50, 3.00 };
            int replies = 0;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            var pump = Task.Run(async () =>
            {
                while (!pumpCts.IsCancellationRequested)
                {
                    await Task.Delay(10);
                    if (machine.ProbeStartCount > replies && replies < heights.Length)
                    {
                        machine.SimulateProbeFinished(new Vector3(0, 0, heights[replies]), true);
                        replies++;
                    }
                }
            });

            var run = controller.StartAsync(cts.Token);

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && !controller.IsPaused && !controller.HasFinished)
            {
                await Task.Delay(20);
            }

            Assert.True(controller.IsPaused, "the run should hold on a height it does not trust");
            Assert.DoesNotContain(3.00, recorded);

            int measuredWhilePaused = grid.Progress;
            controller.Resume();
            await Task.Delay(300);

            Assert.DoesNotContain(3.00, recorded);
            Assert.True(grid.RemainingCount > 0, "the refused point must stay queued");
            Assert.False(grid.HasCompleteData);
            Assert.Equal(measuredWhilePaused, grid.Progress);

            pumpCts.Cancel();
            cts.Cancel();
            try { await run; } catch (OperationCanceledException) { }
            try { await pump; } catch (OperationCanceledException) { }
        }

        // =========================================================================
        // Reset tests
        // =========================================================================

        [Fact]
        public void Reset_FromIdle_RemainsIdle()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            controller.Reset();

            Assert.Equal(ControllerState.Idle, controller.State);
        }
    }
}
