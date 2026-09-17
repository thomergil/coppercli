using coppercli.Core.Util;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using coppercli.Core.Communication;
using coppercli.Core.Controllers;
using coppercli.Tests.Fakes;
using Xunit;
using static coppercli.Core.Controllers.ControllerConstants;

namespace coppercli.Tests
{
    /// <summary>
    /// Tests for ControllerBase FSM transitions and event handling.
    /// </summary>
    public class ControllerBaseTests
    {
        /// <summary>
        /// Added to a door budget so a test that waits one out is not racing its own deadline.
        /// </summary>
        private const int DoorTestGraceMs = 2000;

        /// <summary>
        /// Concrete implementation for testing abstract ControllerBase.
        /// </summary>
        private class TestController : ControllerBase
        {
            /// <summary>The door helpers are not under test here, so this drives an idle one.</summary>
            protected override IMachine Machine => Fake;

            /// <summary>The same machine, for a test that needs to put it at the door.</summary>
            public MockMachine Fake { get; } = new MockMachine();

            public bool RunWasCalled { get; private set; }
            public bool CleanupWasCalled { get; private set; }
            public Exception? ExceptionToThrow { get; set; }
            public TaskCompletionSource<bool>? RunBlocker { get; set; }
            public int ResetRunStateCallCount { get; private set; }

            /// <summary>Prompts RunAsync raises back to back, with nothing awaited between.</summary>
            public string[] PromptsToAsk { get; set; } = Array.Empty<string>();

            /// <summary>Makes RunAsync return while still Running, the way a subclass does
            /// when one of its exits forgets the terminal transition.</summary>
            public bool ReturnWithoutFinishing { get; set; }

            /// <summary>Emitted once from RunAsync when set, so the event has something to carry.</summary>
            public ProgressInfo? ProgressToEmit { get; set; }

            /// <summary>
            /// Snapshot of <see cref="ResetRunStateCallCount"/> taken on RunAsync's very
            /// first line, before it does anything else - lets a test tell whether
            /// ResetRunState already ran by the time RunAsync started, rather than only
            /// by the time it finished.
            /// </summary>
            public int? ResetRunStateCallCountAtRunStart { get; private set; }

            protected override async Task RunAsync(CancellationToken ct)
            {
                ResetRunStateCallCountAtRunStart = ResetRunStateCallCount;
                RunWasCalled = true;

                if (ProgressToEmit != null)
                {
                    EmitProgress(ProgressToEmit);
                }

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                foreach (var title in PromptsToAsk)
                {
                    await RequestUserInputAsync(title, title, new[] { "Continue" }, ct);
                }

                TransitionTo(ControllerState.Running);

                if (RunBlocker != null)
                {
                    await RunBlocker.Task;
                }

                if (ReturnWithoutFinishing)
                {
                    return;
                }

                TransitionTo(ControllerState.Completing);
                TransitionTo(ControllerState.Completed);
            }

            protected override Task CleanupAsync()
            {
                CleanupWasCalled = true;
                return Task.CompletedTask;
            }

            /// <inheritdoc/>
            protected override void ResetRunState()
            {
                ResetRunStateCallCount++;
            }

            // Expose protected method for testing
            public void TestTransitionTo(ControllerState state) => TransitionTo(state);
        }

        // =========================================================================
        // Initial state tests
        // =========================================================================

        [Fact]
        public void NewController_StartsInIdleState()
        {
            var controller = new TestController();
            Assert.Equal(ControllerState.Idle, controller.State);
        }

        // =========================================================================
        // A finished task and a finished run mean the same thing
        // =========================================================================

        /// <summary>
        /// A run that returns without transitioning leaves the controller claiming the
        /// machine with no way back, which every front end reads as still running.
        /// </summary>
        [Fact]
        public async Task ARunThatReturnsWithoutFinishing_StillEndsTheRun()
        {
            var controller = new TestController { ReturnWithoutFinishing = true };

            await controller.StartAsync();

            Assert.True(controller.HasFinished);
            Assert.False(controller.IsRunInProgress);
        }

