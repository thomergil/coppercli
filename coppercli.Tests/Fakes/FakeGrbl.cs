#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using coppercli.Core.Communication;
using coppercli.Core.Util;

namespace coppercli.Tests.Fakes
{
    /// <summary>
    /// A GRBL on a loopback port that the real Machine can connect to, stream to, and read
    /// status from. Moves complete immediately, so tests do not wait for travel.
    /// </summary>
    public sealed class FakeGrbl : IDisposable
    {
        /// <summary>What GRBL 1.1f prints each time it starts.</summary>
        private const string WelcomeBanner = "Grbl 1.1f ['$' for help]";

        /// <summary>The alarm GRBL reports when a reset ends a homing cycle.</summary>
        private const int AlarmHomingReset = 6;

        /// <summary>What GRBL prints after a reset that left it alarmed.</summary>
        private const string AlarmLockMessage = "[MSG:'$H'|'$X' to unlock]";

        private const byte StatusQuery = (byte)'?';
        private const byte CycleStart = (byte)GrblProtocol.CycleStart;
        private const byte FeedHold = (byte)GrblProtocol.FeedHold;
        private const byte SoftReset = (byte)GrblProtocol.SoftReset;
        private const int AcceptPollMs = 20;

        private readonly TcpListener _listener;
        private readonly Thread _thread;
        private readonly object _lock = new();
        private readonly List<string> _received = new();

        private readonly List<string> _queued = new();

        // GRBL's door rules, shared with the other two doubles.
        private readonly DoorModel _door;
        private volatile bool _running = true;
        private TcpClient? _client;
        private Stream? _stream;

        private double _x, _y, _z;
        private string _state = GrblProtocol.StatusIdle;

        /// <summary>
        /// The door switch, kept apart from <c>_state</c> because an alarmed GRBL does not
        /// report the door.
        /// </summary>
        private volatile bool _doorSwitchOpen;
        private string _subState = string.Empty;

        // When GRBL answers again, and why it stopped: a homing cycle, or a restart.
        private long _answeringAgainAtMs;
        private bool _homing;
        private bool _restarting;
        private bool _restartAlarmed;

        // Lines that arrived during a homing cycle. GRBL still takes the bytes and answers
        // them once the cycle ends. Lines that arrive during a restart are dropped instead:
        // GRBL clears its receive buffer as it starts.
        private readonly List<string> _deferred = new();

        public FakeGrbl()
        {
            _door = new DoorModel(SetState, () => GrblProtocol.StatusIdle);

            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _thread = new Thread(Serve) { IsBackground = true, Name = "FakeGrbl" };
            _thread.Start();
        }

        public int Port { get; }

        /// <summary>Every probe reports contact here; this fake never reports a miss.</summary>
        public double ProbeContactZ { get; set; } = -1.0;

        /// <summary>
        /// How long the homing cycle runs; zero finishes it before the $H is answered, for a
        /// test that does not care about the wait. GRBL drives the cycle from a loop that
        /// services no status query, so it answers nothing until the cycle ends.
        /// </summary>
        public int HomingMs { get; set; }

        /// <summary>
        /// How long a restart takes: GRBL answers nothing and drops every line until it prints
        /// its banner at the end. Zero restarts at once.
        /// </summary>
        public int RebootMs { get; set; }

        /// <summary>
        /// GRBL restarting on its own - a reset button, a brown-out - with nothing sent by
        /// coppercli. It comes back alarmed, as a board that requires homing does.
        /// </summary>
        public void SimulateRestart() => Restart(alarmed: true);

        /// <summary>
        /// A homing cycle failing as GRBL 1.1 reports it: the alarm, then the ok its $H
        /// handler still returns after the abort, then a restart.
        /// </summary>
        public void SimulateHomingFailure(int alarmCode)
        {
            Send($"{GrblProtocol.ResponseAlarmPrefix}:{alarmCode}");
            Send(GrblProtocol.ResponseOk);
            Restart(alarmed: true);
        }

        /// <summary>Every line sent, in order, without its terminator.</summary>
        public IReadOnlyList<string> Received
        {
            get { lock (_lock) { return _received.ToArray(); } }
        }

