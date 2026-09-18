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
    // MillingController driven end to end through StartAsync: M6 detection, the M0/M1/M2/M30
    // stops, the settling phase and the enclosure door. Tests that need a running stream use
    // FakeMachine, which simulates motion and GRBL replies; the rest use MockMachine, whose
    // state each test sets directly.
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

        // FakeMachine's simulated move and homing time, tuned down so a run costs only the
        // fixed Core-side settle delays that no test double can shorten.
        private const double FastMoveSpeedMmPerSec = 10000.0;
        private const int FastHomingDurationMs = 50;

        // Far above the time a run needs, so a controller that never reaches the event under
        // test fails on a timeout instead of hanging the suite.
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
        /// Long enough that the stream is still running when a test pauses or opens the door.
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
        /// Bounded so a controller that never fires ToolChangeDetected fails the test with a
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

        /// <summary>Returns when the condition holds or the deadline passes; never throws.</summary>
        private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs)
        {
            var stopwatch = Stopwatch.StartNew();
            while (!condition() && stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                await Task.Delay(TestPollIntervalMs);
            }
        }

        /// <summary>
        /// Models a refused tool change: the token is cancelled and Resume() is never called.
        /// Returns with the controller Reset() to Idle, so the caller can start a second run.
        /// </summary>
        private static async Task AbortDuringToolChangeAsync(MillingController controller, int toolNumber)
        {
            using var cts = new CancellationTokenSource();
            var run = controller.StartAsync(cts.Token);
            try
            {
                var toolChange = await WaitForToolChangeOrTimeoutAsync(controller, ToolChangeWaitTimeoutMs);
                Assert.Equal(toolNumber, toolChange.ToolNumber);

                // MillingController transitions to Paused before firing the event, so this
                // returns at once. It guards the cancel below if that order ever changes.
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
                // Cancel so the run reaches a terminal state instead of staying Paused.
                cts.Cancel();
                await run;
            }

            Assert.Equal(SingleRunToolNumber, detected.ToolNumber);
            Assert.Equal(ToolChangeLineIndex, detected.LineNumber);
        }

        [Fact]
        public void M6Pattern_MatchesVariousFormats()
        {
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
                // Assert against GCodeParser, not a copy of the regex: a copy can keep passing
                // while the production recognizer is wrong.
                Assert.Equal(shouldMatch, GCodeParser.IsM6Line(line));

                if (shouldMatch)
                {
                    var (toolNumber, _) = GCodeParser.FindToolInfo(new[] { line }, 0);
                    Assert.Equal(expectedTool, toolNumber ?? 0);
                }
            }
        }

        // An aborted tool change must not leave the controller unusable for the rest of the
        // session: IsPaused derives from ControllerState, which Reset() returns to Idle.

        [Fact]
        public async Task MillingAfterAnAbortedToolChange_StillDetectsTheNextOne()
        {
            using var machine = CreateFastFakeMachine(FileWithToolChange(FirstAbortedToolNumber));
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { RequireHoming = false }
            };

            await AbortDuringToolChangeAsync(controller, FirstAbortedToolNumber);

            // Reuse the controller from the abort above: M6 detection must still fire on this
            // second run.
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

            // Reuse the controller from the abort above, now with a file that has no M6:
            // completion must still be detected.
            machine.LoadFile(FileWithoutToolChange);
            using var secondRunCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(CompletionWaitTimeoutMs));
            await controller.StartAsync(secondRunCts.Token);

            Assert.Equal(ControllerState.Completed, controller.State);
        }

        // Machine.SetFile marks M0/M1/M2/M30 as pause lines alongside M6, so the stream stops
        // at all of them. Each stop must end in a prompt or a completed job, or the run sits
        // mid-cut with nothing on screen.

        private const string UserInputTimeoutMessage = "Timed out waiting for UserInputRequired.";

        /// <summary>
        /// Bounded so a controller that never fires UserInputRequired fails the test with a
        /// TimeoutException instead of hanging it.
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
        /// Queues every prompt a run raises, so a test can subscribe before StartAsync. The
        /// one-shot waiter above loses a prompt raised before it subscribes, and the door
        /// prompt is the first one raised.
        /// </summary>
        private sealed class PromptRecorder
        {
            private readonly ConcurrentQueue<UserInputRequest> _requests = new();

            public PromptRecorder(MillingController controller)
            {
                controller.UserInputRequired += _requests.Enqueue;
            }

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

        // Opening the enclosure makes GRBL park and hold, and closing it does not end the hold
        // (GetDoorState returns WaitingForResume for Door:0). A run waits out an open door and
        // prompts for a closed one, because the cycle start that releases it restarts the
        // spindle.

        [Fact]
        public async Task MovingMachineAtSettleTimeout_FailsRunWithReason()
        {
            // Nothing checks for a moving machine before settling, because such a check also
            // blocks a door hold the controller can release. The settling phase is what
            // reports it.
            var machine = CreateMachineWithFile("G21", "G90", "G1 X1 Y1 F100");
            machine.Status = GrblProtocol.StatusRun;

            string? reported = await RunAndCaptureErrorAsync(machine);

            Assert.Equal(ErrorMachineNotSettled, reported);
        }

        [Fact]
        public async Task AnAlarmBeforeTheJob_ReportsThatItMustBeCleared()
        {
            var machine = CreateMachineWithFile("G21", "G90", "G1 X1 Y1 F100");
            machine.Status = GrblProtocol.StatusAlarm;

            string? reported = await RunAndCaptureErrorAsync(machine);

            Assert.Equal(ErrorAlarmBeforeStart, reported);
        }

        [Fact]
        public async Task ResumeAtADoorHold_IsRefusedNotQueued()
        {
            // GRBL holding at the door takes lines into its planner and runs them when the
            // hold is released, so restarting the stream here fills the buffer and leaves every
            // screen reporting a running job. The refusal is reported, not thrown: the terminal
            // calls Resume from a key press with nothing to catch it.
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
            // settling prompts again and restarts the settle timeout.
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
        public async Task DoorRelease_RestartsSettleTimeout()
        {
            // The time the operator spends at the enclosure must not count against the settle
            // timeout, or a slow answer fails a machine that is already stopped.
            using var machine = CreateFastFakeMachine(FileWithoutToolChange);

            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { RequireHoming = false, SettleTimeoutMs = SettleWaitMs }
            };

            var prompts = new PromptRecorder(controller);
            string? reported = null;
            controller.ErrorOccurred += error => reported = error.Message;

            var run = controller.StartAsync();

            await WaitUntilAsync(() => controller.State == ControllerState.Initializing,
                StateTransitionWaitTimeoutMs);
            machine.SimulateDoorClosedAndHolding();

            var request = await prompts.NextAsync(SettleWaitMs);

            // Answer only after the whole settle timeout would have run out.
            await Task.Delay(SettleWaitMs + TestPollIntervalMs * 10);
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
            // GRBL holds and the stream stalls. Neither screen's resume releases a door hold,
            // so without the prompt the only exits are Stop or a soft reset.
            using var machine = CreateFastFakeMachine(ALongCut);

            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { RequireHoming = false }
            };

            // Open the door off the stream's own progress rather than a delay, so the cut is
            // always still running when it happens.
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
        /// An M0 pause leaves the tool down, so this is where the operator opens the enclosure.
        /// Answering the pause with the door closed is consent to restart, so the hold is
        /// released without a second prompt for the same door.
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
        /// A run that ends at the door queues no retract: GRBL keeps the move in its planner
        /// and runs it when the operator clears the hold.
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

            // G53 G0 Z is the machine-coordinate retract; the work-coordinate form never
            // appears on this path, so matching on it would assert nothing.
            Assert.DoesNotContain(machine.SentCommands,
                c => c.StartsWith(GrblProtocol.CmdMachineCoords, StringComparison.Ordinal)
                    && c.Contains(" Z", StringComparison.Ordinal));
        }

        /// <summary>How long a settle test spends on a machine that never settles.</summary>
        private const int ShortSettleMs = 1_500;

        /// <summary>
        /// Allow at least PostIdleSettleMs of uninterrupted idle; a shorter timeout
        /// would fail even when the machine settles.
        /// </summary>
        private const int SettleWaitMs = PostIdleSettleMs + 3_000;

        /// <summary>
        /// Runs a job that is expected to fail and returns the message reported.
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
                // The refusal under test; its text arrives through ErrorOccurred.
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

            // The door prompt is the first thing a run raises, so the subscription has to be
            // in place before it starts.
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

            // An open door gives the operator nothing to answer, so the run waits without
            // prompting. GRBL would ignore a cycle start here anyway.
            await Task.Delay(DoorResumeTimeoutMs + StateTransitionWaitTimeoutMs);
            Assert.True(MachineWait.IsDoor(machine));
            Assert.Empty(prompts.All);

            // The run prompts once the door is closed, because the cycle start restarts the
            // spindle.
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
        /// A move sent while GRBL holds at the door sits in its planner and runs when the hold
        /// is released, so the tool would rise as the operator cleared the door.
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

            // Cancelling is how Stop reaches the run; cleanup runs as it unwinds.
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
            // The M2 sits mid-file with a line after it, so a controller that only finishes at
            // end-of-file waits forever for a line the program never runs.
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
            // PauseFileOnHold is a feed-hold preference. Turning it off must not skip the tool
            // change, or the job carries on cutting with the wrong tool.
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

        [Fact]
        public async Task StopAsync_WhenIdle_DoesNothing()
        {
            var machine = CreateMachineWithFile("G0 X0");
            var controller = new MillingController(machine);

            await controller.StopAsync();

            Assert.Empty(machine.SentCommands);
        }
    }
}
