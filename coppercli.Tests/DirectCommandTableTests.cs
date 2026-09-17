using coppercli.WebServer;
using Xunit;
using static coppercli.WebServer.WebConstants;

namespace coppercli.Tests
{
    /// <summary>
    /// The HTTP endpoints and the WebSocket commands share one table, so a message reaches
    /// the machine only by naming an entry in it.
    /// </summary>
    public class DirectCommandTableTests
    {
        /// <summary>
        /// Matching on null would select the entries that carry no WebSocket command, among
        /// them the feed override.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not-a-command")]
        public void AMessageWithNoCommand_RunsNothing(string? type)
        {
            Assert.Null(CncWebServer.FindWsCommand(type));
        }

        [Theory]
        [InlineData(WsCmdHome)]
        [InlineData(WsCmdUnlock)]
        [InlineData(WsCmdReset)]
        [InlineData(WsCmdFeedhold)]
        [InlineData(WsCmdResume)]
        [InlineData(WsCmdGotoOrigin)]
        [InlineData(WsCmdGotoCenter)]
        [InlineData(WsCmdGotoSafe)]
        [InlineData(WsCmdGotoRef)]
        [InlineData(WsCmdGotoZ0)]
        [InlineData(WsCmdProbeZ)]
        public void EveryPublishedCommandResolves(string type)
        {
            Assert.NotNull(CncWebServer.FindWsCommand(type));
        }

        [Theory]
        [InlineData(WsCmdReset, true)]
        [InlineData(WsCmdFeedhold, true)]
        [InlineData(WsCmdResume, true)]
        [InlineData(WsCmdUnlock, true)]
        [InlineData(WsCmdHome, false)]
        [InlineData(WsCmdGotoOrigin, false)]
        [InlineData(WsCmdGotoCenter, false)]
        [InlineData(WsCmdGotoSafe, false)]
        [InlineData(WsCmdGotoRef, false)]
        [InlineData(WsCmdGotoZ0, false)]
        [InlineData(WsCmdProbeZ, false)]
        public void OnlyTheJobControls_RunDuringAJob(string type, bool duringRun)
        {
            Assert.Equal(duringRun, CncWebServer.FindWsCommand(type)?.DuringRun);
        }
    }
}
