using System.Net;
using System.Net.Sockets;
using System.Text;
using coppercli.WebServer;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// Pins the adapter rather than the predicate: that the guard reads the Host *header*,
    /// and not <c>request.Url</c>, which HttpListener synthesises from the local endpoint
    /// rather than from what the caller asked for.
    ///
    /// This is not a theoretical distinction. The access check this replaced read
    /// <c>request.Url.Host</c>, and for "Host: evil.com" that yields the loopback address -
    /// so restoring it would look like a simplification, would admit the rebinding case,
    /// and would leave every predicate test in RequestGuardTests green.
    ///
    /// A request with no Host header at all is absent here on purpose: HttpListener rejects
    /// it before any handler sees it, so there is no context to ask the guard about.
    /// </summary>
    public class RequestGuardListenerTests
    {
        private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(10);

        [Theory]
        [InlineData("Host: evil.com", false)]                                 // Url.Host would say loopback
        [InlineData("Host: 127.0.0.1:{0}", true)]
        [InlineData("Host: 127.0.0.1:{0}\r\norigin: http://evil.com", false)] // header lookup is case-insensitive
        [InlineData("Host: 127.0.0.1:{0}\r\nOrIgIn: http://evil.com", false)]
        [InlineData("Host: 127.0.0.1:{0}\r\nSec-Fetch-Site: cross-site", false)]
        [InlineData("Host: 127.0.0.1:{0}\r\nOrigin: http://127.0.0.1:{0}", true)]
        public async Task GuardReadsTheHostHeaderNotTheSynthesisedUrl(string headers, bool expected)
        {
            Assert.Equal(expected, await AskGuardAsync(headers));
        }

        /// <summary>
        /// Sends a hand-built request to a real listener and returns the guard's verdict on
        /// the resulting context. Port 0 lets the OS pick a free one, so a parallel run or a
        /// busy CI box cannot collide.
        /// </summary>
        private static async Task<bool> AskGuardAsync(string headers)
        {
            int port = FreePort();
            var listener = new HttpListener();

            // Matches how the server itself binds, and is what lets a hostile Host header
            // reach a handler at all. Windows refuses this prefix without elevation, so
            // fall back the way CncWebServer.Run does; a request the narrower prefix will
            // not match is simply never delivered, which the timeout below reads as refused.
            try
            {
                listener.Prefixes.Add($"http://+:{port}/");
                listener.Start();
            }
            catch (HttpListenerException)
            {
                listener = new HttpListener();
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Start();
            }

            try
            {
                var pending = listener.GetContextAsync();

                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);

                string request = "GET /api/status HTTP/1.1\r\n"
                                 + string.Format(headers, port) + "\r\n\r\n";
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(request));

                HttpListenerContext context;

                try
                {
                    // A request HttpListener drops - malformed, or a Host the prefix does
                    // not match - never becomes a context. Nothing was allowed in that case,
                    // and without the timeout the await would hang the whole run.
                    context = await pending.WaitAsync(ReplyTimeout);
                }
                catch (TimeoutException)
                {
                    return false;
                }

                bool allowed = RequestGuard.IsAllowed(context.Request);
                context.Response.StatusCode = allowed ? 200 : 403;
                context.Response.Close();
                return allowed;
            }
            finally
            {
                listener.Close();
            }
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }
}
