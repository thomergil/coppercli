#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using coppercli.Core.Util;

namespace coppercli.Tests.Fakes
{
    /// <summary>
    /// A GRBL on a loopback port that the real Machine can connect to, stream to, and read
    /// status from. Moves land instantly, so tests do not wait for travel.
    /// </summary>
    public sealed class FakeGrbl : IDisposable
    {
        private const string WelcomeBanner = "grbl v1.1f ['$' for help]";
        private const byte StatusQuery = (byte)'?';
        private const byte FeedHold = (byte)GrblProtocol.FeedHold;
        private const byte SoftReset = (byte)GrblProtocol.SoftReset;
        private const int AcceptPollMs = 20;

        private readonly TcpListener _listener;
        private readonly Thread _thread;
        private readonly object _lock = new();
        private readonly List<string> _received = new();
        private volatile bool _running = true;
        private TcpClient? _client;
        private Stream? _stream;

        private double _x, _y, _z;
        private string _state = GrblProtocol.StatusIdle;

        public FakeGrbl()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _thread = new Thread(Serve) { IsBackground = true, Name = "FakeGrbl" };
            _thread.Start();
        }

        public int Port { get; }

        /// <summary>The Z a probe reports contact at. The tool stops there.</summary>
        public double ProbeContactZ { get; set; } = -1.0;

        /// <summary>Every line sent, in order, without its terminator.</summary>
        public IReadOnlyList<string> Received
        {
            get { lock (_lock) { return _received.ToArray(); } }
        }

        /// <summary>What <see cref="Received"/> holds where a real-time byte arrived.</summary>
        public const string FeedHoldMark = "<feed-hold>";

        /// <inheritdoc cref="FeedHoldMark"/>
        public const string SoftResetMark = "<soft-reset>";

        public int FeedHoldCount => CountOf(FeedHoldMark);
        public int SoftResetCount => CountOf(SoftResetMark);

        private int CountOf(string mark)
        {
            lock (_lock)
            {
                int n = 0;
                foreach (var line in _received)
                {
                    if (line == mark) { n++; }
                }
                return n;
            }
        }

        private void Record(string line)
        {
            lock (_lock) { _received.Add(line); }
        }

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); } catch { /* shutting down */ }
            try { _client?.Close(); } catch { /* shutting down */ }
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        private void Serve()
        {
            while (_running)
            {
                try
                {
                    if (!_listener.Pending())
                    {
                        Thread.Sleep(AcceptPollMs);
                        continue;
                    }

                    _client = _listener.AcceptTcpClient();
                    _stream = _client.GetStream();
                    Send(WelcomeBanner);
                    ReadLoop(_stream);
                }
                catch (Exception)
                {
                    // Closing the listener or the client is how this thread ends.
                    if (!_running) { return; }
                }
            }
        }

        private void ReadLoop(Stream stream)
        {
            var line = new StringBuilder();

            while (_running)
            {
                int b = stream.ReadByte();
                if (b < 0)
                {
                    return;
                }

                // Real-time bytes arrive anywhere in the stream, including mid-line.
                if (b == StatusQuery) { SendStatus(); continue; }

                if (b == FeedHold)
                {
                    Record(FeedHoldMark);
                    _state = GrblProtocol.StatusHold;
                    continue;
                }

                if (b == SoftReset)
                {
                    Record(SoftResetMark);
                    _state = GrblProtocol.StatusIdle;
                    Send(WelcomeBanner);
                    continue;
                }

                if (b == '\n' || b == '\r')
                {
                    if (line.Length > 0)
                    {
                        Handle(line.ToString());
                        line.Clear();
                    }
                    continue;
                }

                line.Append((char)b);
            }
        }

        private void Handle(string line)
        {
            Record(line);

            if (line.StartsWith(GrblProtocol.CmdViewParameters, StringComparison.Ordinal))
            {
                Send("[G54:0.000,0.000,0.000]");
                Send("[G28:0.000,0.000,0.000]");
                Send("[TLO:0.000]");
                Send("[PRB:0.000,0.000,0.000:0]");
                Send(GrblProtocol.ResponseOk);
                return;
            }

            if (line.StartsWith(GrblProtocol.CmdHome, StringComparison.Ordinal))
            {
                _x = _y = _z = 0;
                Send(GrblProtocol.ResponseOk);
                return;
            }

            if (line.StartsWith("G38.", StringComparison.Ordinal))
            {
                _z = ProbeContactZ;
                Send(GCodeFormat.Inv($"[PRB:{_x:F3},{_y:F3},{_z:F3}:1]"));
                Send(GrblProtocol.ResponseOk);
                return;
            }

            ApplyMove(line);
            Send(GrblProtocol.ResponseOk);
        }

        /// <summary>Reads the axis words so the reported position follows the move.</summary>
        private void ApplyMove(string line)
        {
            _x = AxisWord(line, 'X') ?? _x;
            _y = AxisWord(line, 'Y') ?? _y;
            _z = AxisWord(line, 'Z') ?? _z;
        }

        private static double? AxisWord(string line, char axis)
        {
            int i = line.IndexOf(axis);
            if (i < 0) { return null; }

            int end = i + 1;
            while (end < line.Length && (char.IsDigit(line[end]) || line[end] == '.' || line[end] == '-'))
            {
                end++;
            }

            string word = line.Substring(i + 1, end - i - 1);
            return double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? v
                : null;
        }

        private void SendStatus()
        {
            string pos = GCodeFormat.Inv($"{_x:F3},{_y:F3},{_z:F3}");
            Send($"<{_state}"
                + $"|{GrblProtocol.FieldMachinePos}:{pos}"
                + $"|{GrblProtocol.FieldFeedSpindle}:0,0"
                + $"|{GrblProtocol.FieldWorkCoordOffset}:0.000,0.000,0.000>");
        }

        private void Send(string line)
        {
            var stream = _stream;
            if (stream == null) { return; }

            try
            {
                byte[] bytes = Encoding.ASCII.GetBytes(line + "\r\n");
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
            catch (Exception)
            {
                // A disconnect looks like a failed write from here.
            }
        }
    }
}
