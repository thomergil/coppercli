#nullable enable
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using coppercli.Core.Controllers;
using coppercli.Core.GCode;
using coppercli.Core.Util;
using coppercli.Tests.Fakes;
using Xunit;
using static coppercli.Core.Controllers.ControllerConstants;
using static coppercli.Core.Util.Constants;

namespace coppercli.Tests
{
    /// <summary>
    /// Tests for MillingController workflow behavior.
    /// </summary>
    public class MillingControllerTests
    {
        private MockMachine CreateMachineWithFile(params string[] lines)
        {
            var machine = new MockMachine
            {
                Status = "Idle",
                Connected = true,
                MachinePosition = new Vector3(0, 0, -1),
                WorkPosition = new Vector3(0, 0, 0)
            };
            machine.LoadFile(lines);
            return machine;
        }

        // =========================================================================
        // FakeMachine helpers - drive a real MillingController end to end through
        // StartAsync. Speeds/homing duration are tuned up front so a test only pays
        // for the fixed Core-side delays (5s settle, 1s idle-settle, etc.) that no
        // test double can shorten - not also for FakeMachine's own simulated move and
        // homing time on top of them.
        // =========================================================================

        private const double FastMoveSpeedMmPerSec = 10000.0;
        private const int FastHomingDurationMs = 50;

        // Far above the real time a run needs (dominated by the fixed settle/idle-settle/
        // homing delays above), so a controller that never reaches the event under test
        // fails the test instead of hanging the suite.
        private const int ToolChangeWaitTimeoutMs = 20_000;
        private const int CompletionWaitTimeoutMs = 30_000;
        private const int StateTransitionWaitTimeoutMs = 5_000;
        private const int TestPollIntervalMs = 10;

        private const int ToolChangeLineIndex = 2;
        private const int SingleRunToolNumber = 4;
        private const int FirstAbortedToolNumber = 5;
        private const int SecondRunToolNumber = 6;

        private static FakeMachine CreateFastFakeMachine(params string[] lines)
        {
            var machine = new FakeMachine
            {
                RapidSpeed = FastMoveSpeedMmPerSec,
                FeedSpeed = FastMoveSpeedMmPerSec,
                HomingDurationMs = FastHomingDurationMs,
            };
            machine.LoadFile(lines);
            return machine;
        }

        private static string[] FileWithToolChange(int toolNumber) => new[]
        {
            "G21",
            "G90",
            $"M6 T{toolNumber}",
            "G1 X1 Y1 F100",
        };

        /// <summary>
        /// Enough cutting moves that a test can pause or open the door while the stream is
        /// still running, whatever else the suite is doing.
        /// </summary>
        private static readonly string[] ALongCut =
            new[] { "G21", "G90" }
                .Concat(Enumerable.Range(1, LongCutMoves)
                    .Select(i => GCodeFormat.Inv($"G1 X{i % 10} Y{i % 7} F60")))
                .ToArray();

        private const int LongCutMoves = 200;

        private static readonly string[] FileWithoutToolChange =
        {
            "G21",
            "G90",
            "G1 X1 Y1 F100",
        };

        private const string ToolChangeTimeoutMessage = "Timed out waiting for ToolChangeDetected.";

        /// <summary>
        /// Waits for ToolChangeDetected, bounded so a controller that never fires it -
        /// the regression these tests guard against - fails the test with a
        /// TimeoutException instead of hanging it.
        /// </summary>
        private static async Task<ToolChangeInfo> WaitForToolChangeOrTimeoutAsync(
            MillingController controller, int timeoutMs)
        {
            var tcs = new TaskCompletionSource<ToolChangeInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnToolChange(ToolChangeInfo info) => tcs.TrySetResult(info);

            controller.ToolChangeDetected += OnToolChange;
            try
            {
                return await MachineWait.AwaitReplyOrTimeoutAsync(
                    tcs.Task, timeoutMs, ToolChangeTimeoutMessage, CancellationToken.None);
            }
            finally
            {
                controller.ToolChangeDetected -= OnToolChange;
            }
        }