        /// <summary>Cancelling reports Cancelled, not a failure.</summary>
        [Fact]
        public async Task ARunCancelledWithoutFinishing_EndsAsCancelled()
        {
            var controller = new TestController { ReturnWithoutFinishing = true };
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await controller.StartAsync(cts.Token);

            Assert.Equal(ControllerState.Cancelled, controller.State);
        }

        /// <summary>
        /// Stop, then start again. Releasing returns the controller to Idle whatever state
        /// the run left behind.
        /// </summary>
        [Fact]
        public async Task AfterReleasing_TheNextRunCanStart()
        {
            var controller = new TestController { ReturnWithoutFinishing = true };
            await controller.StartAsync();

            await controller.ReleaseAsync();
            Assert.Equal(ControllerState.Idle, controller.State);

            controller.ReturnWithoutFinishing = false;
            await controller.StartAsync();

            Assert.Equal(ControllerState.Completed, controller.State);
        }

        /// <summary>
        /// Reset alone throws on a controller that still claims a run, so it is not enough
        /// on its own. Releasing stops the run first and always reaches Idle.
        /// </summary>
        [Fact]
        public async Task ReleasingAControllerThatStillClaimsARun_StillReachesIdle()
        {
            var controller = new TestController();
            controller.TestTransitionTo(ControllerState.Initializing);
            controller.TestTransitionTo(ControllerState.Running);

            Assert.Throws<InvalidControllerStateException>(() => controller.Reset());

            await controller.ReleaseAsync();

            Assert.Equal(ControllerState.Idle, controller.State);
        }

        /// <summary>Releasing an idle controller is a no-op, not an error.</summary>
        [Fact]
        public async Task ReleasingAnIdleController_DoesNothing()
        {
            var controller = new TestController();

            await controller.ReleaseAsync();

            Assert.Equal(ControllerState.Idle, controller.State);
        }

        // =========================================================================
        // StartAsync tests
        // =========================================================================

        [Fact]
        public async Task StartAsync_TransitionsToInitializing()
        {
            var controller = new TestController();
            var states = new List<ControllerState>();
            controller.StateChanged += s => states.Add(s);

            await controller.StartAsync();

            Assert.Contains(ControllerState.Initializing, states);
        }

        [Fact]
        public async Task StartAsync_CallsRunAsync()
        {
            var controller = new TestController();

            await controller.StartAsync();

            Assert.True(controller.RunWasCalled);
        }

        [Fact]
        public async Task StartAsync_CompletesSuccessfully()
        {
            var controller = new TestController();

            await controller.StartAsync();

            Assert.Equal(ControllerState.Completed, controller.State);
        }

        [Fact]
        public async Task StartAsync_WhenNotIdle_Throws()
        {
            var controller = new TestController();
            controller.RunBlocker = new TaskCompletionSource<bool>();

            // Start first run
            var runTask = controller.StartAsync();
            await Task.Delay(50); // Let it reach Running state

            // Try to start again
            await Assert.ThrowsAsync<InvalidControllerStateException>(
                () => controller.StartAsync());

            controller.RunBlocker.SetResult(true);
            await runTask;
        }

        [Fact]
        public async Task StartAsync_OnException_TransitionsToFailed()
        {
            var controller = new TestController();
            controller.ExceptionToThrow = new Exception("Test error");

            await controller.StartAsync();

            Assert.Equal(ControllerState.Failed, controller.State);
        }

        /// <summary>A workflow's own refusal reaches the screen unchanged.</summary>
        [Fact]
        public async Task StartAsync_OnRefusal_ShowsTheWorkflowMessage()
        {
            var controller = new TestController
            {
                ExceptionToThrow = new InvalidOperationException(ErrorSafetyRetractFailed)
            };
            ControllerError? receivedError = null;
            controller.ErrorOccurred += e => receivedError = e;

            await controller.StartAsync();

            Assert.NotNull(receivedError);
            Assert.Equal(ErrorSafetyRetractFailed, receivedError!.Message);
        }

