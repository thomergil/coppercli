using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using coppercli.Core.Controllers;
using coppercli.Core.GCode;
using coppercli.Tests.Fakes;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// G-code numbers must use '.' as the decimal separator whatever locale the operator's
    /// machine is set to. C# interpolated strings format with CurrentCulture, so on a German,
    /// Dutch or French system "Z-1.000" comes out as "Z-1,000", which GRBL rejects with
    /// error:2 ("numeric value format is not valid"). No controller reads GRBL errors back,
    /// so the safety retract does nothing and the next XY rapid runs at cutting depth.
    /// </summary>
    // CurrentCulture is set on this thread only and flows into the awaits below with the
    // execution context, so tests on other threads keep theirs. Do not use
    // DefaultThreadCurrentCulture: it applies to the whole process, so a test asserting on a
    // formatted number would depend on what runs beside it.
    public class CultureInvariantGCodeTests : IDisposable
    {
        private readonly CultureInfo _original = CultureInfo.CurrentCulture;

        public CultureInvariantGCodeTests()
        {
            // German formats a decimal comma and a thousands dot, the worst case for G-code.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _original;
        }

        private static void AssertNoDecimalComma(FakeMachine machine)
        {
            List<string> offenders;
                offenders = machine.SentCommands.Where(c => c.Contains(',')).ToList();
            Assert.True(offenders.Count == 0,
                "Commands emitted with a decimal comma: " + string.Join(" | ", offenders));
        }

        [Fact]
        public async Task SafetyRetract_EmitsInvariantDecimalSeparator()
        {
            using var machine = new FakeMachine();
            await MachineWait.SafetyRetractZAsync(machine, -1.5, 500, CancellationToken.None);
            AssertNoDecimalComma(machine);
        }

        [Fact]
        public async Task ZeroWorkOffset_EmitsInvariantDecimalSeparator()
        {
            using var machine = new FakeMachine();
            await MachineWait.ZeroWorkOffsetAsync(machine, "Z0", CancellationToken.None);
            AssertNoDecimalComma(machine);
        }

        [Fact]
        public async Task ProbeControllerMoves_EmitInvariantDecimalSeparator()
        {
            using var machine = new FakeMachine();
            var controller = new ProbeController(machine)
            {
                Options = new ProbeOptions { SafeHeight = 6.5, MaxDepth = 10.25, ProbeFeed = 47.5 }
            };
            controller.LoadGrid(ProbeGrid.ForJob(
                new Core.Util.Vector2(0, 0),
                new Core.Util.Vector2(10.5, 10.5),
                margin: 1.25,
                gridSize: 5.5));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await controller.StartAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // The run need not finish; the assertions below read only what was emitted.
            }

            Assert.NotEmpty(machine.SentCommands);
            AssertNoDecimalComma(machine);
        }

        [Fact]
        public void GCodeFormat_IsImmuneToAmbientCulture()
        {
            Assert.Equal("Z-1.500", Core.Util.GCodeFormat.Inv($"Z{-1.5:F3}"));
            Assert.Equal("F47.5", Core.Util.GCodeFormat.Inv($"F{47.5:0.###}"));
        }
    }
}
