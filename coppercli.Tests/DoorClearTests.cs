#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using coppercli.Core.Controllers;
using coppercli.Core.Util;
using coppercli.Tests.Fakes;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// MachineWait.ClearDoorHoldAsync is the policy a run, the terminal and the browser all
    /// follow: which door states the operator can answer, how many refused releases are
    /// enough, and which states are waited out instead. ControllerBaseTests covers the run's
    /// side; these are the answers only the terminal reaches.
    /// </summary>
    public class DoorClearTests
    {
        private static Task<bool> Yes(string message) => Task.FromResult(true);

        private static Task<bool> No(string message) => Task.FromResult(false);

        [Fact]
        public async Task AClosedDoorTheOperatorConfirms_IsReleased()
        {
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateClosed);
            var asked = new List<string>();
            var announced = new List<string>();

            var outcome = await MachineWait.ClearDoorHoldAsync(
                machine,
                ask: message => { asked.Add(message); return Yes(message); },
                announce: announced.Add);

            Assert.Equal(DoorClearOutcome.Cleared, outcome);
            Assert.Single(asked);
            Assert.Equal(1, machine.CycleStartCount);

            // The question is replaced as soon as it is answered, so the operator does not
            // sit looking at a prompt they have already answered.
            Assert.Equal(ControllerConstants.DoorResumingMessage, Assert.Single(announced));
        }

        /// <summary>
        /// Declining must not send the cycle start: it restarts the spindle and moves the
        /// tool back.
        /// </summary>
        [Fact]
        public async Task AClosedDoorTheOperatorDeclines_SendsNothing()
        {
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateClosed);

            var outcome = await MachineWait.ClearDoorHoldAsync(
                machine,
                ask: No,
                announce: _ => Assert.Fail("a closed door is a question, not an announcement"));

            Assert.Equal(DoorClearOutcome.Declined, outcome);
            Assert.Equal(0, machine.CycleStartCount);
        }

        /// <summary>
        /// A hold that survives the allowed releases points to the switch or its wiring, so
        /// the operator is told that instead of being asked again.
        /// </summary>
        [Fact]
        public async Task AHoldThatWillNotLift_StopsAsking()
        {
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateClosed);
            machine.IgnoreCycleStart = true;
            int asks = 0;

            var outcome = await MachineWait.ClearDoorHoldAsync(
                machine,
                ask: message => { asks++; return Yes(message); },
                announce: _ => { });

            Assert.Equal(DoorClearOutcome.WillNotRelease, outcome);
            Assert.Equal(ControllerConstants.MachineClearAttempts, asks);
        }

        /// <summary>
        /// An open door and a park restore have nothing the operator can answer: the cycle
        /// start would be refused. They are announced and waited out, and Escape ends the
        /// wait on the poll it was pressed.
        /// </summary>
        [Theory]
        [InlineData(GrblProtocol.DoorSubStateAjar, ControllerConstants.DoorOpenPrompt)]
        [InlineData(GrblProtocol.DoorSubStateRetracting, ControllerConstants.DoorOpenPrompt)]
        [InlineData(GrblProtocol.DoorSubStateResuming, ControllerConstants.DoorResumingMessage)]
        public async Task ADoorStateWithNothingToAnswer_IsAnnouncedAndCanBeGivenUpOn(
            string subState, string expected)
        {
            using var machine = MockMachine.AtADoor(subState);
            var announced = new List<string>();
            var clearing = MachineWait.ClearDoorHoldAsync(
                machine,
                ask: message =>
                {
                    Assert.Fail($"ClearDoorHoldAsync asked about door substate {subState}, "
                        + "which the machine will not take a cycle start in");
                    return No(message);
                },
                announce: announced.Add,
                // Stands in for the operator pressing Escape rather than waiting it out.
                onPoll: () => true);

            // Bounded: giving up must end the wait on the poll it was asked. Awaited without
            // this, a version that ignored onPoll would hang the suite instead of failing.
            Assert.Same(
                clearing,
                await Task.WhenAny(clearing, Task.Delay(ControllerConstants.DoorResumeTimeoutMs / 2)));

            var outcome = await clearing;

            Assert.Equal(DoorClearOutcome.Declined, outcome);
            Assert.Equal(expected, Assert.Single(announced));
            Assert.Equal(0, machine.CycleStartCount);
        }

        /// <summary>
        /// A run whose token is already cancelled must not be left waiting out a door. The
        /// waits return at once on a cancelled token without awaiting, so a loop that did not
        /// check the token itself spun with nothing to yield to.
        /// </summary>
        [Theory]
        [InlineData(GrblProtocol.DoorSubStateAjar)]
        [InlineData(GrblProtocol.DoorSubStateRetracting)]
        [InlineData(GrblProtocol.DoorSubStateResuming)]
        public async Task ADoorWaitOnACancelledRun_Ends(string subState)
        {
            using var machine = MockMachine.AtADoor(subState);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            int announced = 0;
            var clearing = Task.Run(() => MachineWait.ClearDoorHoldAsync(
                machine,
                ask: No,
                announce: _ => Interlocked.Increment(ref announced),
                ct: cancelled.Token));

            // On its own thread, because the defect is a loop with no await: called directly
            // it would never yield and the deadline below would never be reached.
            Assert.Same(
                clearing,
                await Task.WhenAny(clearing, Task.Delay(ControllerConstants.DoorResumeTimeoutMs)));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => clearing);
            Assert.True(announced <= 1, $"the wait announced {announced} times on a cancelled run");
        }

        [Fact]
        public async Task AMachineNotAtTheDoor_AsksNothing()
        {
            using var machine = new MockMachine();

            var outcome = await MachineWait.ClearDoorHoldAsync(
                machine,
                ask: message => { Assert.Fail("asked about a door the machine is not at"); return No(message); },
                announce: _ => Assert.Fail("announced a door the machine is not at"));

            Assert.Equal(DoorClearOutcome.Cleared, outcome);
        }
    }
}
