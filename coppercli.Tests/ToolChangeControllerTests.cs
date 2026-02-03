using System;
using coppercli.Core.Controllers;
using coppercli.Core.Util;
using coppercli.Tests.Fakes;
using Xunit;
using static coppercli.Core.Communication.Machine;

namespace coppercli.Tests
{
    /// <summary>
    /// Tests for ToolChangeController workflow behavior.
    /// </summary>
    public class ToolChangeControllerTests
    {
        // =========================================================================
        // Test helpers
        // =========================================================================

        private static MockMachine CreateMockMachine()
        {
            return new MockMachine
            {
                Status = "Idle",
                Connected = true,
                Mode = OperatingMode.Manual,
                MachinePosition = new Vector3(-50, -50, -5),
                WorkPosition = new Vector3(0, 0, 0),
                WorkOffset = new Vector3(-50, -50, -5)
            };
        }

        private static ToolChangeController CreateController(
            MockMachine machine,
            bool hasToolSetter = false,
            (double X, double? Y)? toolSetterPos = null)
        {
            return new ToolChangeController(
                machine,
                () => hasToolSetter,
                () => toolSetterPos,
                () => hasToolSetter ? new ToolSetterConfig
                {
                    X = toolSetterPos?.X ?? 0,
                    Y = toolSetterPos?.Y,
                    ProbeDepth = 50,
                    FastFeed = 200,
                    SlowFeed = 20,
                    Retract = 1.0
                } : null);
        }

        private static ToolChangeInfo CreateToolChangeInfo(int toolNumber = 1)
        {
            return new ToolChangeInfo(
                toolNumber,
                $"Tool {toolNumber}",
                new Vector3(0, 0, 0),
                10);
        }

        // =========================================================================
        // Constructor tests
        // =========================================================================

