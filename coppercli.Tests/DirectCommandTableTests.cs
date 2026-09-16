using coppercli.WebServer;
using Xunit;
using static coppercli.WebServer.WebConstants;

namespace coppercli.Tests
{
    /// <summary>
    /// The HTTP endpoints and the WebSocket commands run one table, so a message reaches the
    /// machine only by naming an entry in it.
    /// </summary>
    public class DirectCommandTableTests
    {
        /// <summary>
        /// A message with no type names no command. Matching null would pick out the entries
        /// that have no WebSocket command, which include the feed override.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not-a-command")]
        public void AMessageNamingNoCommandRunsNothing(string? type)
        {
            Assert.Null(CncWebServer.FindWsCommand(type));
        }

        /// <summary>Every command the browser is told about is one this server runs.</summary>
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

        /// <summary>
        /// Stop, hold, resume, unlock and the feed override are the controls an operator
        /// reaches for because a job is running, so only they are allowed while one is.
        /// </summary>
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
        public void OnlyTheControlsForARunningJobMayRunDuringOne(string type, bool duringRun)
        {
            Assert.Equal(duringRun, CncWebServer.FindWsCommand(type)?.DuringRun);
        }
    }
}