        /// <summary>What <see cref="Received"/> holds where a real-time byte arrived.</summary>
        public const string FeedHoldMark = "<feed-hold>";

        /// <summary><see cref="FeedHoldMark"/> for a cycle start.</summary>
        public const string CycleStartMark = "<cycle-start>";

        /// <inheritdoc cref="FeedHoldMark"/>
        public const string SoftResetMark = "<soft-reset>";

        public int FeedHoldCount => CountOf(FeedHoldMark);
        public int SoftResetCount => CountOf(SoftResetMark);
        public int CycleStartCount => CountOf(CycleStartMark);

        /// <summary>The door opens while GRBL is alarmed. See <see cref="DoorModel.RefusedAtOpenDoor"/>.</summary>
        public void SimulateDoorOpenWhileAlarmed()
        {
            SetState(GrblProtocol.StatusAlarm, string.Empty);
            _doorSwitchOpen = true;
        }

        /// <summary>The door closes again. GRBL stays alarmed.</summary>
        public void SimulateDoorClosedWhileAlarmed() => _doorSwitchOpen = false;

        public void SimulateDoorOpen() =>
            SetState(GrblProtocol.StatusDoor, GrblProtocol.DoorSubStateAjar);

        /// <summary>GRBL is moving to the park position after the door opened.</summary>
        public void SimulateDoorRetracting() =>
            SetState(GrblProtocol.StatusDoor, GrblProtocol.DoorSubStateRetracting);

        /// <summary>GRBL is moving the parked axes back after a cycle start.</summary>
        public void SimulateDoorResuming() =>
            SetState(GrblProtocol.StatusDoor, GrblProtocol.DoorSubStateResuming);

        /// <inheritdoc cref="DoorModel.RestoreMs"/>
        public int DoorRestoreMs
        {
            get => _door.RestoreMs;
            set => _door.RestoreMs = value;
        }

        /// <summary>
        /// The enclosure reads closed and GRBL keeps holding until it takes a cycle start.
        /// </summary>
        public void SimulateDoorClosedAndHolding() =>
            SetState(GrblProtocol.StatusDoor, GrblProtocol.DoorSubStateClosed);

        /// <summary>Writes state and substate together so neither retains an earlier value.</summary>
        private void SetState(string state, string subState)
        {
            _state = state;
            _subState = subState;
        }

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
            _door.Dispose();
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

                // Whatever silenced GRBL ends on its own, and the only clock this fake has
                // is the bytes arriving on it. The real Machine polls '?' throughout, so one
                // lands every poll interval whether or not it is answered.
                AnswerAgainIfDue();

                // Real-time bytes arrive anywhere in the stream, including mid-line.
                if (b == StatusQuery)
                {
                    if (!IsSilent)
                    {
                        SendStatus();
                    }
                    continue;
                }

                if (b == FeedHold)
                {
                    Record(FeedHoldMark);
                    if (DoorModel.FeedHoldApplies(_state))
                    {
                        SetState(GrblProtocol.StatusHold, string.Empty);
                    }
                    continue;
                }

                if (b == SoftReset)
                {
                    Record(SoftResetMark);

                    // GRBL's homing loop watches for the reset byte and fails the cycle with
                    // an alarm, which is the one way a stop can leave a machine that was
                    // idle a moment earlier refusing every G-code line that follows.
                    if (_homing)
                    {
                        Send($"{GrblProtocol.ResponseAlarmPrefix}:{AlarmHomingReset}");
                    }

                    line.Clear();
                    Restart(_homing || DoorModel.ResetAlarms(_state));
                    continue;
                }

