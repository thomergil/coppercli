using System.Net.WebSockets;
using coppercli.WebServer;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// Which browser connection the server keeps when a new one arrives. Nothing enforces a
    /// single client, so this only decides what happens to the socket already stored.
    /// </summary>
    public class WebClientConnectionTests
    {
        /// <summary>
        /// A new connection from a browser replaces one it left behind, but not one still
        /// open: that belongs to a second tab, which would be left sending commands with no
        /// status.
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

        /// <summary>Another browser's connection is never replaced.</summary>
        [Theory]
        [InlineData(WebSocketState.Open)]
        [InlineData(WebSocketState.Closed)]
        public void AnotherBrowsersConnection_IsNeverSuperseded(WebSocketState storedState)
        {
            Assert.False(CncWebServer.IsSupersededClient("abc", storedState, "xyz"));
        }
    }
}