        /// <summary>Polls until <paramref name="condition"/> is true or the deadline passes.</summary>
        private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs)
        {
            var stopwatch = Stopwatch.StartNew();
            while (!condition() && stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                await Task.Delay(TestPollIntervalMs);
            }
        }

        /// <summary>
        /// Drives a run up to its first M6, then aborts exactly the way an operator
        /// does when they refuse the tool change: cancel the token, and never call
        /// Resume(). Leaves the controller Reset() back to Idle, ready for reuse.
        /// </summary>
        private static async Task AbortDuringToolChangeAsync(MillingController controller, int toolNumber)
        {
            using var cts = new CancellationTokenSource();
            var run = controller.StartAsync(cts.Token);
            try
            {
                var toolChange = await WaitForToolChangeOrTimeoutAsync(controller, ToolChangeWaitTimeoutMs);
                Assert.Equal(toolNumber, toolChange.ToolNumber);

                // MillingController transitions to Paused before firing the event, so
                // this resolves immediately - kept as a guard rather than an
                // assumption, so the cancel below still races nothing if that
                // ordering ever changes.
                await WaitUntilAsync(() => controller.State == ControllerState.Paused, StateTransitionWaitTimeoutMs);
            }
            finally
            {
                cts.Cancel();
                await run;
            }

            Assert.Equal(ControllerState.Cancelled, controller.State);
            controller.Reset();
            Assert.Equal(ControllerState.Idle, controller.State);
        }

        // =========================================================================
        // Initial state tests
        // =========================================================================

        [Fact]
        public void NewController_HasIdleState()
        {
            var machine = new MockMachine();
            var controller = new MillingController(machine);

            Assert.Equal(ControllerState.Idle, controller.State);
            Assert.Equal(MillingPhase.NotStarted, controller.Phase);
        }

