using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using coppercli.Core.Communication;
using coppercli.Core.Controllers;
using coppercli.Core.Util;
using coppercli.Tests.Fakes;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// Covers which resets <see cref="Machine.IsHomed"/> survives. A reset coppercli sends while
    /// the axes are stopped keeps GRBL's position; one that interrupts motion, or a restart
    /// coppercli did not cause, may lose it. A true that outlives a lost position lets a job run
    /// without homing; a false after a harmless stop makes every stop cost a homing cycle.
    /// Each test drives the real Machine over a loopback socket.
    /// </summary>
    [Collection(TimingSensitiveCollection.Name)]
    public class HomingAcrossResetTests
    {
        /// <summary>Longer than a few status polls, so a stop visibly waits for it.</summary>
        private const int DecelerationMs = 400;

        /// <summary>Scheduling slack on a timing bound.</summary>
        private const int ToleranceMs = 100;

        /// <summary>Extra time a late reset is allowed before it counts as not bounded.</summary>
        private const int LateMarginMs = 1000;

        private const int HomingWaitMs = 5_000;

        private const int NeverMs = int.MaxValue;

        private static async Task<Machine> ConnectedAndHomedAsync(FakeGrbl grbl)
        {
            var machine = grbl.ConnectedMachine();
            var outcome = await MachineWait.HomeAsync(machine, HomingWaitMs);
            Assert.True(outcome.Success, "homing did not complete");
            Assert.True(machine.IsHomed);
            return machine;
        }

        private static void WaitForFreshReports(Machine machine, int count = 3)
        {
            long target = machine.StatusReportCount + count;
            WebServerFixture.WaitUntil(
                () => machine.StatusReportCount >= target, "status reports after the reset");
        }

        /// <summary>
        /// A reset coppercli sends while GRBL is Idle leaves GRBL's position intact. If IsHomed
        /// fell here, every stop at rest would force a homing cycle on the next job.
        /// </summary>
        [Fact]
        public async Task ASoftResetSentWhileIdle_KeepsIsHomed()
        {
            using var grbl = new FakeGrbl();
            var machine = await ConnectedAndHomedAsync(grbl);
            try
            {
                machine.SoftReset();
                WebServerFixture.WaitUntil(() => grbl.SoftResetCount == 1, "the reset to reach GRBL");
                WaitForFreshReports(machine);

                Assert.Equal(GrblProtocol.StatusIdle, machine.Status);
                Assert.True(machine.IsHomed, "an idle reset cleared IsHomed");
            }
            finally
            {
                machine.Disconnect();
            }
        }

        /// <summary>
        /// A stop on a moving machine must send the reset only once GRBL reports the hold
        /// complete (Hold:0). Reset earlier and GRBL prints ALARM:3, loses its position and the
        /// next job needs homing; this catches a stop that sends the reset straight after the
        /// feed hold.
        /// </summary>
        [Fact]
        public async Task StopAndReset_OnAMovingMachine_WaitsForTheHoldThenKeepsIsHomed()
        {
            using var grbl = new FakeGrbl { HoldDecelMs = DecelerationMs };
            var machine = await ConnectedAndHomedAsync(grbl);
            var errors = new ConcurrentQueue<string>();
            machine.NonFatalException += errors.Enqueue;
            try
            {
                grbl.SimulateMoving();
                WebServerFixture.WaitUntil(() => machine.Status == GrblProtocol.StatusRun, "Run");

                var elapsed = Stopwatch.StartNew();
                var stop = MachineWait.StopAndResetAsync(machine);
                WebServerFixture.WaitUntil(() => grbl.SoftResetCount == 1, "the reset to reach GRBL");
                long resetAtMs = elapsed.ElapsedMilliseconds;
                await stop;

                Assert.True(resetAtMs >= DecelerationMs - ToleranceMs,
                    $"the reset went out after {resetAtMs} ms, before the hold finished");
                var received = grbl.Received.ToList();
                Assert.True(
                    received.IndexOf(FakeGrbl.FeedHoldMark) < received.IndexOf(FakeGrbl.SoftResetMark),
                    "the feed hold must precede the reset");
                Assert.Empty(errors);
                Assert.True(machine.IsHomed, "a reset after the hold cleared IsHomed");
            }
            finally
            {
                machine.Disconnect();
            }
        }

        /// <summary>
        /// A reset while the axes move makes GRBL print ALARM:3 and lose its position. IsHomed
        /// must fall, or the next job skips homing on a machine that no longer knows where it is.
        /// </summary>
        [Fact]
        public async Task ASoftResetWhileMoving_AlarmsAndClearsIsHomed()
        {
            using var grbl = new FakeGrbl();
            var machine = await ConnectedAndHomedAsync(grbl);
            var errors = new ConcurrentQueue<string>();
            machine.NonFatalException += errors.Enqueue;
            try
            {
                grbl.SimulateMoving();
                WebServerFixture.WaitUntil(() => machine.Status == GrblProtocol.StatusRun, "Run");

                machine.SoftReset();

                WebServerFixture.WaitUntil(
                    () => machine.Status == GrblProtocol.StatusAlarm, "GRBL to come back locked");
                Assert.False(errors.IsEmpty, "GRBL's ALARM was not reported");
                Assert.False(machine.IsHomed, "IsHomed survived an ALARM:3");
            }
            finally
            {
                machine.Disconnect();
            }
        }

        /// <summary>
        /// GRBL's message asking to be unlocked says it may have lost its position, and it is
        /// the only sign after a reset out of sleep. On its own, with no banner and no ALARM
        /// line, it must clear IsHomed.
        /// </summary>
        [Fact]
        public async Task TheUnlockMessage_ClearsIsHomed()
        {
            using var grbl = new FakeGrbl();
            var machine = await ConnectedAndHomedAsync(grbl);
            try
            {
                grbl.SimulateUnlockMessage();

                WebServerFixture.WaitUntil(() => !machine.IsHomed, "the unlock message to clear IsHomed");
            }
            finally
            {
                machine.Disconnect();
            }
        }

        /// <summary>
        /// A hard limit prints ALARM:1 and stops GRBL where it is, with no restart and no
        /// unlock message, and the position is likely lost. The ALARM line alone must clear
        /// IsHomed; after an alarmed restart the unlock message would hide a Machine that
        /// ignored it.
        /// </summary>
        [Fact]
        public async Task AnAlarmLineAlone_ClearsIsHomed()
        {
            using var grbl = new FakeGrbl();
            var machine = await ConnectedAndHomedAsync(grbl);
            try
            {
                grbl.SimulateHardLimit();

                WebServerFixture.WaitUntil(() => !machine.IsHomed, "the ALARM line to clear IsHomed");
            }
            finally
            {
                machine.Disconnect();
            }
        }

        /// <summary>
        /// A restart coppercli did not cause (reset button, brown-out) may have lost the
        /// position. Covers the alarmed restart of a board that requires homing.
        /// </summary>
        [Fact]
        public async Task ARestartCoppercliDidNotCause_ClearsIsHomed()
        {
            using var grbl = new FakeGrbl();
            var machine = await ConnectedAndHomedAsync(grbl);
            try
            {
                grbl.SimulateRestart();

                WebServerFixture.WaitUntil(() => !machine.IsHomed, "the restart to clear IsHomed");
            }
            finally
            {
                machine.Disconnect();
            }
        }

        /// <summary>
        /// A restart coppercli did not cause still clears IsHomed when it comes back unlocked,
        /// on a board that does not require homing: the banner is the only sign GRBL
        /// restarted, and a power loss may have moved the axes.
        /// </summary>
        [Fact]
        public async Task ARestartCoppercliDidNotCause_WithoutAnAlarm_ClearsIsHomed()
        {
            using var grbl = new FakeGrbl();
            var machine = await ConnectedAndHomedAsync(grbl);
            try
            {
                grbl.SimulateRestart(alarmed: false);

                WebServerFixture.WaitUntil(() => !machine.IsHomed, "the banner to clear IsHomed");
            }
            finally
            {
                machine.Disconnect();
            }
        }

        /// <summary>
        /// The wait for the hold is bounded. A machine that never reports the hold finished must
        /// still get its reset after about StopHoldTimeoutMs; an unbounded wait would leave the
        /// spindle running when the operator presses stop.
        /// </summary>
        [Fact]
        public async Task StopAndReset_OnAMachineThatNeverFinishesTheHold_ResetsAfterTheTimeout()
        {
            using var grbl = new FakeGrbl { HoldDecelMs = NeverMs };
            var machine = grbl.ConnectedMachine();
            try
            {
                grbl.SimulateMoving();
                WebServerFixture.WaitUntil(() => machine.Status == GrblProtocol.StatusRun, "Run");

                var elapsed = Stopwatch.StartNew();
                var stop = MachineWait.StopAndResetAsync(machine);
                WebServerFixture.WaitUntil(() => grbl.SoftResetCount == 1, "the reset to reach GRBL");
                long resetAtMs = elapsed.ElapsedMilliseconds;
                await stop;

                Assert.True(resetAtMs >= Constants.StopHoldTimeoutMs - ToleranceMs,
                    $"the reset went out after {resetAtMs} ms, before the wait ran out");
                Assert.True(resetAtMs <= Constants.StopHoldTimeoutMs + LateMarginMs,
                    $"the reset went out after {resetAtMs} ms; the wait is not bounded by StopHoldTimeoutMs");
            }
            finally
            {
                machine.Disconnect();
            }
        }

        /// <summary>
        /// A feed hold does not stop a homing cycle, so waiting for Hold:0 there only delays the
        /// reset by the whole timeout. The stop during homing must send the reset promptly.
        /// </summary>
        [Fact]
        public async Task StopAndReset_DuringHoming_IsNotDelayedByTheHoldWait()
        {
            using var grbl = new FakeGrbl { HomingMs = NeverMs };
            var machine = grbl.ConnectedMachine();
            using var cts = new CancellationTokenSource();
            try
            {
                var homing = MachineWait.HomeAsync(machine, NeverMs / 2, cts.Token);
                WebServerFixture.WaitUntil(
                    () => machine.IsHoming && grbl.Received.Contains(GrblProtocol.CmdHome), "the homing cycle to start");

                var elapsed = Stopwatch.StartNew();
                var stop = MachineWait.StopAndResetAsync(machine);
                WebServerFixture.WaitUntil(() => grbl.SoftResetCount == 1, "the reset to reach GRBL");
                long resetAtMs = elapsed.ElapsedMilliseconds;

                Assert.True(resetAtMs < Constants.StopHoldTimeoutMs / 2,
                    $"the reset waited {resetAtMs} ms during homing");

                await stop;
                cts.Cancel();
                try
                {
                    await homing;
                }
                catch (OperationCanceledException)
                {
                    // The homing wait ends with the cancel; its outcome is not under test.
                }
            }
            finally
            {
                machine.Disconnect();
            }
        }

        /// <summary>
        /// After a connect, coppercli has no position from this session, and after a
        /// disconnect it cannot tell whether the axes moved. A stale true across either would
        /// let a job skip homing.
        /// </summary>
        [Fact]
        public async Task ConnectAndDisconnect_ClearIsHomed()
        {
            using var grbl = new FakeGrbl();
            var machine = await ConnectedAndHomedAsync(grbl);
            machine.Disconnect();
            Assert.False(machine.IsHomed, "Disconnect kept IsHomed");

            machine.IsHomed = true;
            machine.Connect();
            try
            {
                Assert.False(machine.IsHomed, "Connect kept a stale IsHomed");
            }
            finally
            {
                machine.Disconnect();
            }
        }
    }
}
