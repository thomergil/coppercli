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
    /// Covers zeroing the work origin, and the
    /// <see cref="WorkZeroOutcomeExtensions.LeftTheGCodeWrong"/> result that both the terminal
    /// and the browser branch on. A wrong result there leaves the operator cutting with
    /// corrections measured against an origin that has moved.
    /// </summary>
    public class WorkZeroOutcomeTests
    {
        /// <summary>
        /// G10 L20 moves the origin, and machine.G54Offset is only as fresh as the last $#.
        /// Left stale, every later height map records the pre-zero origin, so the next pass
        /// over the same board is refused as a changed setup.
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

        [Fact]
        public async Task WorkOffsetReadbackFailure_ReportsUnknown()
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
        /// Expected values written out one per outcome. Computing them from
        /// LeftTheGCodeWrong instead would make any change to it agree with itself.
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
        public void ExpectedOutcomeTable_MatchesOutcomeEnum()
        {
            Assert.Equal(
                Enum.GetValues<WorkZeroOutcome>().ToHashSet(),
                LeavesTheGCodeWrong.Keys.ToHashSet());
        }

        [Fact]
        public void LeftTheGCodeWrong_MatchesExpectedValues()
        {
            foreach (var outcome in Enum.GetValues<WorkZeroOutcome>())
            {
                Assert.Equal(LeavesTheGCodeWrong[outcome], outcome.LeftTheGCodeWrong());
            }
        }

        /// <summary>
        /// GRBL refuses G-code while alarmed, so the origin was not written. Recording it
        /// anyway would leave an X or Y zero deleting the height map for a datum the machine
        /// never had.
        /// </summary>
        [Fact]
        public async Task AnAlarmedMachine_ReportsTheOriginNotWritten()
        {
            using var machine = new MockMachine { Status = GrblProtocol.StatusAlarm };

            string? refused = await MachineWait.ZeroWorkOffsetAsync(machine, "X0 Y0 Z0", CancellationToken.None);

            Assert.Equal(ControllerConstants.ErrorWorkZeroNotWritten, refused);
        }

        /// <summary>
        /// A sleeping GRBL answers nothing, and no answer cannot say whether the line ran, so
        /// the operator is told the origin is unconfirmed rather than that it was not written.
        /// </summary>
        [Fact]
        public async Task AnUnansweredWrite_ReportsTheOriginUnconfirmed()
        {
            using var machine = new MockMachine { Status = GrblProtocol.StatusSleep };

            string? refused = await MachineWait.ZeroWorkOffsetAsync(machine, "X0 Y0 Z0", CancellationToken.None);

            Assert.Equal(ControllerConstants.ErrorWorkZeroUnconfirmed, refused);
        }

        /// <summary>
        /// JogMenu.ZeroedMessage looks its wording up by outcome and returns the bare axes
        /// line when the outcome has no entry. A new outcome then reaches the operator as
        /// "Z zeroed" with nothing about the height map.
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
                    Assert.Equal(zeroed, message);
                    continue;
                }

                Assert.NotEqual(zeroed, message);
                Assert.Contains(zeroed, message);
            }
        }

        /// <summary>
        /// GRBL rejects a locked-out line with error:9 and drops it. The caller must read
        /// that rejection rather than record an origin the machine does not have.
        /// </summary>
        [Fact]
        public async Task AnOffsetGrblLocksOut_IsReportedAsNotWritten()
        {
            using var machine = new MockMachine
            {
                AnswerTo = line => GrblReply.Refused(new GrblRejection(
                    GrblRejection.LockedOut, line, "G-code locked out during alarm or jog state"))
            };

            Assert.Equal(
                ControllerConstants.ErrorWorkZeroNotWritten,
                await MachineWait.ZeroWorkOffsetAsync(machine, "X0 Y0 Z0", CancellationToken.None));
        }

        /// <summary>
        /// Any other rejection is reported as GRBL's own message text, not a coppercli constant.
        /// </summary>
        [Fact]
        public async Task AnOffsetRefusedForAnotherReason_ReportsWhatGrblSaid()
        {
            const string because = "unsupported statement";
            using var machine = new MockMachine
            {
                AnswerTo = line => GrblReply.Refused(
                    new GrblRejection(GrblRejection.HomingNotEnabled, line, because))
            };

            Assert.Equal(
                because,
                await MachineWait.ZeroWorkOffsetAsync(machine, "Z0", CancellationToken.None));
        }

        /// <summary>
        /// A door hold is not a rejection: GRBL holds the line in its planner and runs it on
        /// cycle start, which is how a tool change re-zeroes Z.
        /// </summary>
        [Fact]
        public async Task ClosedDoorHold_AllowsWorkOffsetWrite()
        {
            using var machine = MockMachine.AtADoor(GrblProtocol.DoorSubStateClosed);

            string? refused = await MachineWait.ZeroWorkOffsetAsync(machine, "Z0", CancellationToken.None);

            Assert.Null(refused);
            Assert.Contains(machine.SentCommands, line => line.Contains(GrblProtocol.CmdZeroWorkOffset));
        }
    }
}