        /// <summary>
        /// A probe that never replies is a machine problem the operator can act on, so the
        /// workflow's own message is shown. MachineWait raises it as a TimeoutException.
        /// </summary>
        [Fact]
        public async Task StartAsync_OnTimeout_ShowsTheWorkflowMessage()
        {
            var controller = new TestController
            {
                ExceptionToThrow = new TimeoutException(ErrorProbeTimeout)
            };
            ControllerError? receivedError = null;
            controller.ErrorOccurred += e => receivedError = e;

            await controller.StartAsync();

            Assert.Equal(ErrorProbeTimeout, receivedError?.Message);
        }

        /// <summary>The state machine's refusals name states, not the machine.</summary>
        /// <summary>
        /// ObjectDisposedException names an internal object, even though its base type is
        /// InvalidOperationException.
        /// </summary>
        [Fact]
        public async Task StartAsync_OnDisposedObject_HidesTheObjectName()
        {
            var controller = new TestController
            {
                ExceptionToThrow = new ObjectDisposedException("SerialPort")
            };
            ControllerError? receivedError = null;
            controller.ErrorOccurred += e => receivedError = e;

            await controller.StartAsync();

            Assert.Equal(ErrorRunFailed, receivedError?.Message);
        }

        [Fact]
        public async Task StartAsync_OnIllegalTransition_HidesTheStateNames()
        {
            var controller = new TestController
            {
                ExceptionToThrow = new InvalidControllerStateException("Invalid state transition: A → B")
            };
            ControllerError? receivedError = null;
            controller.ErrorOccurred += e => receivedError = e;

            await controller.StartAsync();

            Assert.Equal(ErrorRunFailed, receivedError?.Message);
        }

        /// <summary>A caller catching InvalidOperationException still catches them.</summary>
        [Fact]
        public void IllegalTransition_ThrowsInvalidOperation()
        {
            var controller = new TestController();

            Assert.IsAssignableFrom<InvalidOperationException>(
                Assert.Throws<InvalidControllerStateException>(() => controller.Pause()));
        }

        /// <summary>
        /// Framework text names paths and offsets the operator cannot act on. The exception
        /// itself stays on the error object, for the log.
        /// </summary>
        [Fact]
        public async Task StartAsync_OnUnexpectedException_HidesTheExceptionText()
        {
            var thrown = new IOException("/home/someone/boards/back.ngc is in use");
            var controller = new TestController { ExceptionToThrow = thrown };
            ControllerError? receivedError = null;
            controller.ErrorOccurred += e => receivedError = e;

            await controller.StartAsync();

            Assert.NotNull(receivedError);
            Assert.Equal(ErrorRunFailed, receivedError!.Message);
            Assert.Same(thrown, receivedError.Exception);
        }

        // =========================================================================
        // Pause/Resume tests
        // =========================================================================

        [Fact]
        public async Task Pause_WhenRunning_TransitionsToPaused()
        {
            var controller = new TestController();
            controller.RunBlocker = new TaskCompletionSource<bool>();

            var runTask = controller.StartAsync();
            await Task.Delay(50); // Let it reach Running

            controller.Pause();

            Assert.Equal(ControllerState.Paused, controller.State);

            controller.RunBlocker.SetResult(true);
        }

        [Fact]
        public void Pause_WhenNotRunning_Throws()
        {
            var controller = new TestController();

            Assert.Throws<InvalidControllerStateException>(() => controller.Pause());
        }

        [Fact]
        public async Task Resume_WhenPaused_TransitionsToRunning()
        {
            var controller = new TestController();
            controller.RunBlocker = new TaskCompletionSource<bool>();

            var runTask = controller.StartAsync();
            await Task.Delay(50);

            controller.Pause();
            controller.Resume();

            Assert.Equal(ControllerState.Running, controller.State);

            controller.RunBlocker.SetResult(true);
        }

        [Fact]
        public void Resume_WhenNotPaused_Throws()
        {
            var controller = new TestController();

            Assert.Throws<InvalidControllerStateException>(() => controller.Resume());
        }

        // =========================================================================
        // StopAsync tests
        // =========================================================================

        [Fact]
        public async Task StopAsync_CallsCleanup()
        {
            var controller = new TestController();
            controller.RunBlocker = new TaskCompletionSource<bool>();

            var runTask = controller.StartAsync();
            await Task.Delay(50);

            await controller.StopAsync();

            Assert.True(controller.CleanupWasCalled);
        }

