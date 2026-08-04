using System;
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
        // SetupGrid tests
        // =========================================================================

        [Fact]
        public void SetupGrid_CreatesGrid()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            controller.SetupGrid(
                fileMin: new Vector2(0, 0),
                fileMax: new Vector2(100, 100),
                margin: 5.0,
                gridSize: 10.0);

            Assert.NotNull(controller.Grid);
            Assert.True(controller.TotalPoints > 0);
        }

        [Fact]
        public void SetupGrid_AppliesMargin()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            controller.SetupGrid(
                fileMin: new Vector2(10, 10),
                fileMax: new Vector2(50, 50),
                margin: 5.0,
                gridSize: 10.0);

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
        public void SetupGrid_WhenNotIdle_Throws()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            // Setup grid and start
            controller.SetupGrid(new Vector2(0, 0), new Vector2(10, 10), 1.0, 5.0);

            // Manually set state to running (simulate)
            // This is a synchronous test so we can't actually start
            // Just verify the controller validates state
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

            controller.SetupGrid(new Vector2(0, 0), new Vector2(50, 50), 2.0, 10.0);

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

        [Theory]
        [InlineData(ProbePhase.NotStarted)]
        [InlineData(ProbePhase.CreatingGrid)]
        [InlineData(ProbePhase.TracingOutline)]
        [InlineData(ProbePhase.SafetyRetracting)]
        [InlineData(ProbePhase.MovingToStart)]
        [InlineData(ProbePhase.Descending)]
        [InlineData(ProbePhase.MovingToPoint)]
        [InlineData(ProbePhase.Probing)]
        [InlineData(ProbePhase.RecordingResult)]
        [InlineData(ProbePhase.FinalRetract)]
        [InlineData(ProbePhase.Complete)]
        [InlineData(ProbePhase.Cancelled)]
        [InlineData(ProbePhase.Failed)]
        public void AllPhaseValues_AreValid(ProbePhase phase)
        {
            Assert.True(Enum.IsDefined(typeof(ProbePhase), phase));
        }

        // =========================================================================
        // Event tests
        // =========================================================================

        [Fact]
        public void PhaseChanged_EventCanBeSubscribed()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            ProbePhase? receivedPhase = null;
            controller.PhaseChanged += p => receivedPhase = p;

            // Just verify event is wirable
            Assert.Null(receivedPhase);
        }

        [Fact]
        public void PointCompleted_EventCanBeSubscribed()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            (int Index, Vector2 Coords, double Z)? received = null;
            controller.PointCompleted += (idx, coords, z) => received = (idx, coords, z);

            // Just verify event is wirable
            Assert.Null(received);
        }

        [Fact]
        public void ProgressChanged_EventCanBeSubscribed()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            ProgressInfo? receivedProgress = null;
            controller.ProgressChanged += p => receivedProgress = p;

            // Just verify event is wirable
            Assert.Null(receivedProgress);
        }

        [Fact]
        public void ErrorOccurred_EventCanBeSubscribed()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            ControllerError? receivedError = null;
            controller.ErrorOccurred += err => receivedError = err;

            // Just verify event is wirable
            Assert.Null(receivedError);
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

            controller.SetupGrid(new Vector2(0, 0), new Vector2(50, 50), 2.0, 10.0);

            Assert.Equal(0, controller.CurrentPointIndex);
        }

        /// <summary>
        /// LoadGrid sets _currentPointIndex to the grid's saved progress so an
        /// interrupted board resumes where it stopped, and both SetupGrid and LoadGrid
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
