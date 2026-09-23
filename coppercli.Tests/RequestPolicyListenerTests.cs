using System.Net;
using System.Net.Sockets;
using System.Text;
using coppercli.WebServer;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// HttpListener builds <c>request.Url</c> from the local endpoint, so RequestPolicy must
    /// read the Host header; predicate-only tests cannot catch that adapter error. HttpListener
    /// rejects requests without Host before a handler runs, so this fixture omits them.
    /// </summary>
    public class RequestPolicyListenerTests
    {
        private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(10);

        [Theory]
        [InlineData("Host: evil.com", false)]                                 // Url.Host would be loopback
        [InlineData("Host: 127.0.0.1:{0}", true)]
        [InlineData("Host: 127.0.0.1:{0}\r\norigin: http://evil.com", false)] // header lookup is case-insensitive
        [InlineData("Host: 127.0.0.1:{0}\r\nOrIgIn: http://evil.com", false)]
        [InlineData("Host: 127.0.0.1:{0}\r\nSec-Fetch-Site: cross-site", false)]
        [InlineData("Host: 127.0.0.1:{0}\r\nOrigin: http://127.0.0.1:{0}", true)]
        public async Task RequestPolicy_UsesHostHeaderInsteadOfListenerUrl(string headers, bool expected)
        {
            Assert.Equal(expected, await CheckRequestPolicyAsync(headers));
        }

        /// <summary>
        /// Sends a hand-built request to a real HttpListener and returns the policy's decision
        /// on the resulting context. The OS picks the port, so a parallel run or a busy CI
        /// machine cannot collide.
        /// </summary>
        private static async Task<bool> CheckRequestPolicyAsync(string headers)
        {
            int port = WebServerFixture.FreeTcpPort();
            var listener = new HttpListener();

            // The + prefix matches how the server binds, and is what lets a hostile Host
            // header reach a handler. Windows refuses it without elevation, so fall back as
            // CncWebServer.Run does: the narrower prefix never delivers such a request, and
            // the timeout below reads that as refused.
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
                    // not match - never becomes a context. Without the timeout the await
                    // would hang the run.
                    context = await pending.WaitAsync(ReplyTimeout);
                }
                catch (TimeoutException)
                {
                    return false;
                }

                bool allowed = RequestPolicy.IsAllowed(context.Request);
                context.Response.StatusCode = allowed ? 200 : 403;
                context.Response.Close();
                return allowed;
            }
            finally
            {
                listener.Close();
            }
        }

    }
}