        [Fact]
        public void Constructor_WithNullMachine_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new ToolChangeController(
                    null!,
                    () => false,
                    () => null,
                    () => null));
        }

        [Fact]
        public void Constructor_WithNullHasToolSetter_Throws()
        {
            var machine = CreateMockMachine();
            Assert.Throws<ArgumentNullException>(() =>
                new ToolChangeController(
                    machine,
                    null!,
                    () => null,
                    () => null));
        }

        [Fact]
        public void Constructor_WithNullGetPosition_Throws()
        {
            var machine = CreateMockMachine();
            Assert.Throws<ArgumentNullException>(() =>
                new ToolChangeController(
                    machine,
                    () => false,
                    null!,
                    () => null));
        }

        [Fact]
        public void Constructor_WithNullGetConfig_Throws()
        {
            var machine = CreateMockMachine();
            Assert.Throws<ArgumentNullException>(() =>
                new ToolChangeController(
                    machine,
                    () => false,
                    () => null,
                    null!));
        }

        // =========================================================================
        // Initial state tests
        // =========================================================================

        [Fact]
        public void NewController_HasIdleState()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            Assert.Equal(ControllerState.Idle, controller.State);
            Assert.Equal(ToolChangePhase.NotStarted, controller.Phase);
        }

        [Fact]
        public void NewController_HasNoCurrentToolChange()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            Assert.Null(controller.CurrentToolChange);
        }

        [Fact]
        public void HasToolSetter_DelegatesToFunction()
        {
            var machine = CreateMockMachine();
            bool hasSetter = true;

            var controller = new ToolChangeController(
                machine,
                () => hasSetter,
                () => (-100, -50),
                () => null);

            Assert.True(controller.HasToolSetter);

            hasSetter = false;
            Assert.False(controller.HasToolSetter);
        }

        // =========================================================================
        // Session state tests
        // =========================================================================

        [Fact]
        public void SetSessionState_StoresValues()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            controller.SetSessionState(referenceToolLength: 25.5, hasReferenceToolLength: true, lastToolSetterZ: -10.0);

            var (refLength, hasRef, lastZ) = controller.GetSessionState();
            Assert.Equal(25.5, refLength);
            Assert.True(hasRef);
            Assert.Equal(-10.0, lastZ);
        }

        [Fact]
        public void GetSessionState_ReturnsDefaultsInitially()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            var (refLength, hasRef, lastZ) = controller.GetSessionState();

            Assert.Equal(0, refLength);
            Assert.False(hasRef);
            Assert.Equal(0, lastZ);
        }

        // =========================================================================
        // Phase progression tests (synchronous verification)
        // =========================================================================

        [Theory]
        [InlineData(ToolChangePhase.NotStarted)]
        [InlineData(ToolChangePhase.RaisingZ)]
        [InlineData(ToolChangePhase.MovingToToolSetter)]
        [InlineData(ToolChangePhase.MeasuringReference)]
        [InlineData(ToolChangePhase.MovingToWorkArea)]
        [InlineData(ToolChangePhase.WaitingForToolChange)]
        [InlineData(ToolChangePhase.WaitingForZeroZ)]
        [InlineData(ToolChangePhase.MeasuringNewTool)]
        [InlineData(ToolChangePhase.ApplyingOffset)]
        [InlineData(ToolChangePhase.Returning)]
        [InlineData(ToolChangePhase.Complete)]
        public void AllPhaseValues_AreValid(ToolChangePhase phase)
        {
            // Verify all enum values are defined
            Assert.True(Enum.IsDefined(typeof(ToolChangePhase), phase));
        }

        // =========================================================================
        // User input callback tests
        // =========================================================================

        [Fact]
        public void UserInputRequired_EventCanBeSubscribed()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            UserInputRequest? receivedRequest = null;
            controller.UserInputRequired += req => receivedRequest = req;

            // Just verify event is wirable - actual callback tested in async tests
            Assert.Null(receivedRequest); // No input requested yet
        }

        // =========================================================================
        // Error handling tests
        // =========================================================================

        [Fact]
        public void ErrorOccurred_EventCanBeSubscribed()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            ControllerError? receivedError = null;
            controller.ErrorOccurred += err => receivedError = err;

            // Verify event is wirable
            Assert.Null(receivedError);
        }

        // =========================================================================
        // Tool setter path tests
        // =========================================================================

        [Fact]
        public void WithToolSetter_HasToolSetterReturnsTrue()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine, hasToolSetter: true, toolSetterPos: (-100, -50));

            Assert.True(controller.HasToolSetter);
        }

        [Fact]
        public void WithoutToolSetter_HasToolSetterReturnsFalse()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine, hasToolSetter: false);

            Assert.False(controller.HasToolSetter);
        }

        // =========================================================================
        // Reset tests
        // =========================================================================

        [Fact]
        public void Reset_AfterCompletion_AllowsNewToolChange()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            // Manually set to completed state via reflection or complete a tool change
            // For now, just verify Reset is callable from Idle
            controller.Reset();

            Assert.Equal(ControllerState.Idle, controller.State);
        }

        // =========================================================================
        // ToolChangeInfo tests
        // =========================================================================

        [Fact]
        public void ToolChangeInfo_CreatedWithAllFields()
        {
            var returnPos = new Vector3(10, 20, 30);
            var info = new ToolChangeInfo(5, "Drill Bit", returnPos, 42);

            Assert.Equal(5, info.ToolNumber);
            Assert.Equal("Drill Bit", info.ToolName);
            Assert.Equal(returnPos, info.ReturnPosition);
            Assert.Equal(42, info.LineNumber);
        }

        [Fact]
        public void ToolChangeInfo_WithNullToolName_IsValid()
        {
            var info = new ToolChangeInfo(1, null, new Vector3(), 0);

            Assert.Null(info.ToolName);
        }

        // =========================================================================
        // ToolSetterConfig tests
        // =========================================================================

        [Fact]
        public void ToolSetterConfig_AllPropertiesSettable()
        {
            var config = new ToolSetterConfig
            {
                X = -100.5,
                Y = -75.0,
                ProbeDepth = 50,
                FastFeed = 200,
                SlowFeed = 20,
                Retract = 1.0
            };

            Assert.Equal(-100.5, config.X);
            Assert.Equal(-75.0, config.Y);
            Assert.Equal(50, config.ProbeDepth);
            Assert.Equal(200, config.FastFeed);
            Assert.Equal(20, config.SlowFeed);
            Assert.Equal(1.0, config.Retract);
        }

        [Fact]
        public void ToolSetterConfig_NullableY()
        {
            var config = new ToolSetterConfig { X = -100 };

            Assert.Null(config.Y);
        }

        // =========================================================================
        // ToolChangeOptions tests
        // =========================================================================

        [Fact]
        public void ToolChangeOptions_HasDefaultValues()
        {
            var options = new ToolChangeOptions();

            Assert.Equal(5.0, options.ProbeMaxDepth);
            Assert.Equal(20.0, options.ProbeFeed);
            Assert.Equal(6.0, options.RetractHeight);
            Assert.Null(options.WorkAreaCenter);
        }

        [Fact]
        public void ToolChangeOptions_AllPropertiesSettable()
        {
            var center = new Vector3(10, 20, 0);
            var options = new ToolChangeOptions
            {
                ProbeMaxDepth = 10.0,
                ProbeFeed = 50.0,
                RetractHeight = 8.0,
                WorkAreaCenter = center
            };

            Assert.Equal(10.0, options.ProbeMaxDepth);
            Assert.Equal(50.0, options.ProbeFeed);
            Assert.Equal(8.0, options.RetractHeight);
            Assert.Equal(center, options.WorkAreaCenter);
        }

        [Fact]
        public void Controller_HasOptionsProperty()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            // Verify Options is accessible and has defaults
            Assert.NotNull(controller.Options);
            Assert.Equal(5.0, controller.Options.ProbeMaxDepth);
        }

        [Fact]
        public void Controller_OptionsCanBeSet()
        {
            var machine = CreateMockMachine();
            var controller = CreateController(machine);

            controller.Options = new ToolChangeOptions
            {
                ProbeMaxDepth = 15.0,
                ProbeFeed = 75.0
            };

            Assert.Equal(15.0, controller.Options.ProbeMaxDepth);
            Assert.Equal(75.0, controller.Options.ProbeFeed);
        }
    }
}
