using System.Net.WebSockets;
using coppercli.WebServer;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// Which browser connection the server keeps when a new one arrives. Nothing enforces a
    /// single client, so the rule only decides what to do with the socket it already has.
    /// </summary>
    public class WebClientConnectionTests
    {
        /// <summary>
        /// A new connection from a browser replaces one it left behind, but not one that is
        /// still open: that belongs to a second tab, which would then be sending commands
        /// with no status.
        /// </summary>
        [Theory]
        [InlineData(WebSocketState.Open, false)]
        [InlineData(WebSocketState.Closed, true)]
        [InlineData(WebSocketState.Aborted, true)]
        [InlineData(WebSocketState.CloseReceived, true)]
        [InlineData(WebSocketState.CloseSent, true)]
        public void OnlyAClosedConnectionFromTheSameBrowserIsSuperseded(
            WebSocketState storedState, bool superseded)
        {
            Assert.Equal(superseded, CncWebServer.IsSupersededClient("abc", storedState, "abc"));
        }

        /// <summary>Another browser's connection is never replaced.</summary>
        [Theory]
        [InlineData(WebSocketState.Open)]
        [InlineData(WebSocketState.Closed)]
        public void AnotherBrowsersConnectionIsNeverSuperseded(WebSocketState storedState)
        {
            Assert.False(CncWebServer.IsSupersededClient("abc", storedState, "xyz"));
        }
    }
}
