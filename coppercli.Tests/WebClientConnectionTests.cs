using System.Net.WebSockets;
using coppercli.WebServer;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// IsSupersededClient governs which stored browser connection a new one replaces. Nothing
    /// limits the server to one client, so it applies only to the socket already stored.
    /// </summary>
    public class WebClientConnectionTests
    {
        /// <summary>
        /// A reconnecting browser replaces its own closed socket but not one still open: the
        /// open socket is a second tab, which would then send commands and receive no status.
        /// </summary>
        [Theory]
        [InlineData(WebSocketState.Open, false)]
        [InlineData(WebSocketState.Closed, true)]
        [InlineData(WebSocketState.Aborted, true)]
        [InlineData(WebSocketState.CloseReceived, true)]
        [InlineData(WebSocketState.CloseSent, true)]
        public void OnlyAClosedConnectionFromTheSameBrowser_IsSuperseded(
            WebSocketState storedState, bool superseded)
        {
            Assert.Equal(superseded, CncWebServer.IsSupersededClient("abc", storedState, "abc"));
        }

        [Theory]
        [InlineData(WebSocketState.Open)]
        [InlineData(WebSocketState.Closed)]
        public void AnotherBrowsersConnection_IsNeverSuperseded(WebSocketState storedState)
        {
            Assert.False(CncWebServer.IsSupersededClient("abc", storedState, "xyz"));
        }
    }
}
