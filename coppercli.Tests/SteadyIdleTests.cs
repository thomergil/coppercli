#nullable enable
using System.Threading;
using coppercli.Core.Controllers;
using coppercli.Core.Util;
using coppercli.Tests.Fakes;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// These tests check that <see cref="SteadyIdle"/> reports steady only after Idle without a
    /// break for the whole steady time.
    /// </summary>
    [Collection(TimingSensitiveCollection.Name)]
    public class SteadyIdleTests
    {
        private const int SteadyMs = 600;

        /// <summary>Over half of SteadyMs and under all of it, so two of them together pass it but one does not.</summary>
        private const int PartMs = 320;

        /// <summary>Past SteadyMs with room for scheduling.</summary>
        private const int PastSteadyMs = 800;

        private static MockMachine Idle() => new() { Status = GrblProtocol.StatusIdle };

        /// <summary>
        /// Idle is not steady at the first reading, however the clock reads: a machine just
        /// seen Idle may be between two moves. Steady only once the time has passed.
        /// </summary>
        [Fact]
        public void IsNotSteadyAtTheFirstIdleReading_ButIsOnceTheSteadyTimeHasPassed()
        {
            var machine = Idle();
            var steady = new SteadyIdle(SteadyMs);

            Assert.False(steady.IsSteady(machine), "the first Idle reading was taken for a steady one");
            Thread.Sleep(PastSteadyMs);

            Assert.True(steady.IsSteady(machine), "Idle for the whole steady time was not steady");
        }

        /// <summary>
        /// A single reading that is not Idle starts the count again: Idle, a move, and Idle
        /// again is a machine between two moves, not one that has finished.
        /// </summary>
        [Fact]
        public void ANonIdleReading_RestartsTheCount()
        {
            var machine = Idle();
            var steady = new SteadyIdle(SteadyMs);
            Assert.False(steady.IsSteady(machine));
            Thread.Sleep(PartMs);

            machine.Status = GrblProtocol.StatusRun;
            Assert.False(steady.IsSteady(machine));
            machine.Status = GrblProtocol.StatusIdle;
            Assert.False(steady.IsSteady(machine));
            Thread.Sleep(PartMs);

            // More than SteadyMs since the first Idle, but under it since the last break.
            Assert.False(steady.IsSteady(machine), "an Idle interrupted by a move counted from before the move");

            Thread.Sleep(PastSteadyMs);
            Assert.True(steady.IsSteady(machine));
        }

        /// <summary>
        /// A caller with its own reason to think the machine is not done interrupts the count,
        /// and it starts again from the next Idle reading.
        /// </summary>
        [Fact]
        public void Interrupt_RestartsTheCount()
        {
            var machine = Idle();
            var steady = new SteadyIdle(SteadyMs);
            Assert.False(steady.IsSteady(machine));
            Thread.Sleep(PartMs);

            steady.Interrupt();
            Thread.Sleep(PartMs);

            Assert.False(steady.IsSteady(machine), "an interrupted count was not restarted");
            Thread.Sleep(PastSteadyMs);
            Assert.True(steady.IsSteady(machine));
        }

        /// <summary>
        /// A machine that never reads Idle is never steady, whatever the time.
        /// </summary>
        [Fact]
        public void AMachineThatNeverReadsIdle_IsNeverSteady()
        {
            var machine = new MockMachine { Status = GrblProtocol.StatusRun };
            var steady = new SteadyIdle(0);

            Assert.False(steady.IsSteady(machine));
            Thread.Sleep(PartMs);
            Assert.False(steady.IsSteady(machine));
        }
    }
}
