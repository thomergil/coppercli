using System;
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
    }
}
