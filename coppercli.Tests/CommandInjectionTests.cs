using coppercli.Core.Communication;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// One SendLine call must put exactly one command on the wire.
    ///
    /// The regression this pins: /api/zero interpolated a client-supplied axis list
    /// straight into a G-code line. A newline in that list appended commands of the
    /// caller's choosing - spindle on, then a plunge - to the line we meant to send.
    /// GRBL's real-time bytes are the same hazard by another route.
    /// </summary>
    public class CommandInjectionTests
    {
        [Theory]
        [InlineData("X0\nM3 S24000")]        // a second command smuggled in
        [InlineData("X0\r\nG1 Z-25 F800")]
        [InlineData("X0\u0018")]              // GRBL soft reset
        [InlineData("X0\u0085")]              // GRBL jog cancel
        public void ControlCharacters_AreRejected(string payload)
        {
            Assert.True(Machine.ContainsControlCharacter(payload));
        }

        [Theory]
        [InlineData("G53 G0 Z-1.000")]
        [InlineData("G10 L20 P0 X0 Y0 Z0")]
        [InlineData("G38.3 Z-10.000 F50.0")]
        [InlineData("$H")]
        [InlineData("$X")]
        [InlineData("G0\tX1")]   // tab is legal G-code whitespace
        public void OrdinaryCommands_AreAccepted(string line)
        {
            Assert.False(Machine.ContainsControlCharacter(line));
        }

    }
}
