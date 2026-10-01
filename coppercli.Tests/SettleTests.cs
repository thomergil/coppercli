#nullable enable
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;
using coppercli.Core.Controllers;
using coppercli.Core.Util;
using coppercli.Tests.Fakes;
using Xunit;
using static coppercli.Core.Util.Constants;

namespace coppercli.Tests
{
    /// <summary>
    /// The settle before a job: how long it takes, what it reports for a machine that will not
    /// settle, what it does with a door hold, and how <see cref="MachineWait.WaitForSteadyIdleAsync"/> reads a brief Run between two
    /// Idles.
    /// </summary>
    [Collection(TimingSensitiveCollection.Name)]
    public class SettleTests
    {
        private const double FastMoveSpeedMmPerSec = 10000.0;
        private const int FastHomingDurationMs = 50;

        /// <summary>Slack over IdleSettleMs for the retract, setup and scheduling.</summary>
        private const int SettleMarginMs = 1500;

        /// <summary>The fixed wait the settle used to cost; streaming must start well before it.</summary>
        private const int OldFixedSettleMs = 5000;

        private const int UnsettledTimeoutMs = 1200;
        private const int ToleranceMs = 100;
        private const int WaitMs = 10_000;
        private const int DoorHeldObservationMs = 700;

        private const int RunStartMs = 300;
        private const int RunLengthMs = 300;
        private const int SteadyMs = 600;
        private const int SettleCeilingMs = 10_000;

        private static readonly string[] Job = { "G21", "G90", "G1 X1 Y1 F100" };

        private static FakeMachine FastMachine()
        {
            var machine = new FakeMachine
            {
                RapidSpeed = FastMoveSpeedMmPerSec,
                FeedSpeed = FastMoveSpeedMmPerSec,
                HomingDurationMs = FastHomingDurationMs,
            };
            machine.LoadFile(Job);
            return machine;
        }

        private static async Task AwaitOutcomeAsync(Task run)
        {
            try
            {
                await run;
            }
            catch (InvalidOperationException)
            {
                // A refusal under test; its text arrives through ErrorOccurred.
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>
        /// A settled machine starts streaming after about IdleSettleMs. Catches a settle that
        /// waits out a fixed delay such as the old 5 s one.
        /// </summary>
        [Fact]
        public async Task MillingStarts_WithinTheIdleSettleTimeAndAMargin()
        {
            using var machine = FastMachine();
            machine.IsHomed = true;
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions ()
            };

            var elapsed = Stopwatch.StartNew();
            var run = controller.StartAsync();
            await AsyncWait.WaitUntilAsync(
                () => controller.Phase == MillingPhase.Milling, "the Milling phase", WaitMs);
            long startedAtMs = elapsed.ElapsedMilliseconds;

            await controller.StopAsync();
            await AwaitOutcomeAsync(run);

            Assert.True(startedAtMs >= IdleSettleMs - ToleranceMs, $"streaming began after {startedAtMs} ms, before the machine was steady");
            Assert.True(startedAtMs < OldFixedSettleMs && startedAtMs <= IdleSettleMs + SettleMarginMs,
                $"streaming began after {startedAtMs} ms; the settle takes about {IdleSettleMs} ms");
        }

        /// <summary>
        /// A machine that will not settle is waited out until SettleTimeoutMs and then
        /// reported by what it is doing: an alarm, a machine that takes no moves (asleep), or
        /// one still holding.
        /// </summary>
        [Theory]
        [InlineData(GrblProtocol.StatusAlarm, ControllerConstants.ErrorAlarmBeforeStart)]
        [InlineData(GrblProtocol.StatusSleep, ControllerConstants.ErrorMachineNotResponding)]
        [InlineData(GrblProtocol.StatusHold, ControllerConstants.ErrorMachineNotSettled)]
        public async Task AMachineThatWillNotSettle_IsWaitedOutThenReportedByItsState(string status, string expected)
        {
            var machine = new MockMachine { Status = status, Connected = true, IsHomed = true };
            machine.LoadFile(Job);
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { SettleTimeoutMs = UnsettledTimeoutMs }
            };
            string? reported = null;
            controller.ErrorOccurred += error => reported = error.Message;

            var elapsed = Stopwatch.StartNew();
            await AwaitOutcomeAsync(controller.StartAsync());

            Assert.Equal(ControllerState.Failed, controller.State);
            Assert.Equal(expected, reported);
            Assert.True(elapsed.ElapsedMilliseconds >= UnsettledTimeoutMs - ToleranceMs,
                $"gave up after {elapsed.ElapsedMilliseconds} ms, before SettleTimeoutMs");
        }

        /// <summary>
        /// A door hold during the settle goes to the operator as a prompt, and the hold is not
        /// released (no cycle start) until the operator answers. Catches a settle that resumes
        /// the spindle on its own.
        /// </summary>
        [Fact]
        public async Task ADoorHoldDuringTheSettle_IsPromptedAndNotResumedWithoutAnAnswer()
        {
            using var machine = FastMachine();
            machine.IsHomed = true;
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions ()
            };
            var prompts = new ConcurrentQueue<UserInputRequest>();
            controller.UserInputRequired += prompts.Enqueue;

            var run = controller.StartAsync();
            await AsyncWait.WaitUntilAsync(
                () => controller.State == ControllerState.Initializing, "Initializing", WaitMs);
            machine.SimulateDoorClosedAndHolding();

            await AsyncWait.WaitUntilAsync(() => !prompts.IsEmpty, "the door prompt", WaitMs);
            Assert.True(prompts.TryDequeue(out var request));
            Assert.Equal(ControllerConstants.DoorHoldingPrompt, request!.Message);

            await AsyncWait.AssertStaysTrueAsync(
                () => MachineWait.IsDoor(machine) && controller.State != ControllerState.Running,
                DoorHeldObservationMs,
                "the door hold was released, or the run went on, without an answer");

            request.OnResponse(ControllerConstants.OptionContinue);
            await AsyncWait.WaitUntilAsync(
                () => controller.State == ControllerState.Running, "Running after the answer", WaitMs);

            await controller.StopAsync();
            await AwaitOutcomeAsync(run);
        }

        /// <summary>
        /// A brief Run between two Idles restarts the steady-Idle clock: the wait returns only
        /// after steadyMs of unbroken Idle. Catches a wait that keeps counting from the first
        /// Idle.
        /// </summary>
        [Fact]
        public async Task WaitForSteadyIdle_RestartsTheSteadyClockAfterABriefRun()
        {
            var machine = new MockMachine { Status = GrblProtocol.StatusIdle, Connected = true };

            var elapsed = Stopwatch.StartNew();
            var briefRun = Task.Run(async () =>
            {
                await Task.Delay(RunStartMs);
                machine.Status = GrblProtocol.StatusRun;
                await Task.Delay(RunLengthMs);
                machine.Status = GrblProtocol.StatusIdle;
            });

            bool settled = await MachineWait.WaitForSteadyIdleAsync(machine, SteadyMs, SettleCeilingMs);
            long settledAtMs = elapsed.ElapsedMilliseconds;
            await briefRun;

            Assert.True(settled);
            Assert.True(settledAtMs >= RunStartMs + RunLengthMs + SteadyMs - ToleranceMs,
                $"settled after {settledAtMs} ms; the Idle before the brief Run was counted");
        }
    }
}
