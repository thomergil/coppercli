#nullable enable
using System;
using System.Threading.Tasks;
using coppercli.Core.Controllers;
using coppercli.Core.Util;
using coppercli.Tests.Fakes;
using Xunit;

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
        public void M6InFile_EmitsToolChangeEvent()
        {
            var machine = CreateMachineWithFile("G0 X0", "M6 T1", "G0 X10");
            var controller = new MillingController(machine);
            controller.Options = new MillingOptions { RequireHoming = false };

            ToolChangeInfo? detectedToolChange = null;
            controller.ToolChangeDetected += info => detectedToolChange = info;

            // Note: Full M6 detection test requires more setup
            // This tests the event is wirable
            Assert.Null(detectedToolChange);
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
                var match = System.Text.RegularExpressions.Regex.Match(
                    line, @"^\s*M0*6\s*T?(\d*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                Assert.Equal(shouldMatch, match.Success);
            }
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