        [Fact]
        public async Task StopAsync_TransitionsToCancelled()
        {
            var controller = new TestController();
            controller.RunBlocker = new TaskCompletionSource<bool>();

            var runTask = controller.StartAsync();
            await Task.Delay(50);

            await controller.StopAsync();

            Assert.Equal(ControllerState.Cancelled, controller.State);
        }

        [Fact]
        public async Task StopAsync_WhenIdle_DoesNothing()
        {
            var controller = new TestController();

            await controller.StopAsync();

            Assert.Equal(ControllerState.Idle, controller.State);
            Assert.False(controller.CleanupWasCalled);
        }

        // =========================================================================
        // Reset tests
        // =========================================================================

        [Fact]
        public async Task Reset_AfterCompleted_TransitionsToIdle()
        {
            var controller = new TestController();
            await controller.StartAsync();

            controller.Reset();

            Assert.Equal(ControllerState.Idle, controller.State);
        }

        [Fact]
        public async Task Reset_AfterFailed_TransitionsToIdle()
        {
            var controller = new TestController();
            controller.ExceptionToThrow = new Exception("Test");
            await controller.StartAsync();

            controller.Reset();

            Assert.Equal(ControllerState.Idle, controller.State);
        }

        [Fact]
        public async Task Reset_WhenRunning_Throws()
        {
            var controller = new TestController();
            controller.RunBlocker = new TaskCompletionSource<bool>();

            var runTask = controller.StartAsync();
            await Task.Delay(50);

            Assert.Throws<InvalidControllerStateException>(() => controller.Reset());

            controller.RunBlocker.SetResult(true);
        }

        // =========================================================================
        // ResetRunState contract tests
        //
        // ResetRunState is the one place a subclass clears the fields that describe a
        // run (see the doc comment on the abstract method). Controllers are
        // session-lifetime singletons, so if either caller ever stopped invoking it, a
        // field left behind by one run would leak into the next.
        // =========================================================================

        [Fact]
        public async Task StartAsync_CallsResetRunStateBeforeRunning()
        {
            var controller = new TestController();

            await controller.StartAsync();

            // Checked at the moment RunAsync started, not merely by the time the run
            // finished, so a ResetRunState() moved to the bottom of StartAsync fails
            // this rather than passing it.
            Assert.Equal(1, controller.ResetRunStateCallCountAtRunStart);
        }

        [Fact]
        public void Reset_AfterCompleted_CallsResetRunState()
        {
            var controller = new TestController();
            controller.TestTransitionTo(ControllerState.Initializing);
            controller.TestTransitionTo(ControllerState.Running);
            controller.TestTransitionTo(ControllerState.Completing);
            controller.TestTransitionTo(ControllerState.Completed);

            controller.Reset();

            Assert.Equal(1, controller.ResetRunStateCallCount);
        }

        [Fact]
        public async Task StartAsync_ThenReset_CallsResetRunStateTwice()
        {
            var controller = new TestController();
            await controller.StartAsync();

            controller.Reset();

            // Once from the top of StartAsync, once from Reset() itself.
            Assert.Equal(2, controller.ResetRunStateCallCount);
        }

        // =========================================================================
        // State transition validation tests
        // =========================================================================

        [Theory]
        [InlineData(ControllerState.Idle, ControllerState.Running)]
        [InlineData(ControllerState.Idle, ControllerState.Completed)]
        [InlineData(ControllerState.Running, ControllerState.Idle)]
        public void InvalidTransition_Throws(ControllerState from, ControllerState to)
        {
            var controller = new TestController();

            // Get to 'from' state if not Idle
            if (from == ControllerState.Running)
            {
                controller.TestTransitionTo(ControllerState.Initializing);
                controller.TestTransitionTo(ControllerState.Running);
            }

            Assert.Throws<InvalidControllerStateException>(
                () => controller.TestTransitionTo(to));
        }

        // =========================================================================
        // Event emission tests
        // =========================================================================

        [Fact]
        public async Task StateChanged_FiredOnEveryTransition()
        {
            var controller = new TestController();
            var states = new List<ControllerState>();
            controller.StateChanged += s => states.Add(s);

            await controller.StartAsync();

            // Should have: Initializing, Running, Completing, Completed
            Assert.True(states.Count >= 4);
            Assert.Equal(ControllerState.Initializing, states[0]);
        }

