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
        [InlineData(WsCmdReset)]
        [InlineData(WsCmdFeedhold)]
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
        /// HTTP only: each can be refused, and a refusal cannot come back over the WebSocket.
        /// </summary>
        [Theory]
        [InlineData(ApiHome)]
        [InlineData(ApiUnlock)]
        [InlineData(ApiResume)]
        public void RefusableCommands_AreNotOnTheWebSocket(string path)
        {
            var command = CncWebServer.FindHttpCommand(path);
            Assert.NotNull(command);
            Assert.Null(command!.WsCommand);
        }

        [Theory]
        [InlineData(ApiReset, true)]
        [InlineData(ApiFeedhold, true)]
        [InlineData(ApiResume, true)]
        [InlineData(ApiUnlock, true)]
        [InlineData(ApiHome, false)]
        [InlineData(ApiGotoOrigin, false)]
        [InlineData(ApiGotoCenter, false)]
        [InlineData(ApiGotoSafe, false)]
        [InlineData(ApiGotoRef, false)]
        [InlineData(ApiGotoZ0, false)]
        [InlineData(ApiProbeZ, false)]
        public void OnlyTheJobControls_RunDuringAJob(string path, bool duringRun)
        {
            Assert.Equal(duringRun, CncWebServer.FindHttpCommand(path)?.DuringRun);
        }
    }
}