                if (b == CycleStart)
                {
                    Record(CycleStartMark);

                    // A feed hold always resumes on a cycle start; a door hold resumes only
                    // under DoorModel's conditions.
                    if (_state == GrblProtocol.StatusHold)
                    {
                        SetState(GrblProtocol.StatusIdle, string.Empty);
                    }
                    else
                    {
                        _door.CycleStart(_state, _subState);
                    }

                    RunQueuedLines();
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

            if (IsSilent)
            {
                if (!_restarting)
                {
                    lock (_lock) { _deferred.Add(line); }
                }
                return;
            }

            if (DoorModel.RefusedAtOpenDoor(line, _state, _doorSwitchOpen))
            {
                Send($"{GrblProtocol.ResponseErrorPrefix}{GrblRejection.DoorOpen}");
                return;
            }

            if (DoorModel.ClearsAlarm(line, _state))
            {
                SetState(GrblProtocol.StatusIdle, string.Empty);
                Send(GrblProtocol.ResponseOk);
                return;
            }

            // The line is acknowledged, queued, and run on the cycle start. A fake that moved
            // anyway would report a retract as having run while the enclosure was open.
            if (DoorModel.Holding(_state))
            {
                lock (_lock) { _queued.Add(line); }
                Send(GrblProtocol.ResponseOk);
                return;
            }

            // GRBL locks G-code out in alarm and takes only its own $ commands until the
            // alarm is cleared. A fake that moved anyway would report a safety retract as
            // having run on a machine that refused it.
            if (DoorModel.LockedOut(line, _state))
            {
                Send($"{GrblProtocol.ResponseErrorPrefix}{GrblRejection.LockedOut}");
                return;
            }

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
                // The status poll runs on its own schedule, so a '?' issued just before the
                // $H is answered just after it. That report says nothing about whether GRBL
                // accepted the command, and a caller that judged the $H by it gets it wrong.
                SendStatus();

                if (HomingMs <= 0)
                {
                    FinishHoming();
                    return;
                }

                _homing = true;
                GoSilent(HomingMs);
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

        private void RunQueuedLines()
        {
            string[] queued;
            lock (_lock)
            {
                queued = _queued.ToArray();
                _queued.Clear();
            }

            foreach (string line in queued)
            {
                Handle(line);
            }
        }

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

        /// <summary>True while GRBL is busy in a routine that answers no status query.</summary>
        private bool IsSilent => Environment.TickCount64 < _answeringAgainAtMs;

        private void GoSilent(int forMs) =>
            _answeringAgainAtMs = Environment.TickCount64 + forMs;

        /// <summary>
        /// Ends a homing cycle or a restart once its time is up: a restart prints the banner,
        /// a cycle answers its $H, and lines held during the cycle are answered in order.
        /// </summary>
        private void AnswerAgainIfDue()
        {
            if (IsSilent)
            {
                return;
            }

            if (_restarting)
            {
                _restarting = false;
                Send(WelcomeBanner);
                if (_restartAlarmed)
                {
                    Send(AlarmLockMessage);
                }
            }

            if (_homing)
            {
                FinishHoming();
            }

            string[] waiting;
            lock (_lock)
            {
                if (_deferred.Count == 0)
                {
                    return;
                }

                waiting = _deferred.ToArray();
                _deferred.Clear();
            }

            foreach (string line in waiting)
            {
                Handle(line);
            }
        }

        /// <summary>
        /// GRBL starting again: it drops every line it held, runs nothing it had planned, and
        /// prints its banner when it is back.
        /// </summary>
        private void Restart(bool alarmed)
        {
            _homing = false;
            lock (_lock)
            {
                _deferred.Clear();
                _queued.Clear();
            }

            SetState(alarmed ? GrblProtocol.StatusAlarm : GrblProtocol.StatusIdle, string.Empty);

            _restartAlarmed = alarmed;
            _restarting = true;
            GoSilent(RebootMs);
            AnswerAgainIfDue();
        }

        /// <summary>The machine is at the origin, the cycle's ok arrives, and '?' is answered
        /// again.</summary>
        private void FinishHoming()
        {
            _homing = false;
            _x = _y = _z = 0;
            SetState(GrblProtocol.StatusIdle, string.Empty);
            Send(GrblProtocol.ResponseOk);
        }

        private void SendStatus()
        {
            string pos = GCodeFormat.Inv($"{_x:F3},{_y:F3},{_z:F3}");
            string state = _subState.Length == 0 ? _state : $"{_state}:{_subState}";
            Send($"<{state}"
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
