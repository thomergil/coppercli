#nullable enable
using coppercli.Core.Communication;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// Loading a file into the machine to stream from a line after its first, as the rest of a
    /// job is rebuilt at a tool change.
    /// </summary>
    public class MachineSetFileTests
    {
        private static readonly string[] Lines = { "G21", "M6", "G0 X1" };
        private const int AfterTheToolChange = 2;

        /// <summary>Catches the stream put back at the start, which would send the lines already run again.</summary>
        [Fact]
        public void SetFile_StreamsFromTheLineGiven()
        {
            var machine = new Machine();

            Assert.True(machine.SetFile(Lines, AfterTheToolChange));

            Assert.Equal(Lines, machine.File);
            Assert.Equal(AfterTheToolChange, machine.FilePosition);
        }

        /// <summary>Catches a file loaded to stream from a line it does not have.</summary>
        [Theory]
        [InlineData(-1)]
        [InlineData(3)]
        public void SetFile_FromALineOutsideTheFile_IsRefused_AndKeepsTheFileItHad(int startAt)
        {
            var machine = new Machine();
            Assert.True(machine.SetFile(Lines));

            Assert.False(machine.SetFile(new[] { "G0 X2", "G0 X3", "G0 X4" }, startAt));

            Assert.Equal(Lines, machine.File);
        }
    }
}
