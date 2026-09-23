#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using coppercli;
using coppercli.WebServer;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// MachineHoldTests covers the hold's own rules against a fake connection; this drives it
    /// through the real HTTP and WebSocket API. A closed browser WebSocket must leave the
    /// machine connected, with homing and the work origin unchanged. Only a terminal takeover
    /// disconnects the machine; a browser takeover reconnects it on the hold's next attempt.
    /// </summary>
    [Collection(WebServerCollection.Name)]
    public class WebServerMachineHoldTests
    {
        private readonly WebServerFixture _web;

        public WebServerMachineHoldTests(WebServerFixture web)
        {
            _web = web;
            _web.RestoreFixtureState();
        }

        private HttpClient Client => _web.Client;

        // Long enough to catch a reconnect (or a disconnect) that should not happen; short
        // enough that the suite does not pay for the full terminal takeover window
        // (WebConstants.TerminalTakeoverWindowMs).
        private const int SettleCheckMs = 1000;
        private const int CloseTimeoutMs = 5000;
        private const int ReceiveBufferSize = 64 * 1024;

        private Task<HttpResponseMessage> PostBrowserTakeoverAsync() =>
            Client.PostAsync(WebConstants.ApiBrowserTakeover, null);

        private Task<HttpResponseMessage> PostTerminalTakeoverAsync() =>
            Client.PostAsync(WebConstants.ApiTerminalTakeover, null);

        private async Task<ClientWebSocket> OpenBrowserSocketAsync(string clientId)
        {
            var socket = new ClientWebSocket();
            // The upgrade goes through RequestPolicy like every other request; an Origin naming
            // this server is what a real browser page sends.
            socket.Options.SetRequestHeader(
                WebConstants.HeaderOrigin, $"http://127.0.0.1:{_web.Port}");

            var uri = new Uri(
                $"ws://127.0.0.1:{_web.Port}{WebConstants.WsPath}?{WebConstants.QueryParamClientId}={clientId}");
            await socket.ConnectAsync(uri, CancellationToken.None);
            return socket;
        }

        /// <summary>
        /// A closed browser WebSocket must leave the machine connected, and must leave homing
        /// and the work origin as they were.
        /// </summary>
        [Fact]
        public async Task ABrowserSocket_ThatConnectsAndCloses_LeavesTheMachineConnectedAndKeepsHomingAndWorkZero()
        {
            AppState.Machine.IsHomed = true;
            AppState.MarkWorkZeroSet();

            var socket = await OpenBrowserSocketAsync(Guid.NewGuid().ToString());
            try
            {
                WebServerFixture.WaitUntil(() => CncWebServer.HasWebClient, "the server to count the browser");

                // The full close handshake, so a server that stops answering a client's close
                // fails this on the timeout instead of leaving the socket open.
                using (var closeTimeout = new CancellationTokenSource(CloseTimeoutMs))
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closeTimeout.Token);
                }
                WebServerFixture.WaitUntil(() => !CncWebServer.HasWebClient, "the server to drop the closed browser");

                await AsyncWait.AssertStaysTrueAsync(() => AppState.Machine.Connected, SettleCheckMs,
                    "the machine disconnected after the browser's only tab closed");

                Assert.True(AppState.Machine.IsHomed,
                    "homing changed though nothing asked the server to disconnect the machine");
                Assert.True(AppState.IsWorkZeroSet,
                    "the work origin changed though nothing asked the server to disconnect the machine");
            }
            finally
            {
                socket.Dispose();
                AppState.Machine.IsHomed = false;
                AppState.ClearWorkZero();
            }
        }

        /// <summary>A browser takeover must never disconnect the machine.</summary>
        [Fact]
        public async Task ABrowserTakeover_LeavesTheMachineConnected()
        {
            Assert.True(AppState.Machine.Connected, "the fixture did not start this test connected");

            var response = await PostBrowserTakeoverAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await AsyncWait.AssertStaysTrueAsync(() => AppState.Machine.Connected, SettleCheckMs,
                "a browser takeover disconnected the machine");
        }

        /// <summary>
        /// A terminal takeover must disconnect the machine at once and keep it disconnected for
        /// the takeover window; a browser takeover afterward must reconnect it and re-enable
        /// automatic state clearing, which a workflow in progress would have turned off.
        /// </summary>
        [Fact]
        public async Task ATerminalTakeover_DisconnectsAtOnce_AndABrowserTakeoverReconnectsIt()
        {
            Assert.True(AppState.Machine.Connected, "the fixture did not start this test connected");
            Assert.True(CncWebServer.HoldsMachine, "the server did not start holding the machine");

            AppState.Machine.EnableAutoStateClear = false;

            try
            {
                var terminalResponse = await PostTerminalTakeoverAsync();
                Assert.Equal(HttpStatusCode.OK, terminalResponse.StatusCode);

                Assert.False(CncWebServer.HoldsMachine,
                    "the server still reports holding the machine right after a terminal takeover");
                Assert.False(AppState.Machine.Connected,
                    "the machine is still connected right after a terminal takeover");

                await AsyncWait.AssertStaysTrueAsync(() => !AppState.Machine.Connected, SettleCheckMs,
                    "the server reconnected during the terminal's own takeover window");

                var browserResponse = await PostBrowserTakeoverAsync();
                Assert.Equal(HttpStatusCode.OK, browserResponse.StatusCode);
                Assert.True(CncWebServer.HoldsMachine,
                    "the server does not report holding the machine right after a browser takeover");

                WebServerFixture.WaitUntil(() => AppState.Machine.Connected,
                    "the browser takeover to reconnect the machine",
                    timeoutMs: WebConstants.ReconnectIntervalMs * 2);

                Assert.True(AppState.Machine.EnableAutoStateClear,
                    "reconnecting after the browser takeover did not re-enable automatic state clearing");
            }
            finally
            {
                await EndTheTerminalTakeoverAsync();
            }
        }

        /// <summary>
        /// A test that fails partway through can leave the machine yielded to the terminal. A
        /// browser takeover ends that at once, so the next test starts connected.
        /// </summary>
        private async Task EndTheTerminalTakeoverAsync()
        {
            await PostBrowserTakeoverAsync();
            WebServerFixture.WaitUntil(() => AppState.Machine.Connected,
                "the machine to reconnect for the next test");
            _web.RestoreFixtureState();
        }

        /// <summary>
        /// While a terminal has the machine, a page that opens is offered the takeover.
        /// Without that it shows a disconnected machine and no way to get it back.
        /// </summary>
        [Fact]
        public async Task APageOpenedAfterATerminalTakeover_IsOfferedTheTakeover()
        {
            var socket = new ClientWebSocket();
            try
            {
                Assert.Equal(HttpStatusCode.OK, (await PostTerminalTakeoverAsync()).StatusCode);

                socket = await OpenBrowserSocketAsync(Guid.NewGuid().ToString());

                Assert.True(await ReceivesTakeoverOfferAsync(socket),
                    "the page was not offered the takeover while a terminal had the machine");
            }
            finally
            {
                socket.Dispose();
                await EndTheTerminalTakeoverAsync();
            }
        }

        /// <summary>Reads messages until the takeover offer, or false once the socket is quiet.</summary>
        private static async Task<bool> ReceivesTakeoverOfferAsync(ClientWebSocket socket)
        {
            var buffer = new byte[ReceiveBufferSize];
            using var timeout = new CancellationTokenSource(SettleCheckMs);
            try
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(buffer, timeout.Token);
                    var message = JsonDocument.Parse(buffer.AsMemory(0, result.Count)).RootElement;
                    if (message.GetProperty("type").GetString() == WebConstants.WsMessageTypeConnectionError
                        && message.GetProperty("data").GetProperty("otherClientConnected").GetBoolean())
                    {
                        return true;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        /// <summary>
        /// The hold logs and retries a failed connect only if the failure arrives as an
        /// exception; Machine.Connect reports it through an event and returns.
        /// </summary>
        [Fact]
        public void ConnectMachine_ThrowsWithTheReasonWhenConnectFails()
        {
            int closedPort = WebServerFixture.FreeTcpPort();

            var machine = new coppercli.Core.Communication.Machine(new coppercli.Core.Settings.MachineSettings
            {
                ConnectionType = coppercli.Core.Settings.ConnectionType.Ethernet,
                EthernetIP = IPAddress.Loopback.ToString(),
                EthernetPort = closedPort
            });
            string? reported = null;
            machine.NonFatalException += message => reported = message;

            var thrown = Assert.Throws<System.IO.IOException>(() => CncWebServer.ConnectMachine(machine));

            Assert.NotNull(reported);
            Assert.Equal(reported, thrown.Message);
            Assert.False(machine.Connected);
        }

        /// <summary>
        /// A terminal takeover must be refused while homing has the machine, and must not
        /// disconnect it, or a terminal taking the serial port mid-home leaves the machine
        /// stopped partway.
        /// </summary>
        [Fact]
        public async Task ATerminalTakeover_IsRefusedWhileTheMachineIsHoming()
        {
            Assert.True(AppState.Machine.Connected, "the fixture did not start this test connected");
            AppState.Machine.IsHoming = true;

            try
            {
                var response = await PostTerminalTakeoverAsync();

                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
                Assert.Equal(WebConstants.ErrorTakeoverWhileBusy,
                    body.GetProperty(WebConstants.JsonFieldError).GetString());
                Assert.True(AppState.Machine.Connected, "a refused terminal takeover disconnected the machine");
                Assert.True(CncWebServer.HoldsMachine, "a refused terminal takeover left the machine yielded");
            }
            finally
            {
                AppState.Machine.IsHoming = false;
            }
        }
    }
}