        /// <summary>
        /// Raises progress and asserts on what the subscriber received.
        /// </summary>
        [Fact]
        public async Task ProgressChanged_ReachesSubscribersWithTheEmittedValues()
        {
            var controller = new TestController();
            var received = new List<ProgressInfo>();
            controller.ProgressChanged += p => received.Add(p);

            controller.ProgressToEmit = new ProgressInfo("Testing", 42, "half way");
            await controller.StartAsync();

            var progress = Assert.Single(received);
            Assert.Equal("Testing", progress.Phase);
            Assert.Equal(42, progress.Percentage);
            Assert.Equal("half way", progress.Message);
        }

        /// <summary>
        /// A paused run can still raise a prompt. RequestUserInputAsync moves to
        /// WaitingForUserInput and back to whatever it interrupted, so both transitions must
        /// be legal or a pause arriving with a prompt throws out of the run.
        /// </summary>
        [Fact]
        public void APromptRaisedWhilePaused_ReturnsToPaused()
        {
            var controller = new TestController();

            controller.TestTransitionTo(ControllerState.Initializing);
            controller.TestTransitionTo(ControllerState.Running);
            controller.TestTransitionTo(ControllerState.Paused);

            // What RequestUserInputAsync does: interrupt the current state, then restore it.
            controller.TestTransitionTo(ControllerState.WaitingForUserInput);
            controller.TestTransitionTo(ControllerState.Paused);

            Assert.Equal(ControllerState.Paused, controller.State);
        }

        /// <summary>
        /// A run parked at a prompt or finishing is still under way. Anything that reads it
        /// as idle - a second start, closing the serial port - acts on a machine mid-job.
        /// </summary>
        [Theory]
        [InlineData(ControllerState.Idle, false)]
        [InlineData(ControllerState.Initializing, true)]
        [InlineData(ControllerState.Running, true)]
        [InlineData(ControllerState.Paused, true)]
        [InlineData(ControllerState.WaitingForUserInput, true)]
        [InlineData(ControllerState.Completing, true)]
        [InlineData(ControllerState.Completed, false)]
        [InlineData(ControllerState.Failed, false)]
        [InlineData(ControllerState.Cancelled, false)]
        public void EveryState_ReportsWhetherARunIsUnderWay(ControllerState state, bool underWay)
        {
            var controller = new TestController();

            foreach (var step in PathTo(state))
            {
                controller.TestTransitionTo(step);
            }

            Assert.Equal(state, controller.State);
            Assert.Equal(underWay, controller.IsRunInProgress);
        }

        /// <summary>A legal sequence of transitions reaching <paramref name="state"/>.</summary>
        private static ControllerState[] PathTo(ControllerState state) => state switch
        {
            ControllerState.Idle => Array.Empty<ControllerState>(),
            ControllerState.Initializing => new[] { ControllerState.Initializing },
            ControllerState.Running => new[] { ControllerState.Initializing, ControllerState.Running },
            ControllerState.Paused => new[]
                { ControllerState.Initializing, ControllerState.Running, ControllerState.Paused },
            ControllerState.WaitingForUserInput => new[]
                { ControllerState.Initializing, ControllerState.WaitingForUserInput },
            ControllerState.Completing => new[]
                { ControllerState.Initializing, ControllerState.Running, ControllerState.Completing },
            ControllerState.Completed => new[]
            {
                ControllerState.Initializing, ControllerState.Running,
                ControllerState.Completing, ControllerState.Completed
            },
            ControllerState.Failed => new[] { ControllerState.Initializing, ControllerState.Failed },
            ControllerState.Cancelled => new[] { ControllerState.Initializing, ControllerState.Cancelled },
            _ => Array.Empty<ControllerState>()
        };

