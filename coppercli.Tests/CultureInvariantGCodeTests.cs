using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using coppercli.Core.Controllers;
using coppercli.Tests.Fakes;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// G-code numbers must always use '.' as the decimal separator, whatever locale the
    /// operator's machine is set to.
    ///
    /// The regression these pin: C# interpolated strings format with CurrentCulture. On a
    /// German/Dutch/French system "Z-1.000" is emitted as "Z-1,000", which GRBL rejects
    /// with error:2 ("numeric value format is not valid"). Because no controller observes
    /// GRBL errors, the safety retract silently does nothing and the next XY rapid runs at
    /// cutting depth.
    /// </summary>
    // Sets the process-wide culture, so it must not run alongside other test classes.
    [Collection("culture-sensitive")]
    public class CultureInvariantGCodeTests : IDisposable
    {
        private readonly CultureInfo _original = CultureInfo.CurrentCulture;

        public CultureInvariantGCodeTests()
        {
            // German: decimal comma, thousands dot - the worst case for G-code.
            var german = new CultureInfo("de-DE");
            CultureInfo.CurrentCulture = german;
            CultureInfo.DefaultThreadCurrentCulture = german;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _original;
            CultureInfo.DefaultThreadCurrentCulture = null;
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
            controller.SetupGrid(
                new Core.Util.Vector2(0, 0),
                new Core.Util.Vector2(10.5, 10.5),
                margin: 1.25,
                gridSize: 5.5);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await controller.StartAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Only the emitted text matters here.
            }

            Assert.NotEmpty(machine.SentCommands);
            AssertNoDecimalComma(machine);
        }

        /// <summary>The invariant formatter must be immune to the ambient culture.</summary>
        [Fact]
        public void GCodeFormat_IsImmuneToAmbientCulture()
        {
            Assert.Equal("Z-1.500", Core.Util.GCodeFormat.Inv($"Z{-1.5:F3}"));
            Assert.Equal("F47.5", Core.Util.GCodeFormat.Inv($"F{47.5:0.###}"));
        }
    }
}
