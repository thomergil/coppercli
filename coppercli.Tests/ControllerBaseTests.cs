using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using coppercli.Core.Controllers;
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
        /// Concrete implementation for testing abstract ControllerBase.
        /// </summary>
        private class TestController : ControllerBase
        {
            public bool RunWasCalled { get; private set; }
            public bool CleanupWasCalled { get; private set; }
            public Exception? ExceptionToThrow { get; set; }
            public TaskCompletionSource<bool>? RunBlocker { get; set; }
            public int ResetRunStateCallCount { get; private set; }

            /// <summary>Questions RunAsync asks back to back, with nothing awaited between.</summary>
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
        /// A run that returns without transitioning would leave the controller claiming the
        /// machine with no way back, and every front end reads that as "still running".
        /// </summary>
        [Fact]
        public async Task ARunThatReturnsWithoutFinishing_StillEndsTheRun()
        {
            var controller = new TestController { ReturnWithoutFinishing = true };

            await controller.StartAsync();

            Assert.True(controller.HasFinished);
            Assert.False(controller.IsRunInProgress);
        }

        /// <summary>Cancelling says so, rather than reporting a failure nobody caused.</summary>
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
        /// The operator's way out of any ended run: stop, then start again. Releasing is
        /// what returns the controller to Idle, whatever state the run left behind.
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
        public async Task StartAsync_OnRefusal_PassesTheWorkflowsOwnWords()
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
        /// A probe that never answers is a machine problem the operator can act on, and the
        /// workflow names it. MachineWait raises those words as a TimeoutException.
        /// </summary>
        [Fact]
        public async Task StartAsync_OnTimeout_PassesTheWorkflowsOwnWords()
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
        /// A disposed object names itself, which is a fact about this code, even though its
        /// type says invalid operation.
        /// </summary>
        [Fact]
        public async Task StartAsync_OnDisposedObject_KeepsItsNameOffTheScreen()
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
        public async Task StartAsync_OnIllegalTransition_KeepsTheStateNamesOffTheScreen()
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
        public void AStateMachineRefusalIsAnInvalidOperation()
        {
            var controller = new TestController();

            Assert.IsAssignableFrom<InvalidOperationException>(
                Assert.Throws<InvalidControllerStateException>(() => controller.Pause()));
        }

        /// <summary>
        /// Framework text names paths and offsets that mean nothing at a machine. The
        /// exception itself stays on the error, for the log.
        /// </summary>
        [Fact]
        public async Task StartAsync_OnUnexpectedException_KeepsItsTextOffTheScreen()
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
        public async Task ProgressChanged_ReachesSubscribersWithWhatWasEmitted()
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
        /// A paused run can still have something to ask. RequestUserInputAsync moves to
        /// WaitingForUserInput and back to whatever it interrupted, so both edges must
        /// exist or an operator pausing as a prompt is raised throws out of the run.
        /// </summary>
        [Fact]
        public void APromptCanBeRaisedWhilePausedAndReturnToPaused()
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
        /// A run parked at a prompt or finishing is still under way. Anything reading it as
        /// free - a second start, closing the serial port - acts on a machine mid-job.
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
        public void EveryStateSaysWhetherARunIsUnderWay(ControllerState state, bool underWay)
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
        /// Nothing is awaited between two questions, so answering the first resumes the run
        /// on the answering thread and publishes the second. This is why PendingPrompt.Answer
        /// takes an id.
        /// </summary>
        [Fact]
        public async Task AnsweringOnePromptPublishesTheNextBeforeItReturns()
        {
            var controller = new TestController { PromptsToAsk = new[] { "first", "second" } };
            var asked = new List<UserInputRequest>();
            controller.UserInputRequired += request => asked.Add(request);

            var run = controller.StartAsync();

            Assert.Single(asked);

            asked[0].OnResponse("Continue");

            // Back from the answer, and the run has already asked the next question.
            Assert.Equal(2, asked.Count);

            asked[1].OnResponse("Continue");
            await run;
        }

        /// <summary>
        /// Stopping a run that already cancelled itself must not throw. StopAsync
        /// transitions from inside a finally, and Cancelled may only go to Idle, so a
        /// throw there would escape over whatever brought the caller in. The terminal
        /// swallows that and skips the Reset after it, leaving the controller stuck:
        /// StartAsync needs Idle, so nothing would run again that session.
        /// </summary>
        [Fact]
        public async Task StoppingARunThatAlreadyCancelled_LeavesItResettable()
        {
            var controller = new TestController();

            controller.ExceptionToThrow = new OperationCanceledException();
            await controller.StartAsync();
            Assert.Equal(ControllerState.Cancelled, controller.State);

            // What ProbeMenu does on the way out: stop anything not already idle.
            await controller.StopAsync();

            Assert.Equal(ControllerState.Cancelled, controller.State);

            controller.Reset();
            Assert.Equal(ControllerState.Idle, controller.State);
        }
    }
}