        /// <summary>
        /// Nothing is awaited between two prompts, so answering the first resumes the run on
        /// the answering thread and publishes the second. This is why PendingPrompt.Answer
        /// takes an id.
        /// </summary>
        [Fact]
        public async Task AnsweringAPrompt_PublishesTheNextBeforeItReturns()
        {
            var controller = new TestController { PromptsToAsk = new[] { "first", "second" } };
            var asked = new List<UserInputRequest>();
            controller.UserInputRequired += request => asked.Add(request);

            var run = controller.StartAsync();

            Assert.Single(asked);

            asked[0].OnResponse("Continue");

            // The answer has returned and the run has already published the next prompt.
            Assert.Equal(2, asked.Count);

            asked[1].OnResponse("Continue");
            await run;
        }

        /// <summary>
        /// Stopping a run that already cancelled itself must not throw. StopAsync
        /// transitions from inside a finally, and Cancelled may only go to Idle, so a throw
        /// there escapes over whatever brought the caller in. The terminal swallows that and
        /// skips the Reset after it, leaving the controller stuck: StartAsync needs Idle.
        /// </summary>
        [Fact]
        public async Task StoppingARunThatAlreadyCancelled_LeavesItResettable()
        {
            var controller = new TestController();

            controller.ExceptionToThrow = new OperationCanceledException();
            await controller.StartAsync();
            Assert.Equal(ControllerState.Cancelled, controller.State);

            // What ProbeMenu does on exit: stop anything not already idle.
            await controller.StopAsync();

            Assert.Equal(ControllerState.Cancelled, controller.State);

            controller.Reset();
            Assert.Equal(ControllerState.Idle, controller.State);
        }

        /// <summary>
        /// A prompt with no subscriber used to wait for an answer that could not arrive, so
        /// the run held the machine until the operator pressed Stop.
        /// </summary>
        [Fact]
        public async Task RequestUserInput_WithNoSubscriber_Throws()
        {
            var controller = new PromptingController();

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => controller.AskAsync(CancellationToken.None));

            Assert.Equal(ControllerConstants.ErrorNoPromptHandler, failure.Message);
        }

        /// <summary>
        /// A door state reaches the operator on one channel. The closed door is the state
        /// with something to answer, so it goes out as a prompt only. Sent as both, a screen
        /// would draw the same sentence twice, once with the choices and once without.
        /// </summary>
        [Fact]
        public async Task ClosedDoor_IsPromptedAndNotAlsoEmittedAsAMessage()
        {
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateClosed);
            var controller = new DoorController(machine);

            // Every progress, not only PhaseWaitingForOperator: the mill screen and the
            // browser draw any phase that is not PhaseMilling, so the door sentence under
            // any phase is drawn twice.
            var published = new List<ProgressInfo>();
            controller.ProgressChanged += published.Add;

            UserInputRequest? asked = null;
            controller.UserInputRequired += request =>
            {
                asked = request;
                request.OnResponse(ControllerConstants.OptionAbort);
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await controller.StartAsync(cts.Token);

            Assert.NotNull(asked);
            Assert.True(asked!.IsDoorPrompt);
            Assert.Equal(ControllerConstants.DoorHoldingPrompt, asked.Message);
            Assert.DoesNotContain(
                published, p => p.Message == ControllerConstants.DoorHoldingPrompt);
        }

        /// <summary>
        /// Closing the enclosure moves GRBL from Door:1 to Door:0, which is still Door. A run
        /// watching for the door to clear waits out its whole timeout, and for those seconds
        /// every screen tells the operator to close a door they have closed.
        /// </summary>
        [Fact]
        public async Task ClosingTheDoor_IsNoticedOnTheStatusChange()
        {
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateAjar);
            var controller = new DoorController(machine);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var closedAt = new TaskCompletionSource<long>();
            var promptedAt = new TaskCompletionSource<long>();

            controller.UserInputRequired += request =>
            {
                promptedAt.TrySetResult(clock.ElapsedMilliseconds);
                request.OnResponse(ControllerConstants.OptionContinue);
            };

