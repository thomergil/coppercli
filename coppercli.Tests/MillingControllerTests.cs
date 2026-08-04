#nullable enable
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using coppercli.Core.Controllers;
using coppercli.Core.GCode;
using coppercli.Core.Util;
using coppercli.Tests.Fakes;
using Xunit;
using static coppercli.Core.Controllers.ControllerConstants;

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

        // Generous relative to the real time a run needs (dominated by the fixed
        // settle/idle-settle/homing delays above): pre-fix, the controller never
        // reaches the event or state under test, so these bound the wait rather than
        // hang the suite.
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
