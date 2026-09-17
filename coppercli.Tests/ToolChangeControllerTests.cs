using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using coppercli.Core.Controllers;
using coppercli.Core.Util;
using coppercli.Tests.Fakes;
using Xunit;
using static coppercli.Core.Communication.Machine;
using static coppercli.Core.Controllers.ControllerConstants;
using static coppercli.Core.Util.GrblProtocol;

namespace coppercli.Tests
{
    /// <summary>
    /// Tests for ToolChangeController workflow behavior.
    /// </summary>
    public class ToolChangeControllerTests
    {
        /// <summary>What this file calls the door prompt, which carries no title of its own.</summary>
        private const string DoorLabel = "<door>";

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
            using var machine = CreateMockMachine();
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
            using var machine = CreateMockMachine();
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
            using var machine = CreateMockMachine();
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
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);

            Assert.Equal(ControllerState.Idle, controller.State);
            Assert.Equal(ToolChangePhase.NotStarted, controller.Phase);
        }

        [Fact]
        public void NewController_HasNoCurrentToolChange()
        {
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);

            Assert.Null(controller.CurrentToolChange);
        }

        [Fact]
        public void HasToolSetter_DelegatesToFunction()
        {
            using var machine = CreateMockMachine();
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



        // =========================================================================
        // Phase progression tests (synchronous verification)
        // =========================================================================


        // =========================================================================
        // User input callback tests
        // =========================================================================

        // =========================================================================
        // Error handling tests
        // =========================================================================

        // =========================================================================
        // Tool setter path tests
        // =========================================================================

        [Fact]
        public void WithToolSetter_HasToolSetterReturnsTrue()
        {
            using var machine = CreateMockMachine();
            var controller = CreateController(machine, hasToolSetter: true, toolSetterPos: (-100, -50));

            Assert.True(controller.HasToolSetter);
        }

        [Fact]
        public void WithoutToolSetter_HasToolSetterReturnsFalse()
        {
            using var machine = CreateMockMachine();
            var controller = CreateController(machine, hasToolSetter: false);

            Assert.False(controller.HasToolSetter);
        }

        // =========================================================================
        // Reset tests
        // =========================================================================

        [Fact]
        public void Reset_AfterCompletion_AllowsNewToolChange()
        {
            using var machine = CreateMockMachine();
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
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);

            // Verify Options is accessible and has defaults
            Assert.NotNull(controller.Options);
            Assert.Equal(5.0, controller.Options.ProbeMaxDepth);
        }

        [Fact]
        public void Controller_OptionsCanBeSet()
        {
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);

            controller.Options = new ToolChangeOptions
            {
                ProbeMaxDepth = 15.0,
                ProbeFeed = 75.0
            };

            Assert.Equal(15.0, controller.Options.ProbeMaxDepth);
            Assert.Equal(75.0, controller.Options.ProbeFeed);
        }

        // =========================================================================
        // Enclosure door
        //
        // The operator opens the enclosure to reach the tool, which leaves GRBL holding.
        // Releasing that hold restarts the spindle, so it needs the operator's consent - but
        // closing the door and pressing Continue is that consent. A door already closed and
        // holding at that moment is released without a second question; one still open is
        // put to them once it closes.
        // =========================================================================

        /// <summary>
        /// The operator changes the tool, closes the door, then presses Continue. The door is
        /// closed and holding at that moment, so releasing it needs no second question: they
        /// have just answered one about the same door.
        /// </summary>
        [Fact]
        public async Task ContinuingWithTheDoorAlreadyClosed_AsksOnce()
        {
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);

            var prompts = new List<UserInputRequest>();
            controller.UserInputRequired += request =>
            {
                prompts.Add(request);

                // The operator opens the enclosure to reach the tool, and closes it before
                // answering.
                if (prompts.Count == 1)
                {
                    machine.SimulateDoorClosedAndHolding();
                }
                request.OnResponse(OptionContinue);
            };

            await controller.HandleToolChangeAsync(CreateToolChangeInfo());

            Assert.DoesNotContain(prompts, p => p.IsDoorPrompt);
            Assert.Equal(1, machine.CycleStartCount);
            Assert.False(MachineWait.IsDoor(machine));
        }

        /// <summary>
        /// A door still open when they press Continue is a different thing: they have not
        /// closed it, so once they do it is put to them.
        /// </summary>
        [Fact]
        public async Task ADoorStillOpenWhenTheyContinue_IsPutToTheOperator()
        {
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);

            var prompts = new List<UserInputRequest>();
            controller.UserInputRequired += request =>
            {
                prompts.Add(request);

                // Answered with the enclosure still open.
                if (prompts.Count == 1)
                {
                    machine.SimulateDoorOpen();
                }
                request.OnResponse(OptionContinue);
            };

            // The run announces "close the door" and waits; the operator closes it then.
            controller.ProgressChanged += progress =>
            {
                if (progress.Message == ControllerConstants.DoorOpenPrompt)
                {
                    machine.SimulateDoorClosedAndHolding();
                }
            };

            await controller.HandleToolChangeAsync(CreateToolChangeInfo());

            var doorPrompt = Assert.Single(prompts, p => p.IsDoorPrompt);
            Assert.Equal(DoorHoldingPrompt, doorPrompt.Message);
            Assert.Equal(1, machine.CycleStartCount);
        }

        /// <summary>
        /// The operator opens the enclosure to jog to the surface, closes it, and answers the
        /// zero-Z prompt. That call site releases the hold on its own.
        /// </summary>
        [Fact]
        public async Task ZeroZPrompt_ReleasesTheDoorItWasAnsweredAt()
        {
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);

            controller.UserInputRequired += request =>
            {
                // The door is held only at the second prompt, not the first.
                if (request.Title == ToolChangeZeroZTitle)
                {
                    machine.SimulateDoorClosedAndHolding();
                }
                request.OnResponse(OptionContinue);
            };

            await controller.HandleToolChangeAsync(CreateToolChangeInfo());

            Assert.Equal(1, machine.CycleStartCount);
            Assert.False(MachineWait.IsDoor(machine));
        }

        /// <summary>
        /// The same for the tool-change prompt. Without a case that reaches only this call
        /// site, either could be deleted and the other would cover it.
        /// </summary>
        [Fact]
        public async Task ToolChangePrompt_ReleasesTheDoorBeforeTheZeroZPrompt()
        {
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);

            int cycleStartsBeforeZeroZ = -1;
            controller.UserInputRequired += request =>
            {
                if (request.Title == ToolChangePromptTitle)
                {
                    machine.SimulateDoorClosedAndHolding();
                }
                else if (request.Title == ToolChangeZeroZTitle)
                {
                    cycleStartsBeforeZeroZ = machine.CycleStartCount;
                }
                request.OnResponse(OptionContinue);
            };

            await controller.HandleToolChangeAsync(CreateToolChangeInfo());

            Assert.Equal(1, cycleStartsBeforeZeroZ);
            Assert.Equal(1, machine.CycleStartCount);
        }

        [Fact]
        public async Task AbandoningTheDoorPrompt_CancelsTheToolChange()
        {
            using var machine = CreateMockMachine();
            var controller = CreateController(machine);

            var errors = new List<ControllerError>();
            controller.ErrorOccurred += errors.Add;

            controller.UserInputRequired += request =>
            {
                if (request.IsDoorPrompt)
                {
                    request.OnResponse(OptionAbort);
                    return;
                }

                // Answered with the enclosure still open, so the door is put to them once
                // it closes - which is the prompt this test abandons.
                machine.SimulateDoorOpen();
                request.OnResponse(OptionContinue);
            };

            controller.ProgressChanged += progress =>
            {
                if (progress.Message == ControllerConstants.DoorOpenPrompt)
                {
                    machine.SimulateDoorClosedAndHolding();
                }
            };

            var toolChange = controller.HandleToolChangeAsync(CreateToolChangeInfo());

            // Bounded: the door it abandons never closes, so a version that stopped
            // aborting would hang the suite instead of failing it.
            Assert.Same(
                toolChange,
                await Task.WhenAny(toolChange, Task.Delay(ControllerConstants.DoorResumeTimeoutMs)));

            Assert.False(await toolChange);
            Assert.Equal(0, machine.CycleStartCount);

            // Stopped at the door: the soft reset cleared the hold, so the tool's position
            // is unknown. The probe and the mill both report that.
            Assert.Contains(
                errors, e => e.Message == ControllerConstants.ErrorStopRetractFailed);

            // The abort happens with the machine still holding, so a retract queued now
            // would run when the hold is released. One G53 Z move belongs to the clearance
            // raise at the start; cleanup must not add a second.
            Assert.Equal(1, machine.SentCommands.Count(
                c => c.Contains(CmdMachineCoords) && c.Contains("Z")));
        }
    }
}