            _ = Task.Run(async () =>
            {
                await Task.Delay(Constants.StatusPollIntervalMs * 3);
                machine.SimulateDoorClosedAndHolding();
                closedAt.TrySetResult(clock.ElapsedMilliseconds);
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await controller.StartAsync(cts.Token);

            long lag = await promptedAt.Task - await closedAt.Task;
            Assert.True(lag < Constants.StatusPollIntervalMs * 5,
                $"the run took {lag}ms to notice the enclosure was closed");
        }

        /// <summary>
        /// A screen holds the last waiting message until the run sends another. The run has
        /// to withdraw it when the door is dealt with, or the message is still up while the
        /// tool moves - on the probe's path the next progress is a whole retract away.
        /// </summary>
        [Fact]
        public async Task OnceTheDoorIsDealtWith_TheRunWithdrawsItsMessage()
        {
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateAjar);
            var controller = new DoorController(machine);

            // The rule ProbeMenu and MillMenu both draw by.
            string? onScreen = null;
            controller.ProgressChanged += progress =>
                onScreen = progress.Phase == ControllerConstants.PhaseWaitingForOperator
                    ? progress.Message
                    : null;

            controller.UserInputRequired += request =>
                request.OnResponse(ControllerConstants.OptionContinue);

            _ = Task.Run(async () =>
            {
                await Task.Delay(Constants.StatusPollIntervalMs * 3);
                machine.SimulateDoorClosedAndHolding();
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await controller.StartAsync(cts.Token);

            Assert.False(MachineWait.IsDoor(machine), "the hold was never released");
            Assert.Null(onScreen);
        }

        /// <summary>
        /// A park restore is a move under way, so it goes out as a message like an open door.
        /// Without this, GetDoorMessage could return anything for it and the screens would
        /// show that while the tool moves.
        /// </summary>
        [Fact]
        public async Task DoorResuming_IsEmittedAsAMessage()
        {
            Assert.Contains(
                ControllerConstants.DoorResumingMessage,
                await DoorMessagesAsync(GrblProtocol.DoorSubStateResuming));
        }

        /// <summary>
        /// The other side of the same rule: an open door has nothing to answer, so it goes
        /// out as a message. Without it the screens have nothing to draw while the run waits.
        /// </summary>
        [Fact]
        public async Task OpenDoor_IsEmittedAsAMessage()
        {
            Assert.Contains(
                ControllerConstants.DoorOpenPrompt,
                await DoorMessagesAsync(GrblProtocol.DoorSubStateAjar));
        }

        /// <summary>
        /// The park retract is GRBL's own move away from the work, still with the enclosure
        /// open. It reads as an open door, so it is waited out and never prompted about.
        /// </summary>
        [Fact]
        public async Task DoorRetracting_IsEmittedAsAMessageLikeAnOpenDoor()
        {
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateRetracting);

            Assert.Equal(DoorState.Open, MachineWait.GetDoorState(machine));
            Assert.Contains(
                ControllerConstants.DoorOpenPrompt,
                await DoorMessagesAsync(GrblProtocol.DoorSubStateRetracting));
        }

        /// <summary>
        /// A switch that reads closed but never lets GRBL resume would otherwise re-prompt
        /// for ever. After MachineClearAttempts answers the run names the switch instead.
        /// </summary>
        [Fact]
        public async Task ADoorThatNeverReleases_StopsAskingAndNamesTheSwitch()
        {
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateClosed);
            machine.IgnoreCycleStart = true;
            var controller = new DoorController(machine);

            int asked = 0;
            ControllerError? reported = null;
            controller.UserInputRequired += request =>
            {
                asked++;
                request.OnResponse(ControllerConstants.OptionContinue);
            };
            controller.ErrorOccurred += error => reported = error;

            // Every answer is given the full restore budget before the run gives up on it,
            // so the whole sequence is that budget times the number of attempts.
            using var cts = new CancellationTokenSource(
                ControllerConstants.MachineClearAttempts * ControllerConstants.DoorResumeTimeoutMs
                + DoorTestGraceMs);
            await controller.StartAsync(cts.Token);

            Assert.Equal(ControllerConstants.MachineClearAttempts, asked);
            Assert.Equal(ControllerConstants.ErrorDoorWillNotRelease, reported?.Message);
            Assert.Equal(ControllerState.Failed, controller.State);
        }

