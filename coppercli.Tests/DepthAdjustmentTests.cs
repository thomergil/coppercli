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
    /// Depth adjustment shifts the G54 Z origin so a re-mill cuts deeper.
    ///
    /// The regression these pin: ApplyDepthAdjustment read the LIVE work offset, added
    /// the adjustment, and never restored it. Re-milling the same file without reloading
    /// therefore stacked the adjustment on the already-shifted offset - two runs at
    /// -0.05mm cut 0.10mm deep - while the UI still displayed -0.05. On 35um copper that
    /// is straight through the trace.
    /// </summary>
    public class DepthAdjustmentTests
    {
        /// <summary>
        /// The FIRST work-offset write of a run is the one that decides how deep this
        /// job cuts. (The last one is the restore.)
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

            // MillingController settles for PostIdleSettleMs (5s) before it touches the
            // work offset, so this has to outlast that.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await controller.StartAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // We only care about the offsets that were written.
            }
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

            // The second run must target the same absolute Z origin as the first, not
            // one that is another 0.05mm deeper.
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
        /// A tool change during the job rewrites the same G54 Z to compensate the new
        /// tool's length. Restoring an absolute snapshot at the end would throw that
        /// compensation away and the next plunge would be off by the length difference,
        /// so the restore has to take the adjustment back out relative to whatever the
        /// origin is by then.
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

            // Let the adjustment land, then simulate a tool change compensating Z.
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

            // The adjustment is gone; the tool compensation remains.
            Assert.Equal(before + toolCompensation, machine.G54Offset.Z, precision: 3);
        }

        // =========================================================================
        // _outstandingDepthAdjustment survives ResetRunState
        //
        // _depthAdjustment is this run's own snapshot of MillingOptions.DepthAdjustment
        // and ResetRunState clears it every run. _outstandingDepthAdjustment measures
        // something else - how much of that snapshot is still sitting in GRBL's G54 Z
        // unrestored - and describes the machine, not the run, so ResetRunState leaves
        // it alone. These tests drive one MillingController instance through two runs,
        // the way the session singleton actually gets reused, to pin that a restore
        // failure in run 1 is still taken back out in run 2 rather than forgotten the
        // moment Reset() runs.
        // =========================================================================

        private const int MillingReadyTimeoutMs = 20_000;
        private const int PhasePollIntervalMs = 10;

        /// <summary>All G10 L2 P1 Z... writes sent so far, in order.</summary>
        private static List<double> WorkOffsetWrites(IEnumerable<string> sentCommands)
        {
            string prefix = GrblProtocol.CmdSetWorkOffset + " Z";
            return sentCommands
                .Where(c => c.StartsWith(prefix))
                .Select(c => double.Parse(c.Substring(prefix.Length), System.Globalization.CultureInfo.InvariantCulture))
                .ToList();
        }

        /// <summary>
        /// Polls until the controller reaches the given phase, so the caller can act at
        /// a known point in the run (here: right after ApplyDepthAdjustmentAsync has
        /// returned, and before the file monitor loop does anything of its own).
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

        [Fact]
        public async Task MillingAfterAFailedRestore_StillTakesOutTheOldAdjustmentNextRun()
        {
            const double InitialG54Z = -2.0;
            const float RunOneAdjustment = -0.05f;

            var machine = new MockMachine
            {
                Status = GrblProtocol.StatusIdle,
                MachinePosition = new Vector3(0, 0, Constants.MillStartSafetyZ),
                WorkPosition = new Vector3(0, 0, 0),
                G54Offset = new Vector3(0, 0, InitialG54Z),
                WorkOffsetQuerySucceeds = true,
            };
            machine.LoadFile("G21", "G90", "G1 X1 Y1 F100");

            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { DepthAdjustment = RunOneAdjustment, RequireHoming = false }
            };

            // === Run 1: the adjustment applies, then the restore is refused ===
            using (var cts = new CancellationTokenSource())
            {
                var run = controller.StartAsync(cts.Token);
                try
                {
                    await WaitUntilPhaseAsync(controller, MillingPhase.Milling, MillingReadyTimeoutMs);

                    // RestoreDepthAdjustmentAsync, called from CleanupAsync once this run
                    // is cancelled, reads G54 back before it can write anything - the
                    // documented "could not restore" path, the same way GRBL not
                    // answering $# after a soft reset behaves.
                    machine.WorkOffsetQuerySucceeds = false;
                }
                finally
                {
                    cts.Cancel();
                    await run;
                }
            }

            Assert.Equal(ControllerState.Cancelled, controller.State);

            // Only the apply write landed - restore bailed out on the failed query
            // before it ever called SendLine, so the shift is still sitting in G54 with
            // only the outstanding field left to say so.
            var run1Writes = WorkOffsetWrites(machine.SentCommands);
            Assert.Single(run1Writes);
            Assert.Equal(InitialG54Z + RunOneAdjustment, run1Writes[0], precision: 3);

            controller.Reset();
            Assert.Equal(ControllerState.Idle, controller.State);

            // === Run 2: nothing of its own to apply, but the machine still owes -0.05 ===
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

            // Run 2's own adjustment is 0, so ApplyDepthAdjustmentAsync touched nothing.
            // The only write that can appear here is RestoreDepthAdjustmentAsync taking
            // out what run 1 left behind - and it targets the ORIGINAL -0.05, not 0,
            // which is exactly what clearing _outstandingDepthAdjustment in
            // ResetRunState would break: run 2 would owe nothing and write nothing.
            var run2Writes = WorkOffsetWrites(machine.SentCommands);
            Assert.Single(run2Writes);
            Assert.Equal(InitialG54Z - RunOneAdjustment, run2Writes[0], precision: 3);
        }
    }
}
