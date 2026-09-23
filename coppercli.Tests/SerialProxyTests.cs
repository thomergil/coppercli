#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using coppercli.Core.Communication;
using coppercli.Core.Util;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// The proxy is the only thing that stops the machine when a remote terminal drops in
    /// the middle of a job, and it must never open the serial port while the server has it.
    /// A fake serial link records what reached the port and when it was closed.
    /// </summary>
    public class SerialProxyTests
    {
        private const string PortName = "fake";
        private const int WaitTimeoutMs = 5000;
        private const int ReplyTimeoutMs = 5000;
        private const int ReplyBufferSize = 1024;

        private static readonly byte[] StopSequence = ExpectedStopSequence();

        private static byte[] ExpectedStopSequence()
        {
            var wire = new MemoryStream();
            Machine.WriteStopSequence(wire);
            return wire.ToArray();
        }

        /// <summary>Records every byte written to the port, and the bytes written before it closed.</summary>
        private sealed class FakeSerialLink : ISerialLink
        {
            private readonly object _lock = new();
            private readonly List<byte> _written = new();
            private readonly RecordingStream _stream;

            public FakeSerialLink()
            {
                _stream = new RecordingStream(this);
            }

            public bool IsOpen { get; private set; } = true;
            public int BytesToRead => 0;
            public Stream BaseStream => _stream;

            /// <summary>Null until Close; then everything written before it.</summary>
            public byte[]? WrittenBeforeClose { get; private set; }

            /// <summary>When set, Close waits on it, to hold a session in its close.</summary>
            public ManualResetEventSlim? CloseGate { get; init; }

            public ManualResetEventSlim Closing { get; } = new(false);

            public int Read(byte[] buffer, int offset, int count) => 0;

            public void Write(byte[] buffer, int offset, int count)
            {
                lock (_lock)
                {
                    _written.AddRange(buffer.Skip(offset).Take(count));
                }
            }

            public void Close()
            {
                Closing.Set();
                CloseGate?.Wait(WaitTimeoutMs);
                lock (_lock)
                {
                    WrittenBeforeClose ??= _written.ToArray();
                    IsOpen = false;
                }
            }

            public void Dispose() => Close();

            private sealed class RecordingStream : MemoryStream
            {
                private readonly FakeSerialLink _link;

                public RecordingStream(FakeSerialLink link)
                {
                    _link = link;
                }

                public override void Write(byte[] buffer, int offset, int count) =>
                    _link.Write(buffer, offset, count);

                public override void WriteByte(byte value) => _link.Write(new[] { value }, 0, 1);
            }
        }

        /// <summary>A started proxy on a free port, with the links it opened and the releases it made.</summary>
        private sealed class ProxyUnderTest : IDisposable
        {
            private int _releases;

            public ProxyUnderTest(Func<bool> claim, Func<FakeSerialLink>? nextLink = null)
            {
                Proxy = new SerialProxy
                {
                    TryClaimSerialPort = claim,
                    ReleaseSerialPort = () => Interlocked.Increment(ref _releases),
                    OpenSerialLink = (_, _) =>
                    {
                        var link = nextLink?.Invoke() ?? new FakeSerialLink();
                        lock (Links)
                        {
                            Links.Add(link);
                        }
                        return link;
                    }
                };
                Port = WebServerFixture.FreeTcpPort();
                Proxy.Start(PortName, Constants.DefaultBaudRate, Port);
            }

            public SerialProxy Proxy { get; }
            public int Port { get; }

            /// <summary>The first link is the one Start opens to check the port.</summary>
            public List<FakeSerialLink> Links { get; } = new();

            public int Releases => Volatile.Read(ref _releases);

            public FakeSerialLink SessionLink
            {
                get
                {
                    lock (Links)
                    {
                        return Links[^1];
                    }
                }
            }

            public int LinksOpened
            {
                get
                {
                    lock (Links)
                    {
                        return Links.Count;
                    }
                }
            }

            public TcpClient Connect()
            {
                var client = new TcpClient();
                client.Connect(IPAddress.Loopback, Port);
                return client;
            }

            public void Dispose() => Proxy.Stop();
        }

        /// <summary>Everything the proxy sends before it closes the connection.</summary>
        private static string ReadUntilClosed(TcpClient client)
        {
            client.ReceiveTimeout = ReplyTimeoutMs;
            var stream = client.GetStream();
            var text = new StringBuilder();
            var buffer = new byte[ReplyBufferSize];
            try
            {
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    text.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }
            }
            catch (IOException)
            {
                // The proxy reset the connection after its message.
            }
            return text.ToString();
        }

        private static void AssertStoppedBeforeClosing(FakeSerialLink link)
        {
            Assert.NotNull(link.WrittenBeforeClose);
            Assert.Equal(StopSequence, link.WrittenBeforeClose!.TakeLast(StopSequence.Length));
        }

        /// <summary>
        /// A terminal that closes its connection mid-job leaves GRBL working through its
        /// planner buffer unless the proxy stops it before closing the port.
        /// </summary>
        [Fact]
        public void AClientThatLeaves_IsFollowedByAStop_ThenTheClose_ThenTheRelease()
        {
            using var proxy = new ProxyUnderTest(() => true);
            var client = proxy.Connect();
            WebServerFixture.WaitUntil(() => proxy.Proxy.HasClient, "the proxy to admit the client");

            client.Close();

            WebServerFixture.WaitUntil(() => proxy.Releases == 1, "the proxy to release the port", WaitTimeoutMs);
            AssertStoppedBeforeClosing(proxy.SessionLink);
        }

        /// <summary>A browser taking the machine back from a terminal ends its session the same way.</summary>
        [Fact]
        public void AForcedDisconnect_IsFollowedByAStop_ThenTheClose_ThenTheRelease()
        {
            using var proxy = new ProxyUnderTest(() => true);
            using var client = proxy.Connect();
            WebServerFixture.WaitUntil(() => proxy.Proxy.HasClient, "the proxy to admit the client");

            Assert.True(proxy.Proxy.ForceDisconnectClient());

            WebServerFixture.WaitUntil(() => proxy.Releases == 1, "the proxy to release the port", WaitTimeoutMs);
            AssertStoppedBeforeClosing(proxy.SessionLink);
        }

        /// <summary>
        /// Stopping the server with a terminal attached must stop the machine once, before the
        /// port closes, whichever of the session thread and Stop gets there first.
        /// </summary>
        [Fact]
        public void StoppingWithAClientAttached_StopsTheMachineOnceBeforeClosing()
        {
            var proxy = new ProxyUnderTest(() => true);
            using var client = proxy.Connect();
            WebServerFixture.WaitUntil(() => proxy.Proxy.HasClient, "the proxy to admit the client");

            proxy.Dispose();

            var link = proxy.SessionLink;
            AssertStoppedBeforeClosing(link);
            Assert.Equal(StopSequence, link.WrittenBeforeClose);
        }

        /// <summary>
        /// While the server has the machine, a terminal is refused before the proxy touches
        /// the port, and nothing is released that was never claimed.
        /// </summary>
        [Fact]
        public void AClientTheServerRefuses_IsToldSo_AndThePortIsNeverOpened()
        {
            using var proxy = new ProxyUnderTest(() => false);
            using var client = proxy.Connect();

            string reply = ReadUntilClosed(client);

            Assert.StartsWith(Constants.ProxySerialPortInUsePrefix, reply);
            Assert.Equal(1, proxy.LinksOpened);
            Assert.Equal(0, proxy.Releases);
            Assert.False(proxy.Proxy.HasClient);
        }

        /// <summary>
        /// A port that fails to open must still be released, or the server never gets the
        /// machine back and every later terminal is refused.
        /// </summary>
        [Fact]
        public void APortThatFailsToOpen_IsStillReleased_AndTheNextClientIsAdmitted()
        {
            int opens = 0;
            using var proxy = new ProxyUnderTest(() => true, () =>
            {
                // The first open is Start's check; the second is the first session's.
                if (Interlocked.Increment(ref opens) == 2)
                {
                    throw new IOException("simulated open failure");
                }
                return new FakeSerialLink();
            });

            using (var first = proxy.Connect())
            {
                Assert.StartsWith(Constants.ProxySerialPortBusyPrefix, ReadUntilClosed(first));
            }
            WebServerFixture.WaitUntil(() => proxy.Releases == 1, "the failed session to release the port", WaitTimeoutMs);

            using var second = proxy.Connect();
            WebServerFixture.WaitUntil(() => proxy.Proxy.HasClient, "the proxy to admit the next client");
        }

        /// <summary>
        /// A session's client is gone before it has stopped the machine and closed the port. A
        /// terminal that connects in between must be refused, or two sessions have the port and
        /// the first one's release lets the server connect under the second.
        /// </summary>
        [Fact]
        public void ANewClient_IsRefusedWhileThePreviousSessionIsStillClosingThePort()
        {
            using var closeGate = new ManualResetEventSlim(false);
            var closingLink = new FakeSerialLink { CloseGate = closeGate };
            int opens = 0;
            // The first open is Start's check; the second is the first session's.
            using var proxy = new ProxyUnderTest(() => true,
                () => Interlocked.Increment(ref opens) == 2 ? closingLink : new FakeSerialLink());
            try
            {
                var first = proxy.Connect();
                WebServerFixture.WaitUntil(() => proxy.Proxy.HasClient, "the proxy to admit the first client");
                first.Close();
                Assert.True(closingLink.Closing.Wait(WaitTimeoutMs), "the first session never reached its close");

                using var second = proxy.Connect();

                Assert.StartsWith(Constants.ProxyConnectionRejectedPrefix, ReadUntilClosed(second));
                Assert.Equal(2, proxy.LinksOpened);
            }
            finally
            {
                closeGate.Set();
            }
        }

        /// <summary>One terminal at a time: a second one must not share the port.</summary>
        [Fact]
        public void ASecondClient_IsRejectedWhileOneIsAttached()
        {
            using var proxy = new ProxyUnderTest(() => true);
            using var first = proxy.Connect();
            WebServerFixture.WaitUntil(() => proxy.Proxy.HasClient, "the proxy to admit the first client");

            using var second = proxy.Connect();

            Assert.StartsWith(Constants.ProxyConnectionRejectedPrefix, ReadUntilClosed(second));
        }
    }
}