        /// <summary>
        /// Every message a run publishes while it holds at a door in <paramref name="subState"/>.
        /// </summary>
        private static async Task<List<string>> DoorMessagesAsync(string subState)
        {
            using var machine = MockMachine.AtADoor(subState);
            var controller = new DoorController(machine);

            var messages = new List<string>();
            controller.ProgressChanged += progress =>
            {
                if (progress.Phase == ControllerConstants.PhaseWaitingForOperator)
                {
                    messages.Add(progress.Message);
                }
            };

            using var cts = new CancellationTokenSource(
                ControllerConstants.DoorResumeTimeoutMs + DoorTestGraceMs);
            try
            {
                await controller.StartAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Cancelling is the only exit when the door is never cleared.
            }

            return messages;
        }

        /// <summary>
        /// A closed door is the only door state with a prompt, and it offers both options.
        /// An open door and a park restore are waited out instead.
        /// </summary>
        [Fact]
        public async Task ClosedDoor_PromptOffersContinueAndAbort()
        {
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateClosed);
            var controller = new DoorController(machine);

            UserInputRequest? asked = null;
            controller.UserInputRequired += request =>
            {
                asked = request;
                request.OnResponse(ControllerConstants.OptionAbort);
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await controller.StartAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Aborting ends the test; the options are what it checks.
            }

            Assert.NotNull(asked);
            Assert.Equal(
                new[] { ControllerConstants.OptionContinue, ControllerConstants.OptionAbort },
                asked!.Options);
        }

        /// <summary>
        /// A park restore takes as long as the machine's parking settings say, so it never
        /// raises a prompt. It used to prompt after five seconds - "the machine has not
        /// finished moving the tool back", with Abort as the only option - on a restore that
        /// was still running.
        /// </summary>
        [Fact]
        public async Task DoorResuming_RaisesNoPrompt()
        {
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateResuming);
            var controller = new DoorController(machine);

            bool asked = false;
            controller.UserInputRequired += request =>
            {
                asked = true;
                request.OnResponse(ControllerConstants.OptionAbort);
            };

            // Longer than DoorResumeTimeoutMs, which is what used to raise the prompt.
            using var cts = new CancellationTokenSource(
                ControllerConstants.DoorResumeTimeoutMs + DoorTestGraceMs);
            try
            {
                await controller.StartAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Cancelling is the only exit from a restore that never finishes.
            }

            Assert.False(asked, "a restore that was still running was turned into a question");
        }

        private sealed class DoorController : ControllerBase
        {
            private readonly MockMachine _machine;

            public DoorController(MockMachine machine) => _machine = machine;

            protected override IMachine Machine => _machine;

            protected override Task RunAsync(CancellationToken ct) => EnsureDoorClosedAsync(ct);

            protected override Task CleanupAsync() => Task.CompletedTask;

            protected override void ResetRunState() { }
        }

        private sealed class PromptingController : ControllerBase
        {
            private readonly MockMachine _machine = new();

            protected override IMachine Machine => _machine;

            public Task<string> AskAsync(CancellationToken ct) =>
                RequestUserInputAsync("Title", "Message", new[] { "Continue" }, ct);

            protected override Task RunAsync(CancellationToken ct) => Task.CompletedTask;

            protected override Task CleanupAsync() => Task.CompletedTask;

            protected override void ResetRunState() { }
        }

        /// <summary>
        /// A run paused at the door stays paused. GRBL keeps the moves a resume sends in its
        /// planner and runs them the moment the hold lifts, with nobody watching. Every
        /// controller inherits this, not only the mill.
        /// </summary>
        [Fact]
        public async Task ResumingAtTheDoor_IsRefusedAndTheRunStaysPaused()
        {
            var controller = new TestController { RunBlocker = new TaskCompletionSource<bool>() };
            var run = controller.StartAsync();

            await Task.Delay(50); // Let it reach Running
            controller.Pause();
            Assert.Equal(ControllerState.Paused, controller.State);

            controller.Fake.SimulateDoorOpen();
            var errors = new List<ControllerError>();
            controller.ErrorOccurred += errors.Add;

            controller.Resume();

            Assert.Equal(ControllerState.Paused, controller.State);
            Assert.Equal(ControllerConstants.ErrorDoorBlocksResume, Assert.Single(errors).Message);

            controller.RunBlocker!.TrySetResult(true);
            await run;
        }
    }
}
