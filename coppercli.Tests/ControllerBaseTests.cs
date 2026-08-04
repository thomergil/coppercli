using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using coppercli.Core.Controllers;
using Xunit;

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

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                TransitionTo(ControllerState.Running);

                if (RunBlocker != null)
                {
                    await RunBlocker.Task;
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
            await Assert.ThrowsAsync<InvalidOperationException>(
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

        [Fact]
        public async Task StartAsync_OnException_EmitsErrorEvent()
        {
            var controller = new TestController();
            controller.ExceptionToThrow = new Exception("Test error");
            ControllerError? receivedError = null;
            controller.ErrorOccurred += e => receivedError = e;

            await controller.StartAsync();

            Assert.NotNull(receivedError);
            Assert.Equal("Test error", receivedError!.Message);
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

            Assert.Throws<InvalidOperationException>(() => controller.Pause());
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

            Assert.Throws<InvalidOperationException>(() => controller.Resume());
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

            Assert.Throws<InvalidOperationException>(() => controller.Reset());

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

            Assert.Throws<InvalidOperationException>(
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

        [Fact]
        public async Task ProgressChanged_CanBeEmitted()
        {
            var controller = new TestController();
            ProgressInfo? receivedProgress = null;
            controller.ProgressChanged += p => receivedProgress = p;

            // Progress is emitted by subclass, not tested here directly
            await controller.StartAsync();

            // Just verify no exceptions
        }
    }
}
