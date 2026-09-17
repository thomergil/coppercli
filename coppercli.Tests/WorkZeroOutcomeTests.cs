#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using coppercli;
using coppercli.Core.Communication;
using coppercli.Core.Controllers;
using coppercli.Core.Util;
using coppercli.Tests.Fakes;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// Whether the loaded G-code still matches the origin is one answer, given by
    /// <see cref="WorkZeroOutcomeExtensions.LeftTheGCodeWrong"/>. Both the terminal and the browser
    /// branch on it, so an outcome it answers wrongly puts the operator back to cutting with
    /// corrections measured against an origin that has moved.
    /// </summary>
    public class WorkZeroOutcomeTests
    {
        /// <summary>
        /// G10 L20 moves the origin, and machine.G54Offset is only as fresh as the last $#.
        /// Left stale, every height map measured afterwards records the origin from before
        /// the operator zeroed, and the next pass over the same board is refused as a setup
        /// that changed.
        /// </summary>
        [Fact]
        public async Task ZeroingAnAxis_RereadsTheOffsetItJustWrote()
        {
            using var machine = new MockMachine();

            Assert.Null(await MachineWait.ZeroWorkOffsetAsync(machine, "Z0", CancellationToken.None));
            Assert.True(
                machine.WorkOffsetQueryCount > 0,
                "the origin moved and was never re-read, so every later map records the old one");
        }

        /// <summary>A machine that will not say where its origin is has an unknown one.</summary>
        [Fact]
        public async Task AnOffsetTheMachineWillNotReadBack_IsReportedAsUnknown()
        {
            using var machine = new MockMachine { WorkOffsetQuerySucceeds = false };

            Assert.Equal(
                ControllerConstants.ErrorWorkOffsetUnknown,
                await MachineWait.ZeroWorkOffsetAsync(machine, "Z0", CancellationToken.None));
        }

        /// <summary>
        /// At the door the write is still in GRBL's planner, so there is nothing to re-read
        /// and the query would queue behind it.
        /// </summary>
        [Fact]
        public async Task AtTheDoor_TheOffsetIsNotReadBack()
        {
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateClosed);
            machine.WorkOffsetQuerySucceeds = false;

            Assert.Null(await MachineWait.ZeroWorkOffsetAsync(machine, "Z0", CancellationToken.None));
            Assert.Equal(0, machine.WorkOffsetQueryCount);
        }

        /// <summary>
        /// One row per outcome. Rows, rather than a repeat of the predicate, so narrowing it
        /// to one of the two names fails here.
        /// </summary>
        private static readonly Dictionary<WorkZeroOutcome, bool> LeavesTheGCodeWrong = new()
        {
            [WorkZeroOutcome.NothingToDo] = false,
            [WorkZeroOutcome.MapDiscarded] = false,
            [WorkZeroOutcome.MapReapplied] = false,
            [WorkZeroOutcome.MapNotReapplied] = true,
            [WorkZeroOutcome.MapNotDiscarded] = true,
            [WorkZeroOutcome.FileLeftAlone] = false
        };

        [Fact]
        public void EveryOutcome_HasARowAndNoRowOutlivesItsOutcome()
        {
            // Set equality both ways: a missing row leaves an outcome unchecked, and a row
            // for an outcome that is gone makes the table look complete while one is not.
            Assert.Equal(
                Enum.GetValues<WorkZeroOutcome>().ToHashSet(),
                LeavesTheGCodeWrong.Keys.ToHashSet());
        }

        [Fact]
        public void EveryOutcome_AnswersWhetherItLeftTheGCodeWrong()
        {
            foreach (var outcome in Enum.GetValues<WorkZeroOutcome>())
            {
                Assert.Equal(LeavesTheGCodeWrong[outcome], outcome.LeftTheGCodeWrong());
            }
        }

        /// <summary>
        /// GRBL locks G-code out in Alarm, so the write is dropped. Recording the origin
        /// anyway leaves the machine offering to probe and mill from a datum it never had,
        /// and an X or Y zero deletes the height map on the strength of it.
        /// </summary>
        [Theory]
        [InlineData(GrblProtocol.StatusAlarm)]
        [InlineData(GrblProtocol.StatusSleep)]
        public async Task AMachineThatWillNotTakeTheOffset_IsNotSentOne(string status)
        {
            using var machine = new MockMachine { Status = status };

            string? refused = await MachineWait.ZeroWorkOffsetAsync(machine, "X0 Y0 Z0", CancellationToken.None);

            Assert.Equal(ControllerConstants.ErrorWorkZeroNotWritten, refused);
            Assert.DoesNotContain(machine.SentCommands, line => line.Contains(GrblProtocol.CmdZeroWorkOffset));
        }

        /// <summary>
        /// The terminal looks its wording up by outcome and falls through to the bare axes
        /// line for one it does not know. A new outcome would reach the operator as "Z zeroed"
        /// with nothing about the map.
        /// </summary>
        [Fact]
        public void EveryOutcome_HasWordsForTheTerminal()
        {
            const string zeroed = "Z zeroed";

            foreach (var outcome in Enum.GetValues<WorkZeroOutcome>())
            {
                string message = Menus.JogMenu.ZeroedMessage(zeroed, outcome);

                if (outcome == WorkZeroOutcome.NothingToDo)
                {
                    // Nothing happened to the map, so the axes line is the whole message.
                    Assert.Equal(zeroed, message);
                    continue;
                }

                Assert.NotEqual(zeroed, message);
                Assert.Contains(zeroed, message);
            }
        }

        /// <summary>
        /// GRBL answers a locked-out line with error:9 and drops it. Without reading that,
        /// the caller records an origin the machine does not have, and an X or Y zero then
        /// deletes the height map on the strength of it.
        /// </summary>
        [Fact]
        public async Task AnOffsetGrblLocksOut_IsReportedAsNotWritten()
        {
            using var machine = new MockMachine();
            machine.LineSent += line =>
            {
                if (line.Contains(GrblProtocol.CmdZeroWorkOffset))
                {
                    machine.SimulateRejection(
                        GrblRejection.LockedOut, line, "G-code locked out during alarm or jog state");
                }
            };

            Assert.Equal(
                ControllerConstants.ErrorWorkZeroNotWritten,
                await MachineWait.ZeroWorkOffsetAsync(machine, "X0 Y0 Z0", CancellationToken.None));
        }

        /// <summary>
        /// Any other refusal is GRBL's own wording, which names the line it would not take.
        /// </summary>
        [Fact]
        public async Task AnOffsetRefusedForAnotherReason_ReportsWhatGrblSaid()
        {
            const string because = "unsupported statement";
            using var machine = new MockMachine();
            machine.LineSent += line =>
            {
                if (line.Contains(GrblProtocol.CmdZeroWorkOffset))
                {
                    machine.SimulateRejection(GrblRejection.HomingNotEnabled, line, because);
                }
            };

            Assert.Equal(
                because,
                await MachineWait.ZeroWorkOffsetAsync(machine, "Z0", CancellationToken.None));
        }

        /// <summary>
        /// A door hold is not a refusal: GRBL keeps the line in its planner and runs it on
        /// the cycle start, which is how a tool change re-zeroes Z.
        /// </summary>
        [Fact]
        public async Task AMachineHoldingAtTheDoor_StillTakesTheOffset()
        {
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateClosed);

            string? refused = await MachineWait.ZeroWorkOffsetAsync(machine, "Z0", CancellationToken.None);

            Assert.Null(refused);
            Assert.Contains(machine.SentCommands, line => line.Contains(GrblProtocol.CmdZeroWorkOffset));
        }
    }
}
