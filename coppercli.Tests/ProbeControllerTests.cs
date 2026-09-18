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
    // Covers ProbeController: grid setup, options, the run phases, and what a run does at a
    // stop or an open door. MockMachine follows a machine-coordinate Z rapid and records
    // every other command without moving, so a test that needs a position reached sets it on
    // the mock.
    public class ProbeControllerTests
    {
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

        /// <summary>Distinct from zero, so an unmeasured node cannot pass for this.</summary>
        private const double MeasuredHeight = 0.25;

        /// <summary>Returns -1 when no command starts with the prefix.</summary>
        private static int IndexOfFirstStartingWith(IReadOnlyList<string> commands, string prefix)
        {
            for (int i = 0; i < commands.Count; i++)
            {
                if (commands[i].StartsWith(prefix, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        [Fact]
        public void Constructor_WithNullMachine_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new ProbeController(null!));
        }

        [Fact]
        public void NewController_HasIdleState()
        {
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);

            Assert.Equal(ControllerState.Idle, controller.State);
            Assert.Equal(ProbePhase.NotStarted, controller.Phase);
        }

        [Fact]
        public void NewController_HasNoGrid()
        {
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);

            Assert.Null(controller.Grid);
            Assert.Equal(0, controller.PointsCompleted);
            Assert.Equal(0, controller.TotalPoints);
        }

        [Fact]
        public void NewController_HasDefaultOptions()
        {
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);

            Assert.NotNull(controller.Options);
            Assert.Equal(6.0, controller.Options.SafeHeight);
            Assert.Equal(10.0, controller.Options.MaxDepth);
            Assert.Equal(50.0, controller.Options.ProbeFeed);
        }

        [Fact]
        public void ForJob_CreatesGrid()
        {
            using var machine = CreateMockMachine();
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
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);

            controller.LoadGrid(ProbeGrid.ForJob(
                fileMin: new Vector2(10, 10),
                fileMax: new Vector2(50, 50),
                margin: 5.0,
                gridSize: 10.0));

            var grid = controller.Grid;
            Assert.NotNull(grid);

            Assert.Equal(5.0, grid!.Min.X);
            Assert.Equal(5.0, grid.Min.Y);

            Assert.Equal(55.0, grid.Max.X);
            Assert.Equal(55.0, grid.Max.Y);
        }

        [Fact]
        public async Task LoadGrid_WhenNotIdle_Throws()
        {
            using var machine = CreateMockMachine();
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

        [Fact]
        public void LoadGrid_SetsGrid()
        {
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);

            var grid = new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(100, 100));

            controller.LoadGrid(grid);

            Assert.Same(grid, controller.Grid);
            Assert.Equal(grid.TotalPoints, controller.TotalPoints);
        }

        [Fact]
        public void LoadGrid_WithNullGrid_Throws()
        {
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);

            Assert.Throws<ArgumentNullException>(() => controller.LoadGrid(null!));
        }

        [Fact]
        public void LoadGrid_WithPartiallyProbedGrid_SetsCorrectProgress()
        {
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);

            var grid = new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20));

            if (grid.TryPeekNext(out var point))
            {
                grid.RecordMeasurement(point.X, point.Y, -0.5);
            }

            controller.LoadGrid(grid);

            Assert.Equal(grid.Progress, controller.PointsCompleted);
            Assert.Equal(grid.TotalPoints, controller.TotalPoints);
        }

        [Fact]
        public void GetGrid_ReturnsNull_WhenNoGridSet()
        {
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);

            Assert.Null(controller.GetGrid());
        }

        [Fact]
        public void GetGrid_ReturnsSameGrid()
        {
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);

            controller.LoadGrid(ProbeGrid.ForJob(new Vector2(0, 0), new Vector2(50, 50), 2.0, 10.0));

            var grid = controller.GetGrid();
            Assert.Same(controller.Grid, grid);
        }

        [Fact]
        public void Options_CanBeModified()
        {
            using var machine = CreateMockMachine();
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

        /// <summary>
        /// ControllerState records whether a run is paused, waiting for input, finishing,
        /// finished, cancelled, or failed. Phase enums must not duplicate those values.
        /// </summary>
        [Fact]
        public void PhaseEnums_ExcludeControllerStates()
        {
            // The lifecycle names, plus the spellings that mean the same on their own.
            // WaitingForZeroZ and WaitingForToolChange name what is being waited for, so
            // they are steps of work rather than lifecycle states.
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
                        "Use ControllerState and derive it from the ControllerBase predicates.");
                }
            }
        }

        /// <summary>
        /// A trace height of zero is refused before any motion, so this reaches the phase
        /// changes and the error without a machine that moves.
        /// </summary>
        [Fact]
        public async Task RefusedTraceOutline_RaisesPhaseChangesAndAnError()
        {
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);
            controller.LoadGrid(ProbeGrid.ForJob(new Vector2(0, 0), new Vector2(20, 20), 1.0, 10.0));
            controller.Options.TraceHeight = 0;

            var phases = new List<ProbePhase>();
            ControllerError? error = null;
            controller.PhaseChanged += p => phases.Add(p);
            controller.ErrorOccurred += e => error = e;

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => controller.TraceOutlineAsync(CancellationToken.None));

            Assert.Contains(ProbePhase.TracingOutline, phases);
            Assert.Equal(ProbePhase.NotStarted, phases[^1]);

            // Failed, not Completed: a trace that never ran must not report success.
            Assert.Equal(ControllerState.Failed, controller.State);

            Assert.NotNull(error);
            Assert.True(error!.IsFatal);

            // Built the same way the controller builds it, so the assertion does not depend
            // on the current culture.
            Assert.Equal(
                string.Format(ControllerConstants.ErrorTraceHeightUnsafe, 0.0),
                error.Message);
        }

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

        [Fact]
        public void CurrentPointIndex_StartsAtZero()
        {
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);

            controller.LoadGrid(ProbeGrid.ForJob(new Vector2(0, 0), new Vector2(50, 50), 2.0, 10.0));

            Assert.Equal(0, controller.CurrentPointIndex);
        }

        /// <summary>
        /// The capture below reads CurrentPointIndex at the run's first phase change, before
        /// the probing loop reassigns the field per point. An index cleared by ResetRunState
        /// would make a half-probed board start over from point zero instead of resuming.
        /// </summary>
        [Fact]
        public async Task StartingAPartiallyProbedGrid_ResumesFromItsSavedProgress()
        {
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);

            var grid = new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20));

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
                // StartAsync runs synchronously past this phase change before its first
                // await, so the capture above has already happened.
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
        /// Without the retract, a stopped probe leaves the tip where the last descent put
        /// it. Every run ends through CleanupAsync, so the retract there covers the terminal,
        /// the web UI and a macro.
        /// </summary>
        [Fact]
        public async Task StoppingARun_RetractsToSafeHeight()
        {
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);

            // The fake already reports this height, so the retract confirms immediately.
            controller.Options = new ProbeOptions { SafeHeight = machine.WorkPosition.Z };
            controller.LoadGrid(new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20)));

            var errors = new List<ControllerError>();
            controller.ErrorOccurred += errors.Add;

            using var cts = new CancellationTokenSource();
            var run = controller.StartAsync(cts.Token);
            cts.Cancel();
            await run;

            // The assertion checks order, because a retract queued before the soft reset is
            // discarded by it. The last occurrence is the stop's own lift: the run travels
            // to the same height while it is measuring.
            string retract = GCodeFormat.Inv(
                $"{GrblProtocol.CmdRapidMove} Z{controller.Options.SafeHeight:F3}");
            int spindleOff = machine.SentCommands.IndexOf(GrblProtocol.CmdSpindleOff);
            int lift = machine.SentCommands.LastIndexOf(retract);
            Assert.True(spindleOff >= 0 && lift > spindleOff,
                $"spindle off at {spindleOff}, lift at {lift}, in: {string.Join(" | ", machine.SentCommands)}");
            Assert.DoesNotContain(errors, e => e.Message == ControllerConstants.ErrorStopRetractFailed);
        }

        /// <summary>
        /// A stop that cannot confirm the tool reached the safe height reports that, because
        /// the operator is about to reach into a machine that reports itself stopped.
        /// </summary>
        [Fact]
        public async Task StoppingARun_ReportsAnUnconfirmedRetract()
        {
            using var machine = CreateMockMachine();
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
        /// A probe move sent with GRBL's probe cycle closed runs with nothing watching for
        /// the trigger, so the tool descends to full depth and reports no contact.
        /// </summary>
        [Fact]
        public async Task AProbeCycleThatWillNotOpen_IsNotSentAProbeMove()
        {
            using var machine = CreateMockMachine();
            machine.ProbeStartSucceeds = false;

            var controller = CreateController(machine);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            await Assert.ThrowsAnyAsync<Exception>(() => controller.ProbeZSingleAsync(cts.Token));

            Assert.DoesNotContain(
                machine.SentCommands,
                c => c.StartsWith(GrblProtocol.CmdProbeToward, StringComparison.Ordinal));
        }

        /// <summary>
        /// A single probe the operator stops leaves the G38.2 in GRBL's planner. Closing the
        /// cycle is not enough: the machine is stopped and the tool lifted, in machine
        /// coordinates, because a single probe starts from an arbitrary jog position.
        /// </summary>
        [Fact]
        public async Task AnInterruptedSingleProbe_StopsTheMachineAndLifts()
        {
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);

            using var cts = new CancellationTokenSource();
            var probe = controller.ProbeZSingleAsync(cts.Token);
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);

            Assert.Contains(
                machine.SentCommands,
                c => c.StartsWith(GrblProtocol.CmdMachineCoords, StringComparison.Ordinal));
        }

        /// <summary>
        /// The control for AProbeCycleThatWillNotOpen_IsNotSentAProbeMove: with the cycle
        /// open the move does go out, so that test is not passing on nothing being sent.
        /// </summary>
        [Fact]
        public async Task AProbeCycleThatOpens_DoesSendTheProbeMove()
        {
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            await Assert.ThrowsAnyAsync<Exception>(() => controller.ProbeZSingleAsync(cts.Token));

            Assert.Contains(
                machine.SentCommands,
                c => c.StartsWith(GrblProtocol.CmdProbeToward, StringComparison.Ordinal));
        }

        /// <summary>
        /// The trace's first move after the retract is an XY rapid, so an unconfirmed retract
        /// would drag the probe across the board. The failure has to throw: returned instead,
        /// the run reports Completed.
        /// </summary>
        [Fact]
        public async Task ATraceWhoseSafetyRetractIsNotConfirmed_NeverReportsItFinished()
        {
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);
            controller.LoadGrid(new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20)));

            // Alarm after the retract so the wait ends before its timeout.
            machine.IgnoreMoves = true;
            machine.LineSent += line =>
            {
                if (line.StartsWith(GrblProtocol.CmdMachineCoords, StringComparison.Ordinal))
                {
                    machine.Status = GrblProtocol.StatusAlarm;
                }
            };

            var errors = new List<ControllerError>();
            controller.ErrorOccurred += errors.Add;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => controller.TraceOutlineAsync(cts.Token));

            Assert.NotEqual(ControllerState.Completed, controller.State);
            Assert.Contains(errors, e => e.Message == ControllerConstants.ErrorSafetyRetractFailed);
        }

        [Fact]
        public async Task TracingTheOutline_ReportsTracingNotProbing()
        {
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);
            controller.LoadGrid(new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20)));

            // A trace height of 0 is refused, so the run sets the phase and fails without
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

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => controller.TraceOutlineAsync(CancellationToken.None));

            Assert.True(tracingWhileRunning);
            Assert.False(controller.IsTracingOutline);
        }

        /// <summary>
        /// A stopped run must leave no run in progress, or the next start is refused as one
        /// already running.
        /// </summary>
        [Fact]
        public async Task StoppingARun_LeavesTheNextOneAbleToStart()
        {
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);

            // The fake already reports this height, so the retract confirms immediately.
            controller.Options = new ProbeOptions { SafeHeight = machine.WorkPosition.Z };
            controller.LoadGrid(new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20)));

            using var cts = new CancellationTokenSource();
            var run = controller.StartAsync(cts.Token);
            cts.Cancel();
            await run;

            // The run ends itself; releasing only returns the controller to Idle. Both are
            // asserted so this cannot pass on the release alone.
            Assert.Equal(ControllerState.Cancelled, controller.State);

            await controller.ReleaseAsync();

            Assert.Equal(ControllerState.Idle, controller.State);
            Assert.False(controller.IsRunInProgress);
        }

        [Fact]
        public async Task MeasuringTheGrid_ReportsMeasuringNotTracing()
        {
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);
            controller.LoadGrid(new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20)));

            // The height a machine that obeyed the safety retract would report, so the run
            // reaches the next phase.
            machine.MachinePosition = new Vector3(0, 0, Constants.SafeClearanceZ);

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
        /// A resume means the operator cleared the debris, not that the height outside
        /// tolerance was good. That height must not reach the map, the autosave or
        /// PointCompleted, and its point must stay queued - rule `remeasure-probe-point-after-operator-resume`.
        /// </summary>
        [Fact]
        public async Task ARejectedHeight_IsNotRecordedOnResume()
        {
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);
            controller.LoadGrid(ProbeGrid.ForJob(new Vector2(0, 0), new Vector2(10, 10), 0.0, 10.0));
            var grid = controller.Grid!;

            // The heights a machine that executed the Z moves would report; without them each
            // Z wait sits out its full timeout.
            machine.MachinePosition = new Vector3(0, 0, Constants.SafeClearanceZ);
            machine.WorkPosition = new Vector3(0, 0, controller.Options.SafeHeight);

            var recorded = new List<double>();
            controller.PointCompleted += (_, _, z) => { lock (recorded) { recorded.Add(z); } };

            // Three readings within tolerance, then one 3mm away.
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

            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(20)
                   && !controller.IsPaused && !controller.HasFinished)
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

        [Fact]
        public void Reset_FromIdle_RemainsIdle()
        {
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);

            controller.Reset();

            Assert.Equal(ControllerState.Idle, controller.State);
        }

        /// <summary>
        /// A probe started at an open door waits for the door and does not prompt: an open
        /// door leaves the operator nothing to answer. Without the wait the first safety
        /// retract fails, with a message about the tool that never mentions the door.
        /// </summary>
        [Fact]
        public async Task ProbeStartAtOpenDoor_WaitsInsteadOfFailing()
        {
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateAjar);
            machine.MachinePosition = new Vector3(-50, -50, -5);
            machine.WorkOffset = new Vector3(-50, -50, -5);

            var controller = CreateController(machine);
            controller.LoadGrid(ProbeGrid.ForJob(new Vector2(0, 0), new Vector2(10, 10), 10.0, 5.0));

            bool asked = false;
            controller.UserInputRequired += request =>
            {
                asked = true;
                request.OnResponse(ControllerConstants.OptionAbort);
            };

            using var cts = new CancellationTokenSource(ControllerConstants.DoorResumeTimeoutMs + 2000);
            try
            {
                await controller.StartAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Cancelling is the only exit when the door is never closed.
            }

            Assert.False(asked, "an open door was turned into a question");
            Assert.NotEqual(ControllerState.Failed, controller.State);
        }

        /// <summary>
        /// A door park leaves the tool at probe depth with the probe touching, and GRBL
        /// restores it there. The next moves are an XY rapid and another G38.2, so the run
        /// has to retract first: without it the probe is dragged across the board and the
        /// probe cycle starts from a triggered switch, which GRBL alarms on.
        /// </summary>
        [Fact]
        public async Task DoorDuringAProbe_RetractsBeforeTheNextMove()
        {
            using var machine = CreateMockMachine();

            // The height a machine that obeyed both retracts would report: the safety retract
            // is in machine coordinates, the run's own in work coordinates.
            machine.MachinePosition = new Vector3(-50, -50, Constants.SafeClearanceZ);

            var controller = CreateController(machine);
            controller.Options = new ProbeOptions { SafeHeight = machine.WorkPosition.Z };
            controller.LoadGrid(new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20)));

            // The enclosure opens as the first probe goes out and is closed again before the
            // next status read, which leaves the machine parked and waiting for a cycle start.
            bool parked = false;
            machine.LineSent += line =>
            {
                if (!parked && line.StartsWith(GrblProtocol.CmdProbeToward))
                {
                    parked = true;
                    machine.SimulateDoorClosedAndHolding();
                }
            };

            controller.UserInputRequired += request =>
            {
                // Everything after this is the recovery.
                machine.SentCommands.Clear();
                request.OnResponse(ControllerConstants.OptionContinue);
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await controller.StartAsync(cts.Token);
            }
            catch
            {
                // The run cannot finish against a mock that never moves; the order of the
                // moves after the door is what this checks.
            }

            Assert.True(parked, "the run never sent a probe, so the door never interrupted one");

            int retract = IndexOfFirstStartingWith(
                machine.SentCommands, GrblProtocol.CmdRapidMove + " Z");
            int traverse = IndexOfFirstStartingWith(
                machine.SentCommands, GrblProtocol.CmdRapidMove + " X");

            Assert.True(retract >= 0, "the run did not retract after the door");
            Assert.True(traverse < 0 || retract < traverse,
                "the run moved in XY before retracting the probe");
        }

        /// <summary>
        /// A door park aborts GRBL's probe cycle, so the point it interrupted is measured
        /// again on resume - rule `remeasure-probe-point-after-operator-resume` on the door's path. A point
        /// already recorded keeps its height, because recording took it off the queue.
        /// </summary>
        [Fact]
        public async Task DoorDuringProbe_PreservesMeasuredPoints()
        {
            using var machine = CreateMockMachine();
            machine.MachinePosition = new Vector3(-50, -50, Constants.SafeClearanceZ);

            var controller = CreateController(machine);
            controller.Options = new ProbeOptions { SafeHeight = machine.WorkPosition.Z };

            var grid = new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20));
            controller.LoadGrid(grid);

            // The first probe reaches the board; the enclosure opens during the second.
            int probes = 0;
            machine.LineSent += line =>
            {
                if (!line.StartsWith(GrblProtocol.CmdProbeToward))
                {
                    return;
                }

                probes++;
                if (probes == 1)
                {
                    machine.SimulateProbeFinished(new Vector3(0, 0, MeasuredHeight), true);
                }
                else if (probes == 2)
                {
                    machine.SimulateDoorClosedAndHolding();
                }
            };

            controller.UserInputRequired += request =>
                request.OnResponse(ControllerConstants.OptionAbort);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await controller.StartAsync(cts.Token);
            }
            catch
            {
                // Aborting at the door ends the run; the measured point is what this checks.
            }

            Assert.True(probes >= 2, "the run did not reach a second point");

            // A skipped point also leaves the queue, so the recorded height itself has to be
            // checked. The interrupted point stays queued: a door park aborts GRBL's probe
            // cycle and measures nothing.
            var measured = System.Linq.Enumerable.ToList(grid.MeasuredNodes());
            Assert.Equal(MeasuredHeight, Assert.Single(measured).Height, 3);
            Assert.Equal(grid.TotalPoints - 1, grid.RemainingCount);
        }

        /// <summary>
        /// A move sent while GRBL holds at the door sits in its planner and runs when the
        /// hold is released, so the tool rises as the operator clears the door rather than at
        /// the stop.
        /// </summary>
        [Fact]
        public async Task ProbeStopAtDoor_QueuesNoRetract()
        {
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateAjar);
            machine.MachinePosition = new Vector3(-50, -50, -5);
            machine.WorkOffset = new Vector3(-50, -50, -5);

            var controller = CreateController(machine);
            controller.LoadGrid(ProbeGrid.ForJob(new Vector2(0, 0), new Vector2(10, 10), 10.0, 5.0));
            controller.UserInputRequired += request => request.OnResponse(ControllerConstants.OptionAbort);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await controller.StartAsync(cts.Token);
            }
            catch
            {
                // The abort ends the run; the retract is what is under test.
            }

            // The retract is G90 then a rapid to the safe height; neither may go out behind
            // a door hold.
            Assert.DoesNotContain(machine.SentCommands,
                c => c.StartsWith(GrblProtocol.CmdRapidMove + " Z"));
        }

        /// <summary>
        /// A run waiting on the enclosure prompt still holds the machine. Derived from
        /// IsActive, which excludes WaitingForUserInput, this reads false at the prompt, and
        /// the browser takes that for a finished run and tears the probe screen down with the
        /// tool at probe depth.
        /// </summary>
        [Fact]
        public async Task AProbeWaitingOnTheDoorPrompt_StillReportsItIsMeasuring()
        {
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateClosed);
            machine.MachinePosition = new Vector3(-50, -50, -5);
            machine.WorkOffset = new Vector3(-50, -50, -5);

            var controller = CreateController(machine);
            controller.LoadGrid(ProbeGrid.ForJob(new Vector2(0, 0), new Vector2(10, 10), 10.0, 5.0));

            bool measuringWhenAsked = false;
            controller.UserInputRequired += request =>
            {
                measuringWhenAsked = controller.IsMeasuringGrid;
                request.OnResponse(ControllerConstants.OptionAbort);
            };

            using var cts = new CancellationTokenSource(ControllerConstants.DoorResumeTimeoutMs + 2000);
            try
            {
                await controller.StartAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Cancelling is the only exit when the door is never closed.
            }

            Assert.True(measuringWhenAsked,
                "the run held the machine at the door and the screens were told it had stopped");
        }

        /// <summary>
        /// The same for a trace: derived from IsActive, both flags read the wrong way at the
        /// enclosure prompt and the browser draws the grid-probe window over a trace.
        /// </summary>
        [Fact]
        public async Task ATraceWaitingOnTheDoorPrompt_StillReportsItIsTracing()
        {
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateClosed);
            machine.MachinePosition = new Vector3(-50, -50, -5);
            machine.WorkOffset = new Vector3(-50, -50, -5);

            var controller = CreateController(machine);
            controller.LoadGrid(ProbeGrid.ForJob(new Vector2(0, 0), new Vector2(10, 10), 10.0, 5.0));

            bool tracingWhenAsked = false;
            bool measuringWhenAsked = true;
            controller.UserInputRequired += request =>
            {
                tracingWhenAsked = controller.IsTracingOutline;
                measuringWhenAsked = controller.IsMeasuringGrid;
                request.OnResponse(ControllerConstants.OptionAbort);
            };

            using var cts = new CancellationTokenSource(ControllerConstants.DoorResumeTimeoutMs + 2000);
            try
            {
                await controller.TraceOutlineAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Cancelling is the only exit when the door is never closed.
            }
            catch (InvalidOperationException)
            {
                // An abort at the prompt ends the trace the same way.
            }

            Assert.True(tracingWhenAsked, "the trace stopped reporting itself at the door prompt");
            Assert.False(measuringWhenAsked, "a trace at the door prompt reported itself as a grid probe");
        }
    }
}
