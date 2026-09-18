using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using coppercli.Core.Controllers;
using coppercli.Core.Util;
using coppercli.Tests.Fakes;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// Depth adjustment shifts the G54 Z origin so a re-mill cuts deeper, and the run takes
    /// the shift back out when it ends. Added to an already-shifted origin instead, two runs
    /// at -0.05mm cut 0.10mm deep while the display still reads -0.05, which on 35um copper
    /// cuts through the trace.
    /// </summary>
    public class DepthAdjustmentTests
    {
        /// <summary>
        /// The first work-offset write of a run sets the depth that run cuts at; the last one
        /// is the restore.
        /// </summary>
        private static double? AppliedWorkOffsetZ(FakeMachine machine)
        {
            string? first = machine.SentCommands
                .FirstOrDefault(c => c.StartsWith(GrblProtocol.CmdSetWorkOffset + " Z"));

            if (first == null)
            {
                return null;
            }

            return double.Parse(first.Substring((GrblProtocol.CmdSetWorkOffset + " Z").Length),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void ClearCommands(FakeMachine machine)
        {
            machine.ClearSentCommands();
        }

        private static async Task RunMillAsync(FakeMachine machine, float adjustment)
        {
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { DepthAdjustment = adjustment, RequireHoming = false }
            };

            // MillingController waits PostIdleSettleMs before it writes the work offset, so
            // this timeout has to outlast that.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await controller.StartAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // The cancellation is expected; only the offsets written matter here.
            }
        }

        /// <summary>
        /// A refused restore leaves the depth adjustment in the work origin.
        /// Report the amount so the operator knows the next job would use the wrong depth.
        /// </summary>
        [Fact]
        public async Task RefusedDepthAdjustmentRestore_IsReported()
        {
            using var machine = new FakeMachine();
            machine.LoadFile("G21", "G90", "G1 X1 Y1 F100");

            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { DepthAdjustment = -0.05f, RequireHoming = false }
            };

            var errors = new List<ControllerError>();
            controller.ErrorOccurred += errors.Add;

            // The machine takes the adjustment and refuses the restore, as an alarmed GRBL does.
            controller.StateChanged += state =>
            {
                if (state == ControllerState.Completing)
                {
                    machine.RefuseWorkOffsetWrites = true;
                }
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await controller.StartAsync(cts.Token); }
            catch (OperationCanceledException) { }

            Assert.Contains(
                errors,
                e => e.Message == string.Format(
                    ControllerConstants.ErrorDepthAdjustmentNotRestored, -0.05));
        }

        /// <summary>
        /// A refused restore leaves the prior adjustment in the work origin.
        /// The next run must still use the depth requested from the original work zero.
        /// </summary>
        [Fact]
        public async Task RunAfterRefusedRestore_UsesRequestedDepth()
        {
            using var machine = new FakeMachine();
            machine.LoadFile("G21", "G90", "G1 X1 Y1 F100");

            double baseline = machine.G54Offset.Z;

            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { DepthAdjustment = -0.05f, RequireHoming = false }
            };

            // The restore at the end of the first run is refused.
            controller.StateChanged += state =>
            {
                if (state == ControllerState.Completing)
                {
                    machine.RefuseWorkOffsetWrites = true;
                }
            };

            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                try { await controller.StartAsync(cts.Token); }
                catch (OperationCanceledException) { }
            }

            Assert.Equal(baseline - 0.05, machine.G54Offset.Z, precision: 4);

            // AppState keeps one controller for the session, and that instance holds the only
            // record that the origin is still shifted.
            controller.Reset();
            machine.RefuseWorkOffsetWrites = false;
            ClearCommands(machine);

            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                try { await controller.StartAsync(cts.Token); }
                catch (OperationCanceledException) { }
            }

            // Measured from the operator's zero, so the second run targets 0.05, not 0.10.
            double? applied = AppliedWorkOffsetZ(machine);
            Assert.NotNull(applied);
            Assert.Equal(baseline - 0.05, applied!.Value, precision: 4);
        }

        [Fact]
        public async Task RepeatedMills_DoNotStackTheDepthAdjustment()
        {
            using var machine = new FakeMachine();
            machine.LoadFile("G21", "G90", "G1 X1 Y1 F100");

            ClearCommands(machine);
            await RunMillAsync(machine, -0.05f);
            double? afterFirst = AppliedWorkOffsetZ(machine);

            ClearCommands(machine);
            await RunMillAsync(machine, -0.05f);
            double? afterSecond = AppliedWorkOffsetZ(machine);

            Assert.NotNull(afterFirst);
            Assert.NotNull(afterSecond);

            // The second run targets the same absolute Z origin as the first, rather than one
            // another 0.05mm deeper.
            Assert.Equal(afterFirst!.Value, afterSecond!.Value, precision: 4);
        }

        [Fact]
        public async Task AfterMilling_TheWorkOffsetIsRestored()
        {
            using var machine = new FakeMachine();
            machine.LoadFile("G21", "G90", "G1 X1 Y1 F100");

            double before = machine.WorkOffset.Z;
            await RunMillAsync(machine, -0.05f);

            Assert.Equal(before, machine.WorkOffset.Z, precision: 4);
        }

        [Fact]
        public async Task ZeroAdjustment_TouchesNoWorkOffset()
        {
            using var machine = new FakeMachine();
            machine.LoadFile("G21", "G90", "G1 X1 Y1 F100");

            await RunMillAsync(machine, 0f);

            Assert.Null(AppliedWorkOffsetZ(machine));
        }
    
        /// <summary>
        /// A tool change during the job rewrites the same G54 Z to compensate the new tool's
        /// length. The restore subtracts the adjustment from whatever the origin holds by
        /// then, since writing back an absolute snapshot would discard that compensation and
        /// the next plunge would be off by the length difference.
        /// </summary>
        [Fact]
        public async Task ToolLengthCompensationAppliedMidJob_SurvivesTheRestore()
        {
            using var machine = new FakeMachine();
            machine.LoadFile("G21", "G90", "G1 X1 Y1 F100");

            const double adjustment = -0.05;
            const double toolCompensation = 1.25;

            double before = machine.G54Offset.Z;

            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { DepthAdjustment = (float)adjustment, RequireHoming = false }
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var run = controller.StartAsync(cts.Token);

            // The adjustment is written after PostIdleSettleMs, so the tool change has to be
            // simulated past that point.
            await Task.Delay(TimeSpan.FromSeconds(7));
            machine.SendLine(GrblProtocol.CmdSetWorkOffset + " Z" +
                (machine.G54Offset.Z + toolCompensation).ToString("F3",
                    System.Globalization.CultureInfo.InvariantCulture));

            try
            {
                await run;
            }
            catch (OperationCanceledException)
            {
            }

            Assert.Equal(before + toolCompensation, machine.G54Offset.Z, precision: 3);
        }

        private const int MillingReadyTimeoutMs = 20_000;
        private const int PhasePollIntervalMs = 10;

        private static List<double> WorkOffsetWrites(IEnumerable<string> sentCommands)
        {
            string prefix = GrblProtocol.CmdSetWorkOffset + " Z";
            return sentCommands
                .Where(c => c.StartsWith(prefix))
                .Select(c => double.Parse(c.Substring(prefix.Length), System.Globalization.CultureInfo.InvariantCulture))
                .ToList();
        }

        /// <summary>
        /// Waiting for MillingPhase.Milling puts the caller after ApplyDepthAdjustmentAsync
        /// has returned and before the file monitor loop does anything of its own.
        /// </summary>
        private static async Task WaitUntilPhaseAsync(MillingController controller, MillingPhase phase, int timeoutMs)
        {
            var stopwatch = Stopwatch.StartNew();
            while (controller.Phase != phase && stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                await Task.Delay(PhasePollIntervalMs);
            }

            Assert.Equal(phase, controller.Phase);
        }

        /// <summary>
        /// ResetRunState clears the current run's _depthAdjustment. The separate
        /// _outstandingDepthAdjustment persists until a later run removes it from G54 Z.
        /// </summary>
        [Fact]
        public async Task NextRun_RemovesOutstandingDepthAdjustment()
        {
            const double InitialG54Z = -2.0;
            const float RunOneAdjustment = -0.05f;

            var machine = new MockMachine
            {
                Status = GrblProtocol.StatusIdle,
                MachinePosition = new Vector3(0, 0, Constants.SafeClearanceZ),
                WorkPosition = new Vector3(0, 0, 0),
                G54Offset = new Vector3(0, 0, InitialG54Z),
                WorkOffsetQuerySucceeds = true,
            };
            machine.LoadFile("G21", "G90", "G1 X1 Y1 F100");

            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { DepthAdjustment = RunOneAdjustment, RequireHoming = false }
            };

            using (var cts = new CancellationTokenSource())
            {
                var run = controller.StartAsync(cts.Token);
                try
                {
                    await WaitUntilPhaseAsync(controller, MillingPhase.Milling, MillingReadyTimeoutMs);

                    // RestoreDepthAdjustmentAsync, called from CleanupAsync once this run is
                    // cancelled, reads G54 back before it writes anything. A query that never
                    // answers is what GRBL does with $# after a soft reset.
                    machine.WorkOffsetQuerySucceeds = false;
                }
                finally
                {
                    cts.Cancel();
                    await run;
                }
            }

            Assert.Equal(ControllerState.Cancelled, controller.State);

            // The restore returns on the failed query before it calls SendLine, so only the
            // apply write appears and the shift is still in G54.
            var run1Writes = WorkOffsetWrites(machine.SentCommands);
            Assert.Single(run1Writes);
            Assert.Equal(InitialG54Z + RunOneAdjustment, run1Writes[0], precision: 3);

            controller.Reset();
            Assert.Equal(ControllerState.Idle, controller.State);

            machine.ResetRecording();
            machine.WorkOffsetQuerySucceeds = true;
            controller.Options = new MillingOptions { DepthAdjustment = 0f, RequireHoming = false };

            using (var cts = new CancellationTokenSource())
            {
                var run = controller.StartAsync(cts.Token);
                try
                {
                    await WaitUntilPhaseAsync(controller, MillingPhase.Milling, MillingReadyTimeoutMs);
                }
                finally
                {
                    cts.Cancel();
                    await run;
                }
            }

            // Run 2's own adjustment is 0, so the only write is RestoreDepthAdjustmentAsync
            // taking out the -0.05 run 1 left. Clearing _outstandingDepthAdjustment in
            // ResetRunState would leave run 2 with nothing to write.
            var run2Writes = WorkOffsetWrites(machine.SentCommands);
            Assert.Single(run2Writes);
            Assert.Equal(InitialG54Z - RunOneAdjustment, run2Writes[0], precision: 3);
        }
    }
}