        [Fact]
        public void Constructor_WithNullMachine_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new MillingController(null!));
        }

        // =========================================================================
        // M6 detection tests
        // =========================================================================

        [Fact]
        public async Task Milling_DrivenThroughAnM6File_FiresToolChangeDetected()
        {
            using var machine = CreateFastFakeMachine(FileWithToolChange(SingleRunToolNumber));
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { RequireHoming = false }
            };

            using var cts = new CancellationTokenSource();
            var run = controller.StartAsync(cts.Token);

            ToolChangeInfo detected;
            try
            {
                detected = await WaitForToolChangeOrTimeoutAsync(controller, ToolChangeWaitTimeoutMs);
            }
            finally
            {
                // Let the run settle to a terminal state rather than leaving it
                // dangling in Paused for the next test.
                cts.Cancel();
                await run;
            }

            Assert.Equal(SingleRunToolNumber, detected.ToolNumber);
            Assert.Equal(ToolChangeLineIndex, detected.LineNumber);
        }

        [Fact]
        public void M6Pattern_MatchesVariousFormats()
        {
            // Test the M6 detection regex patterns
            var testCases = new[]
            {
                ("M6 T1", true, 1),
                ("M06 T2", true, 2),
                ("m6 t3", true, 3),
                ("  M6 T4  ", true, 4),
                ("M6", true, 0),
                ("G0 X0", false, 0),
            };

            foreach (var (line, shouldMatch, expectedTool) in testCases)
            {
                // Against production, not a copy of it: the copy kept passing while the
                // real recogniser was wrong about "T1 M6".
                Assert.Equal(shouldMatch, GCodeParser.IsM6Line(line));

                if (shouldMatch)
                {
                    var (toolNumber, _) = GCodeParser.FindToolInfo(new[] { line }, 0);
                    Assert.Equal(expectedTool, toolNumber ?? 0);
                }
            }
        }

        // =========================================================================
        // Regression: an aborted tool change must not disable the controller for the
        // rest of the session. IsPaused derives from ControllerState, which Reset()
        // returns to Idle, so "paused" cannot outlive the run that set it.
        // =========================================================================

        [Fact]
        public async Task MillingAfterAnAbortedToolChange_StillDetectsTheNextOne()
        {
            using var machine = CreateFastFakeMachine(FileWithToolChange(FirstAbortedToolNumber));
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { RequireHoming = false }
            };

            await AbortDuringToolChangeAsync(controller, FirstAbortedToolNumber);

            // Same controller instance as the abort above: pins that M6 detection
            // still works on this second run.
            machine.LoadFile(FileWithToolChange(SecondRunToolNumber));
            using var secondRunCts = new CancellationTokenSource();
            var secondRun = controller.StartAsync(secondRunCts.Token);

            ToolChangeInfo secondToolChange;
            try
            {
                secondToolChange = await WaitForToolChangeOrTimeoutAsync(controller, ToolChangeWaitTimeoutMs);
            }
            finally
            {
                secondRunCts.Cancel();
                await secondRun;
            }

            Assert.Equal(SecondRunToolNumber, secondToolChange.ToolNumber);
            Assert.Equal(ToolChangeLineIndex, secondToolChange.LineNumber);
        }

        [Fact]
        public async Task MillingAfterAnAbortedToolChange_StillReachesCompletion()
        {
            using var machine = CreateFastFakeMachine(FileWithToolChange(FirstAbortedToolNumber));
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { RequireHoming = false }
            };

            await AbortDuringToolChangeAsync(controller, FirstAbortedToolNumber);

            // Same controller instance as the abort above, now running a file with no
            // M6 at all: pins that completion detection still works too.
            machine.LoadFile(FileWithoutToolChange);
            using var secondRunCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(CompletionWaitTimeoutMs));
            await controller.StartAsync(secondRunCts.Token);

            Assert.Equal(ControllerState.Completed, controller.State);
        }

        // =========================================================================
        // Operator pause (M0/M1) and program end (M2/M30) tests
        //
        // Machine.SetFile marks M0/M1/M2/M30 as pause lines alongside M6, so the stream
        // stops at all of them. Every kind of stop must produce an outcome the operator
        // can see: a prompt, or a completed job. A stop nobody reacts to is a job frozen
        // mid-cut with nothing on screen to say so.
        // =========================================================================

        private const string UserInputTimeoutMessage = "Timed out waiting for UserInputRequired.";

        /// <summary>
        /// Waits for UserInputRequired, bounded so a controller that never fires it -
        /// the M0/M1 regression these tests guard against - fails with a
        /// TimeoutException instead of hanging the test.
        /// </summary>
        private static async Task<UserInputRequest> WaitForUserInputOrTimeoutAsync(
            MillingController controller, int timeoutMs)
        {
            var tcs = new TaskCompletionSource<UserInputRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnUserInputRequired(UserInputRequest request) => tcs.TrySetResult(request);

            controller.UserInputRequired += OnUserInputRequired;
            try
            {
                return await MachineWait.AwaitReplyOrTimeoutAsync(
                    tcs.Task, timeoutMs, UserInputTimeoutMessage, CancellationToken.None);
            }
            finally
            {
                controller.UserInputRequired -= OnUserInputRequired;
            }
        }

        /// <summary>
        /// Collects every prompt a run raises, so a test can subscribe before the run starts
        /// and read them in order. The one-shot waiter above cannot: a prompt raised before
        /// it subscribes is lost, and the door prompt is raised immediately.
        /// </summary>
        private sealed class PromptRecorder
        {
            private readonly ConcurrentQueue<UserInputRequest> _requests = new();

            public PromptRecorder(MillingController controller)
            {
                controller.UserInputRequired += _requests.Enqueue;
            }

            /// <summary>Every prompt raised so far, for asserting none was.</summary>
            public IReadOnlyCollection<UserInputRequest> All => _requests;

            public async Task<UserInputRequest> NextAsync(int timeoutMs)
            {
                await WaitUntilAsync(() => !_requests.IsEmpty, timeoutMs);

                if (!_requests.TryDequeue(out var request))
                {
                    throw new TimeoutException(UserInputTimeoutMessage);
                }
                return request;
            }
        }

        // =========================================================================
        // Door tests
        //
        // Opening the enclosure makes GRBL park and hold, and closing it does not end the
        // hold (GetDoorState returns WaitingForResume for Door:0). An open door is waited
        // out; a closed one raises a prompt, because only then does a cycle start do
        // anything, and it restarts the spindle. A job start that refuses the state leaves
        // the operator with no way to send that cycle start.
        // =========================================================================

        [Fact]
        public async Task MachineThatWillNotSettle_FailsTheRunWithAReason()
        {
            // The readiness gate that used to refuse a moving machine was removed, because
            // it also refused a door hold the controller can release. Settling catches it
            // now, so it has to report what it found.
            var machine = CreateMachineWithFile("G21", "G90", "G1 X1 Y1 F100");
            machine.Status = GrblProtocol.StatusRun;

            string? reported = await RunAndCaptureErrorAsync(machine);

            Assert.Equal(ErrorMachineNotSettled, reported);
        }

        [Fact]
        public async Task AnAlarmBeforeTheJob_SaysToClearIt()
        {
            var machine = CreateMachineWithFile("G21", "G90", "G1 X1 Y1 F100");
            machine.Status = GrblProtocol.StatusAlarm;

            string? reported = await RunAndCaptureErrorAsync(machine);

            Assert.Equal(ErrorAlarmBeforeStart, reported);
        }

        [Fact]
        public async Task ResumeAtADoorHold_IsRefusedNotQueued()
        {
            // A machine holding at the door takes lines into its planner and runs them when
            // the hold is released. Restarting the stream here would leave a full buffer and
            // three screens reporting a running job. Reported rather than thrown: the
            // terminal calls Resume straight from a key press with nothing to catch it.
            using var machine = CreateFastFakeMachine(FileWithoutToolChange);

            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { RequireHoming = false }
            };

            var run = controller.StartAsync();
            await WaitUntilAsync(() => controller.State == ControllerState.Running,
                CompletionWaitTimeoutMs);

            controller.Pause();
            await WaitUntilAsync(() => controller.State == ControllerState.Paused,
                StateTransitionWaitTimeoutMs);

            machine.SimulateDoorClosedAndHolding();

            ControllerError? refused = null;
            controller.ErrorOccurred += error => refused = error;

            controller.Resume();

            Assert.Equal(ErrorDoorBlocksResume, refused?.Message);
            Assert.False(refused?.IsFatal, "a door hold leaves the run paused, not failed");
            Assert.Equal(ControllerState.Paused, controller.State);

            machine.SimulateDoorReleased();
            await controller.StopAsync();
            await AwaitRunOutcomeAsync(run);
        }

        [Fact]
        public async Task DoorOpenedWhileSettling_IsPromptedNotTimedOut()
        {
            // The enclosure prompt is raised once before settling. A door opened during
            // settling used to cost the whole settle timeout and then fail; now it prompts
            // again and restarts the timeout.
            using var machine = CreateFastFakeMachine(FileWithoutToolChange);

            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { RequireHoming = false, SettleTimeoutMs = ShortSettleMs }
            };

            var prompts = new PromptRecorder(controller);
            var run = controller.StartAsync();

            await WaitUntilAsync(() => controller.State == ControllerState.Initializing,
                StateTransitionWaitTimeoutMs);
            machine.SimulateDoorClosedAndHolding();

            var request = await prompts.NextAsync(ShortSettleMs * 4);
            Assert.Equal(DoorHoldingPrompt, request.Message);
            request.OnResponse(OptionContinue);

            await WaitUntilAsync(() => !MachineWait.IsDoor(machine), StateTransitionWaitTimeoutMs);
            Assert.False(MachineWait.IsDoor(machine));

            await controller.StopAsync();
            await AwaitRunOutcomeAsync(run);
        }

        [Fact]
        public async Task TheSettleTimeout_RestartsAfterTheDoorIsHandled()
        {
            // The operator's time at the enclosure must not count against the machine's
            // settle timeout, or a slow walk back reports "the machine did not stop moving"
            // for a machine that is stopped.
            using var machine = CreateFastFakeMachine(FileWithoutToolChange);

            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { RequireHoming = false, SettleTimeoutMs = SettleableBudgetMs }
            };

            var prompts = new PromptRecorder(controller);
            string? reported = null;
            controller.ErrorOccurred += error => reported = error.Message;

            var run = controller.StartAsync();

            await WaitUntilAsync(() => controller.State == ControllerState.Initializing,
                StateTransitionWaitTimeoutMs);
            machine.SimulateDoorClosedAndHolding();

            var request = await prompts.NextAsync(SettleableBudgetMs);

            // Answer only after the whole settle timeout would have run out.
            await Task.Delay(SettleableBudgetMs + TestPollIntervalMs * 10);
            request.OnResponse(OptionContinue);

            await WaitUntilAsync(() => controller.State == ControllerState.Running,
                CompletionWaitTimeoutMs);

            Assert.Equal(ControllerState.Running, controller.State);
            Assert.Null(reported);

            await controller.StopAsync();
            await AwaitRunOutcomeAsync(run);
        }

        [Fact]
        public async Task DoorOpenedMidCut_IsPromptedNotStalled()
        {
            // GRBL holds and the stream stalls. Neither screen's resume can release a door
            // hold, so without this the only exits are Stop or a soft reset, and the job is
            // lost.
            // A cut long enough that the stream is still running when the door opens. A
            // three-line file can finish first, making the result depend on timing.
            using var machine = CreateFastFakeMachine(ALongCut);

            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { RequireHoming = false }
            };

            // The door opens off the stream's own progress rather than a delay, so the cut
            // is always still running when it happens.
            machine.FilePositionChanged += () =>
            {
                if (machine.FilePosition == DoorOpensAtLine)
                {
                    machine.SimulateDoorClosedAndHolding();
                }
            };

            var prompts = new PromptRecorder(controller);
            var run = controller.StartAsync();
            try
            {
                var request = await prompts.NextAsync(CompletionWaitTimeoutMs);
                Assert.Equal(DoorHoldingPrompt, request.Message);
                Assert.True(MachineWait.IsDoor(machine));

                request.OnResponse(OptionContinue);

                await WaitUntilAsync(() => !MachineWait.IsDoor(machine), StateTransitionWaitTimeoutMs);
                Assert.False(MachineWait.IsDoor(machine));
            }
            finally
            {
                await controller.StopAsync();
                await AwaitRunOutcomeAsync(run);
            }
        }

        /// <summary>Partway through <see cref="ALongCut"/>, so the stream is still running.</summary>
        private const int DoorOpensAtLine = 10;

        /// <summary>
        /// The M0 prompt tells the operator the tool is still down, so this is when they open
        /// the enclosure. Closing it and answering the pause is consent to restart, so the
        /// hold is released without asking about the same door again.
        /// </summary>
        [Fact]
        public async Task ContinuingPastAnM0_ReleasesTheDoorWithoutAskingAgain()
        {
            using var machine = CreateFastFakeMachine("G21", "G90", "M0", "G1 X1 Y1 F100");

            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { RequireHoming = false }
            };

            var prompts = new PromptRecorder(controller);
            var run = controller.StartAsync();
            try
            {
                var pause = await prompts.NextAsync(CompletionWaitTimeoutMs);

                // The operator opens the enclosure to do what the pause asked, closes it,
                // then answers.
                machine.SimulateDoorClosedAndHolding();
                pause.OnResponse(OptionContinue);

                await WaitUntilAsync(() => controller.HasFinished, CompletionWaitTimeoutMs);
                Assert.Equal(ControllerState.Completed, controller.State);
                Assert.DoesNotContain(prompts.All, p => p.IsDoorPrompt);
            }
            finally
            {
                await AwaitRunOutcomeAsync(run);
            }
        }

        /// <summary>
        /// Abandoning the enclosure prompt ends the run, and a run that ends at the door
        /// queues no retract: GRBL keeps the move in its planner and runs it the moment the
        /// operator clears the hold, with nobody watching.
        /// </summary>
        [Fact]
        public async Task AbandoningTheDoorPrompt_QueuesNoRetract()
        {
            using var machine = CreateFastFakeMachine(FileWithoutToolChange);

            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { RequireHoming = false, SettleTimeoutMs = ShortSettleMs }
            };

            var prompts = new PromptRecorder(controller);
            var run = controller.StartAsync();

            await WaitUntilAsync(() => controller.State == ControllerState.Initializing,
                StateTransitionWaitTimeoutMs);
            machine.SimulateDoorClosedAndHolding();

            var request = await prompts.NextAsync(ShortSettleMs * 4);
            Assert.Equal(DoorHoldingPrompt, request.Message);

            machine.ClearSentCommands();
            request.OnResponse(OptionAbort);
            await AwaitRunOutcomeAsync(run);

            // G53 G0 Z, the machine-coordinate retract: the work-coordinate form never
            // appears on this path, so matching on it would assert nothing.
            Assert.DoesNotContain(machine.SentCommands,
                c => c.StartsWith(GrblProtocol.CmdMachineCoords, StringComparison.Ordinal)
                    && c.Contains(" Z", StringComparison.Ordinal));
        }

        /// <summary>Time a settle test is willing to spend on a machine that never settles.</summary>
        private const int ShortSettleMs = 1_500;

        /// <summary>
        /// A timeout a machine can settle within: the phase needs PostIdleSettleMs of
        /// unbroken idle, so anything shorter fails however the machine behaves.
        /// </summary>
        private const int SettleableBudgetMs = PostIdleSettleMs + 3_000;

        /// <summary>
        /// Runs a job until it fails and returns the message shown. The run is expected to
        /// fail: these cover the states the settling phase refuses.
        /// </summary>
        private static async Task<string?> RunAndCaptureErrorAsync(MockMachine machine)
        {
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { RequireHoming = false, SettleTimeoutMs = ShortSettleMs }
            };

            string? reported = null;
            controller.ErrorOccurred += error => reported = error.Message;

            await AwaitRunOutcomeAsync(controller.StartAsync());
            Assert.Equal(ControllerState.Failed, controller.State);
            return reported;
        }

        private static async Task AwaitRunOutcomeAsync(Task run)
        {
            try
            {
                await run;
            }
            catch (InvalidOperationException)
            {
                // The refusal the test is about; its text arrives through ErrorOccurred.
            }
            catch (OperationCanceledException)
            {
            }
        }

        [Fact]
        public async Task JobStartAtClosedDoor_PromptsThenReleasesTheHold()
        {
            using var machine = CreateFastFakeMachine(FileWithoutToolChange);
            machine.SimulateDoorClosedAndHolding();

            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { RequireHoming = false }
            };

            // The door prompt is the first thing a run does, so the subscription has to
            // be in place before it starts.
            var prompts = new PromptRecorder(controller);
            var run = controller.StartAsync();

            var request = await prompts.NextAsync(StateTransitionWaitTimeoutMs);
            Assert.Contains(OptionContinue, request.Options);
            Assert.True(MachineWait.IsDoor(machine), "the machine should still be holding while it asks");

            request.OnResponse(OptionContinue);

            await WaitUntilAsync(() => !MachineWait.IsDoor(machine), StateTransitionWaitTimeoutMs);
            Assert.False(MachineWait.IsDoor(machine));

            await controller.StopAsync();
            try { await run; } catch (OperationCanceledException) { }
        }

        [Fact]
        public async Task JobStartAtOpenDoor_Waits_ThenPromptsWhenClosed()
        {
            using var machine = CreateFastFakeMachine(FileWithoutToolChange);
            machine.SimulateDoorOpen();

            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { RequireHoming = false }
            };

            var prompts = new PromptRecorder(controller);
            var run = controller.StartAsync();

            // An open door has nothing to answer, so the run waits without prompting. GRBL
            // would ignore a cycle start here anyway.
            await Task.Delay(DoorResumeTimeoutMs + StateTransitionWaitTimeoutMs);
            Assert.True(MachineWait.IsDoor(machine));
            Assert.Empty(prompts.All);

            // A closed door raises a prompt, because the cycle start restarts the spindle.
            machine.SimulateDoorClosedAndHolding();

            var asked = await prompts.NextAsync(DoorResumeTimeoutMs * 3);
            Assert.Equal(DoorHoldingPrompt, asked.Message);
            asked.OnResponse(OptionContinue);

            await WaitUntilAsync(() => !MachineWait.IsDoor(machine), StateTransitionWaitTimeoutMs);
            Assert.False(MachineWait.IsDoor(machine));

            await controller.StopAsync();
            try { await run; } catch (OperationCanceledException) { }
        }

        /// <summary>
        /// A move sent while GRBL holds at the door sits in its planner and runs when the
        /// hold is released, so the tool would rise as the operator cleared the door rather
        /// than at the stop. The stop reports the door because its soft reset clears it.
        /// </summary>
        [Fact]
        public async Task StopAtDoor_QueuesNoRetract()
        {
            using var machine = CreateFastFakeMachine(FileWithoutToolChange);
            machine.SimulateDoorOpen();

            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { RequireHoming = false }
            };

            using var cts = new CancellationTokenSource();
            var run = controller.StartAsync(cts.Token);
            await WaitUntilAsync(() => controller.IsRunInProgress, StateTransitionWaitTimeoutMs);
            machine.ClearSentCommands();

            // Cancelling is how a Stop reaches the run, and cleanup runs as it unwinds.
            cts.Cancel();
            try { await run; } catch (OperationCanceledException) { }

            Assert.DoesNotContain(machine.SentCommands,
                c => c.StartsWith(GrblProtocol.CmdMachineCoords));
        }

        [Fact]
        public async Task BareM0MidFile_PromptsOperatorAndCompletesAfterContinue()
        {
            using var machine = CreateFastFakeMachine("G21", "G90", "M0", "G1 X1 Y1 F100");
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { RequireHoming = false }
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(CompletionWaitTimeoutMs));
            var run = controller.StartAsync(cts.Token);

            var request = await WaitForUserInputOrTimeoutAsync(controller, ToolChangeWaitTimeoutMs);
            Assert.Contains(OptionContinue, request.Options);

            request.OnResponse(OptionContinue);
            await run;

            Assert.Equal(ControllerState.Completed, controller.State);
        }

        [Fact]
        public async Task M2NotOnFinalLine_CompletesRatherThanHanging()
        {
            // The M2 sits mid-file - a trailing line follows it - so a controller that
            // only knows how to finish at true end-of-file would sit here forever,
            // waiting for a line the program never meant to run.
            using var machine = CreateFastFakeMachine("G21", "G90", "M2", "G1 X1 Y1 F100");
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { RequireHoming = false }
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(CompletionWaitTimeoutMs));
            await controller.StartAsync(cts.Token);

            Assert.Equal(ControllerState.Completed, controller.State);
        }

        [Fact]
        public async Task M6_PausesForToolChangeEvenWhenPauseFileOnHoldIsFalse()
        {
            // PauseFileOnHold is a feed-hold preference. Turning it off must not make a
            // tool change silently swallowed - the job would carry on cutting with the
            // wrong tool.
            using var machine = CreateFastFakeMachine(FileWithToolChange(SingleRunToolNumber));
            machine.PauseFileOnHold = false;
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { RequireHoming = false }
            };

            using var cts = new CancellationTokenSource();
            var run = controller.StartAsync(cts.Token);

            ToolChangeInfo detected;
            try
            {
                detected = await WaitForToolChangeOrTimeoutAsync(controller, ToolChangeWaitTimeoutMs);
            }
            finally
            {
                cts.Cancel();
                await run;
            }

            Assert.Equal(SingleRunToolNumber, detected.ToolNumber);
        }

        // =========================================================================
        // Progress tests
        // =========================================================================

        [Fact]
        public void LinesCompleted_ReflectsFilePosition()
        {
            var machine = CreateMachineWithFile("G0 X0", "G0 X10", "G0 X20");
            machine.FilePosition = 2;
            var controller = new MillingController(machine);

            Assert.Equal(2, controller.LinesCompleted);
        }

        [Fact]
        public void TotalLines_ReflectsFileCount()
        {
            var machine = CreateMachineWithFile("G0 X0", "G0 X10", "G0 X20");
            var controller = new MillingController(machine);

            Assert.Equal(3, controller.TotalLines);
        }

        // =========================================================================
        // StopAsync tests
        // =========================================================================

        [Fact]
        public async Task StopAsync_WhenIdle_DoesNothing()
        {
            // StopAsync on an idle controller is a no-op (never started)
            var machine = CreateMachineWithFile("G0 X0");
            var controller = new MillingController(machine);

            await controller.StopAsync();

            // No commands sent - controller was never running
            Assert.Empty(machine.SentCommands);
        }
    }
}
