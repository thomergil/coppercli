using coppercli.Core.GCode;
using coppercli.Core.GCode.GCodeCommands;
using coppercli.Core.Settings;
using coppercli.Core.Util;
using static coppercli.Core.Util.GrblProtocol;
using static coppercli.Core.GCode.GCodeNumbers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace coppercli.Core.Communication
{
    public class Machine : IMachine
    {
        /// <summary>
        /// What the machine is being driven to do. A guard against a disconnected machine
        /// reads <see cref="Connected"/> instead; there is no mode for that.
        /// </summary>
        public enum OperatingMode
        {
            /// <summary>Accepting individual commands.</summary>
            Manual,

            SendFile,

            /// <summary>A probe is running and the reply is outstanding.</summary>
            Probe
        }

        public event Action<Vector3, bool> ProbeFinished;
        public event Action<string> NonFatalException;

        public event Action<GrblRejection> CommandRejected;
        public event Action<string> Info;
        public event Action<string> LineReceived;
        public event Action<string> StatusReceived;
        public event Action<string> LineSent;
        public event Action ConnectionStateChanged;
        public event Action PositionUpdateReceived;
        public event Action StatusChanged;
        public event Action DistanceModeChanged;
        public event Action UnitChanged;
        public event Action PlaneChanged;
        public event Action BufferStateChanged;
        public event Action PinStateChanged;
        public event Action OperatingModeChanged;
        public event Action FileChanged;
        public event Action FilePositionChanged;
        public event Action OverrideChanged;

        private static readonly Regex GCodeSplitter = new Regex(@"([GZ])\s*(\-?\d+\.?\d*)", RegexOptions.Compiled);
        private static readonly Regex StatusEx = new Regex(@"(?<=[<|])(\w+):?([^|>]*)?(?=[|>])", RegexOptions.Compiled);
        private static readonly Regex ProbeEx = new Regex(@"\[PRB:(?'Pos'\-?[0-9\.]*(?:,\-?[0-9\.]*)+):(?'Success'0|1)\]", RegexOptions.Compiled);
        private static readonly Regex StartupRegex = new Regex("grbl v([0-9])\\.([0-9])([a-z])", RegexOptions.Compiled);

        // Vector3 is a 24-byte struct, so one assignment is several machine words and a
        // reader on another thread can catch it half-updated: X and Y from the new status
        // report, Z from the old. These values decide where the next move goes, so every
        // read of them takes _positionLock.
        private readonly object _positionLock = new object();
        private Vector3 _machinePosition = new Vector3();
        private Vector3 _workOffset = new Vector3();
        private Vector3 _lastProbePosMachine;

        public Vector3 MachinePosition
        {
            get { lock (_positionLock) { return _machinePosition; } }
            private set { lock (_positionLock) { _machinePosition = value; } }
        }

        public Vector3 WorkOffset
        {
            get { lock (_positionLock) { return _workOffset; } }
            private set { lock (_positionLock) { _workOffset = value; } }
        }

        /// <summary>Machine position minus work offset, taken under one lock so the two
        /// cannot come from different status reports.</summary>
        public Vector3 WorkPosition
        {
            get { lock (_positionLock) { return _machinePosition - _workOffset; } }
        }

        public Vector3 LastProbePosMachine
        {
            get { lock (_positionLock) { return _lastProbePosMachine; } }
            private set { lock (_positionLock) { _lastProbePosMachine = value; } }
        }

        public int FeedOverride { get; private set; } = Constants.OverrideDefaultPercent;
        public int RapidOverride { get; private set; } = Constants.OverrideDefaultPercent;
        public int SpindleOverride { get; private set; } = Constants.OverrideDefaultPercent;

        public bool PinStateProbe { get; private set; } = false;
        public bool PinStateLimitX { get; private set; } = false;
        public bool PinStateLimitY { get; private set; } = false;
        public bool PinStateLimitZ { get; private set; } = false;

        /// <summary>
        /// Whether the machine has homed since it connected. Only MachineWait.HomeAsync
        /// sets it true; connecting, disconnecting and a soft reset each clear it.
        /// </summary>
        public bool IsHomed { get; set; } = false;

        /// <summary>
        /// True while MachineWait.HomeAsync is running. That method is the only writer, so
        /// homing by any other route leaves this false.
        /// </summary>
        public bool IsHoming { get; set; } = false;

        private long _statusReportCount;

        /// <summary>
        /// Monotonic count of status reports, so a caller can separate GRBL still
        /// answering from GRBL gone quiet without reading a clock.
        /// </summary>
        public long StatusReportCount => Interlocked.Read(ref _statusReportCount);

        public double FeedRateRealtime { get; private set; } = 0;
        public double SpindleSpeedRealtime { get; private set; } = 0;

        public double CurrentTLO { get; private set; } = 0;

        private Vector3 _g54Offset;
        private TaskCompletionSource<bool> _g54Waiter;

        /// <summary>
        /// The G54 offset as GRBL last reported it for $#, not the combined WCO in the
        /// status report (<see cref="WorkOffset"/> is G54 plus G92 plus tool length
        /// offset). A G10 L2 P1 write starts from this value, or the other two shift the
        /// origin.
        /// </summary>
        public Vector3 G54Offset
        {
            get { lock (_positionLock) { return _g54Offset; } }
            private set { lock (_positionLock) { _g54Offset = value; } }
        }

        private ReadOnlyCollection<bool> _pauselines = new ReadOnlyCollection<bool>(new bool[0]);
        public ReadOnlyCollection<bool> PauseLines
        {
            get { return _pauselines; }
            private set { _pauselines = value; }
        }

        private ReadOnlyCollection<string> _file = new ReadOnlyCollection<string>(new string[0]);
        public ReadOnlyCollection<string> File
        {
            get { return _file; }
            private set
            {
                _file = value;
                FilePosition = 0;
                RaiseEvent(FileChanged);
            }
        }

        private int _filePosition = 0;
        public int FilePosition
        {
            get { return _filePosition; }
            private set { _filePosition = value; }
        }

        private int _mode = (int)OperatingMode.Manual;

        /// <summary>
        /// Written by a controller and read from the request threads that refuse a jog
        /// mid-probe, so reads and writes here are atomic.
        /// </summary>
        public OperatingMode Mode
        {
            get { return (OperatingMode)Volatile.Read(ref _mode); }
            private set
            {
                if (Interlocked.Exchange(ref _mode, (int)value) == (int)value)
                {
                    return;
                }

                RaiseEvent(OperatingModeChanged);
            }
        }

        /// <summary>
        /// GRBL's state word and its substate, held as one value. Stored separately they
        /// tear across a write, and a reader landing between the two stores would see a
        /// pair the machine was never in: "Door" with substate "0" is the one pair that
        /// permits a cycle start.
        /// </summary>
        private sealed record StatusReading(string Word, string SubState);

        /// <inheritdoc cref="StatusReading"/>
        private StatusReading _state = new(StatusDisconnected, string.Empty);

        private long _lastStateClearAttemptMs;
        private const int StateClearIntervalMs = 500;

        /// <summary>
        /// When true, an alarm is cleared automatically while in Manual mode; a door hold
        /// never is, because only the operator may release it. Enable only in menus that
        /// display status (MainMenu, JogMenu).
        /// </summary>
        public bool EnableAutoStateClear { get; set; } = false;

        public string Status
        {
            get { return Volatile.Read(ref _state).Word; }
            private set { SetStatus(value, string.Empty); }
        }

        /// <inheritdoc/>
        public string StatusSubState => Volatile.Read(ref _state).SubState;

        /// <summary>
        /// Closing the door moves GRBL from Door:1 to Door:0 without changing the state
        /// word, so a comparison on the word alone would leave every screen showing an
        /// open door.
        /// </summary>
        private void SetStatus(string state, string subState)
        {
            var current = Volatile.Read(ref _state);
            if (current.Word == state && current.SubState == subState)
            {
                return;
            }

            Volatile.Write(ref _state, new StatusReading(state, subState));
            RaiseEvent(StatusChanged);
        }

        private ParseDistanceMode _distanceMode = ParseDistanceMode.Absolute;
        public ParseDistanceMode DistanceMode
        {
            get { return _distanceMode; }
            private set
            {
                if (_distanceMode == value)
                {
                    return;
                }
                _distanceMode = value;
                RaiseEvent(DistanceModeChanged);
            }
        }

        private ParseUnit _unit = ParseUnit.Metric;
        public ParseUnit Unit
        {
            get { return _unit; }
            private set
            {
                if (_unit == value)
                {
                    return;
                }
                _unit = value;
                RaiseEvent(UnitChanged);
            }
        }

        private ArcPlane _plane = ArcPlane.XY;
        public ArcPlane Plane
        {
            get { return _plane; }
            private set
            {
                if (_plane == value)
                {
                    return;
                }
                _plane = value;
                RaiseEvent(PlaneChanged);
            }
        }

        private bool _connected = false;
        public bool Connected
        {
            get { return _connected; }
            private set
            {
                if (value == _connected)
                {
                    return;
                }

                _connected = value;

                if (!Connected)
                {
                    // Back to the default mode, so a link that drops mid-stream does not
                    // leave the next connection still in SendFile.
                    Mode = OperatingMode.Manual;
                }

                RaiseEvent(ConnectionStateChanged);
            }
        }

        private int _bufferState;
        public int BufferState
        {
            get { return _bufferState; }
            private set
            {
                if (_bufferState == value)
                {
                    return;
                }

                _bufferState = value;
                RaiseEvent(BufferStateChanged);
            }
        }

        public bool SyncBuffer { get; set; }

        private Stream Connection;
        private Thread WorkerThread;
        private TcpClient ClientEthernet;
        private StreamWriter Log;
        private MachineSettings _settings;

        public MachineSettings Settings
        {
            get { return _settings; }
            set { _settings = value; }
        }

        private void RecordLog(string message)
        {
            // Snapshot: Disconnect closes and nulls this from another thread, and
            // writing to the closed writer would kill the serial worker.
            var log = Log;

            if (log == null)
            {
                return;
            }

            try
            {
                log.WriteLine(message);
            }
            catch (ObjectDisposedException)
            {
                // Raced with Disconnect. The traffic log is a diagnostic; losing a
                // line from it must never take the connection down.
            }
            catch (IOException)
            {
            }
        }

        public Machine(MachineSettings settings = null)
        {
            _settings = settings ?? new MachineSettings();
        }

        // ConcurrentQueue rather than Queue.Synchronized: the latter makes each call atomic
        // but not a Count/Peek-then-Dequeue sequence, so a Clear() from a UI or web thread
        // landing between them threw and killed the serial worker, dropping the connection
        // mid-cut.
        private readonly ConcurrentQueue<string> Sent = new();
        private readonly ConcurrentQueue<string> ToSend = new();
        private readonly ConcurrentQueue<char> ToSendPriority = new();

        // Guards the pairing of BufferState with Sent. They describe one thing - how
        // many bytes GRBL is holding - and must not be updated independently.
        private readonly object _bufferLock = new object();

        private void Work()
        {
            try
            {
                StreamReader reader = new StreamReader(Connection);
                StreamWriter writer = new StreamWriter(Connection);

                int StatusPollInterval = _settings.StatusPollInterval;
                int ControllerBufferSize = _settings.ControllerBufferSize;
                BufferState = 0;

                TimeSpan WaitTime = TimeSpan.FromMilliseconds(0.5);

                // Monotonic: a DST shift or NTP step must not stall status polling for
                // an hour, nor expire every deadline at once.
                var RunTime = System.Diagnostics.Stopwatch.StartNew();
                long LastStatusPollMs = Constants.FirstStatusPollDelayMs;
                long LastFilePosUpdateMs = 0;
                bool filePosChanged = false;

                void SendLineToGrbl(string line)
                {
                    // // Log every line sent with hex dump for debugging
                    // var bytes = System.Text.Encoding.UTF8.GetBytes(line);
                    // var hex = BitConverter.ToString(bytes).Replace("-", " ");
                    // Controllers.ControllerLog.Log($"GRBL_SEND[{Sent.Count}]: \"{line}\" len={line.Length} hex=[{hex}]");

                    writer.Write(line);
                    writer.Write('\n');
                    writer.Flush();

                    RecordLog("> " + line);

                    RaiseEvent(UpdateStatus, line);
                    RaiseEvent(LineSent, line);

                    lock (_bufferLock)
                    {
                        BufferState += line.Length + 1;
                        Sent.Enqueue(line);
                    }
                }

                writer.Write($"\n{CmdViewGCodeState}\n");
                writer.Write($"\n{CmdViewParameters}\n");
                writer.Flush();

                while (true)
                {
                    Task<string> lineTask = reader.ReadLineAsync();

                    while (!lineTask.IsCompleted)
                    {
                        if (!Connected)
                        {
                            return;
                        }

                        while (ToSendPriority.TryDequeue(out char priorityChar))
                        {
                            writer.Write(priorityChar);
                            writer.Flush();
                        }

                        if (Mode == OperatingMode.SendFile)
                        {
                            if (File.Count > FilePosition && (File[FilePosition].Length + 1) < (ControllerBufferSize - BufferState))
                            {
                                string sendLine = File[FilePosition];

                                // GRBL has no M6, so a tool change line never reaches it.
                                // Classified through GCodeParser so this and MillingController
                                // cannot disagree about what a tool change line is.
                                bool isM6Line = GCodeParser.IsM6Line(sendLine);

                                if (!isM6Line)
                                {
                                    SendLineToGrbl(sendLine);
                                }
                                else
                                {
                                    RecordLog($"> [M6 intercepted at FilePosition={FilePosition}, not sent to GRBL]");
                                    RecordLog($"> [M6] BufferState={BufferState}, Sent.Count={Sent.Count}, Status={Status}");
                                    RecordLog($"> [M6] WorkPos=({WorkPosition.X:F3}, {WorkPosition.Y:F3}, {WorkPosition.Z:F3})");
                                }

                                // PauseFileOnHold governs whether M0/M1/M2/M30 stop the stream.
                                // M6 pauses whichever way it is set, because carrying on would
                                // cut the rest of the job with the wrong tool.
                                if (PauseLines[FilePosition] && (isM6Line || _settings.PauseFileOnHold))
                                {
                                    RecordLog($"> [PAUSE triggered at FilePosition={FilePosition}, PauseLines[{FilePosition}]=true]");
                                    Mode = OperatingMode.Manual;
                                }

                                if (++FilePosition >= File.Count)
                                {
                                    Mode = OperatingMode.Manual;
                                }

                                filePosChanged = true;
                            }
                        }
                        else if (ToSend.TryPeek(out string pending)
                                 && (pending.Length + 1) < (ControllerBufferSize - BufferState)
                                 && ToSend.TryDequeue(out string sendLine))
                        {
                            SendLineToGrbl(sendLine);
                        }

                        long nowMs = RunTime.ElapsedMilliseconds;

                        if (nowMs - LastStatusPollMs > StatusPollInterval)
                        {
                            writer.Write(StatusQuery);
                            writer.Flush();
                            LastStatusPollMs = nowMs;
                        }

                        if (filePosChanged && nowMs - LastFilePosUpdateMs > Constants.FilePosUpdateIntervalMs)
                        {
                            RaiseEvent(FilePositionChanged);
                            LastFilePosUpdateMs = nowMs;
                            filePosChanged = false;
                        }

                        Thread.Sleep(WaitTime);
                    }

                    string line = lineTask.Result;

                    if (line == null)
                    {
                        RaiseEvent(Info, "Connection closed by remote end");
                        break;
                    }

                    RecordLog("< " + line);

                    // // Log all non-status responses
                    // if (!line.StartsWith("<"))
                    // {
                    //     Controllers.ControllerLog.Log($"GRBL_RECV: \"{line}\" Sent.Count={Sent.Count} BufferState={BufferState}");
                    // }

                    if (line == ResponseOk)
                    {
                        lock (_bufferLock)
                        {
                            if (Sent.TryDequeue(out string acked))
                            {
                                BufferState -= acked.Length + 1;
                            }
                            else
                            {
                                // The $G and $# sent at startup go straight to the port
                                // rather than the queue, so their ok arrives with Sent empty.
                                BufferState = 0;
                            }
                        }
                    }
                    else
                    {
                        if (line.StartsWith(ResponseErrorPrefix))
                        {
                            string errorline = null;

                            lock (_bufferLock)
                            {
                                if (Sent.TryDequeue(out errorline))
                                {
                                    BufferState -= errorline.Length + 1;
                                }
                                else
                                {
                                    BufferState = 0;
                                }
                            }

                            if (errorline != null)
                            {
                                Controllers.ControllerLog.Log(
                                    "GRBL rejected {0}: {1}", errorline, line);

                                RaiseEvent(ReportError, $"{line}: {errorline}");

                                CommandRejected?.Invoke(new GrblRejection(
                                    ParseErrorCode(line),
                                    errorline,
                                    GrblCodeTranslator.ExpandError(line)));
                            }
                            else
                            {
                                if (RunTime.ElapsedMilliseconds > Constants.ErrorGracePeriodMs)
                                {
                                    RaiseEvent(ReportError, $"Received <{line}> without anything in the Sent Buffer");
                                }
                            }

                            Mode = OperatingMode.Manual;
                        }
                        else if (line.StartsWith("<"))
                        {
                            RaiseEvent(ParseStatus, line);
                        }
                        else if (line.StartsWith(ResponseProbePrefix))
                        {
                            RaiseEvent(ParseProbe, line);
                            RaiseEvent(LineReceived, line);
                        }
                        else if (line.StartsWith("["))
                        {
                            RaiseEvent(UpdateStatus, line);
                            RaiseEvent(LineReceived, line);
                        }
                        else if (line.StartsWith(ResponseAlarmPrefix))
                        {
                            // An alarm ends whatever was running, and the controller reports
                            // only that the machine stopped moving. Without this line the log
                            // does not record which alarm fired.
                            Controllers.ControllerLog.Log("GRBL alarm: {0}", line);
                            RaiseEvent(ReportError, line);
                            Mode = OperatingMode.Manual;
                            ToSend.Clear();
                        }
                        else if (line.StartsWith(ResponseGrblPrefix))
                        {
                            RaiseEvent(LineReceived, line);
                            RaiseEvent(ParseStartup, line);
                        }
                        else if (line.Length > 0)
                        {
                            RaiseEvent(LineReceived, line);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                RaiseEvent(ReportError, $"Fatal Error in Work Loop: {ex.Message}");
            }
            finally
            {
                // This runs outside the catch above, on a foreground thread. An escape
                // here would terminate the process while GRBL still holds buffered
                // motion, so nothing is allowed out.
                try
                {
                    if (Connected)
                    {
                        RaiseEvent(() => Disconnect());
                    }
                }
                catch (Exception ex)
                {
                    Controllers.ControllerLog.Log("Disconnect during work-loop teardown failed: {0}", ex.Message);
                }
            }
        }

        public void Connect()
        {
            if (Connected)
            {
                throw new Exception("Already connected. Close the existing connection first.");
            }

            switch (_settings.ConnectionType)
            {
                case ConnectionType.Serial:
                    try
                    {
                        SerialPort port = new SerialPort(_settings.SerialPortName, _settings.SerialPortBaud);
                        port.DtrEnable = _settings.SerialPortDTR;
                        port.Open();
                        Connection = port.BaseStream;
                        Connected = true;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        RaiseEvent(NonFatalException, $"Cannot access {_settings.SerialPortName} - serial port is in use by another connection. Close the existing connection first.");
                    }
                    catch (IOException ex)
                    {
                        RaiseEvent(NonFatalException, $"Cannot open {_settings.SerialPortName}: {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        RaiseEvent(NonFatalException, $"Serial connection failed: {ex.Message}");
                    }
                    break;
                case ConnectionType.Ethernet:
                    try
                    {
                        RaiseEvent(Info, "Connecting to " + _settings.EthernetIP + ":" + _settings.EthernetPort);
                        ClientEthernet = new TcpClient(_settings.EthernetIP, _settings.EthernetPort);
                        Connected = true;
                        RaiseEvent(Info, "Successful Connection");
                        Connection = ClientEthernet.GetStream();
                    }
                    catch (ArgumentNullException)
                    {
                        RaiseEvent(NonFatalException, "Invalid address or port");
                    }
                    catch (SocketException)
                    {
                        RaiseEvent(NonFatalException, "Connection failure");
                    }
                    break;
                default:
                    throw new Exception("Invalid Connection Type");
            }

            if (!Connected)
            {
                return;
            }

            if (_settings.LogTraffic)
            {
                try
                {
                    Log = new StreamWriter(Constants.SerialTrafficLogFile);
                }
                catch (Exception e)
                {
                    NonFatalException?.Invoke("could not open logfile: " + e.Message);
                }
            }

            ToSend.Clear();
            ToSendPriority.Clear();
            Sent.Clear();
            
            // Carrying a stale IsHomed across a reconnect would let milling skip homing
            // and run every G53 move against a coordinate system that no longer exists.
            IsHomed = false;
            IsHoming = false;

            Mode = OperatingMode.Manual;

            PositionUpdateReceived?.Invoke();

            WorkerThread = new Thread(Work);
            WorkerThread.Priority = ThreadPriority.AboveNormal;
            WorkerThread.Start();
        }

        /// <summary>
        /// True while GRBL may still have work queued, so the machine is stopped before the
        /// port closes. Idle alone is not enough: a line just sent sits unparsed in GRBL's
        /// receive buffer while the status still reads Idle.
        /// </summary>
        internal static bool NeedsStopBeforeDisconnect(bool connected, string status, int bytesSent) =>
            connected
            && status != GrblProtocol.StatusDisconnected
            && (status != GrblProtocol.StatusIdle || bytesSent > 0);

        /// <summary>
        /// GRBL keeps working through its planner buffer after the port closes, so it is
        /// stopped first. Written straight to the connection rather than queued, because
        /// Connected is already false and the send queue is no longer drained.
        /// </summary>
        private void SendStop()
        {
            try
            {
                if (Connection != null)
                {
                    WriteStopSequence(Connection);
                }
            }
            catch (Exception ex)
            {
                Controllers.ControllerLog.Log("Stop before disconnect failed: {0}", ex.Message);
            }
        }

        /// <summary>
        /// Feed hold, then soft reset, with GRBL given time to act on each. The hold decelerates
        /// the move to a stop; the reset ends the job and stops the spindle.
        /// </summary>
        internal static void WriteStopSequence(Stream connection)
        {
            connection.WriteByte((byte)GrblProtocol.FeedHold);
            connection.Flush();
            Thread.Sleep(Constants.CommandDelayMs);
            connection.WriteByte((byte)GrblProtocol.SoftReset);
            connection.Flush();
            Thread.Sleep(Constants.ResetWaitMs);
        }

        public void Disconnect()
        {
            // Read before Connected is set false and the worker exits, so Status and BufferState
            // still describe the running machine. Acted on below, once nothing is streaming lines.
            bool stopFirst = NeedsStopBeforeDisconnect(Connected, Status, BufferState);

            if (Log != null)
            {
                Log.Close();
            }
            Log = null;

            Connected = false;

            // Joining from the worker thread itself would deadlock.
            if (WorkerThread != null && WorkerThread != Thread.CurrentThread)
            {
                WorkerThread.Join();
            }

            if (stopFirst)
            {
                SendStop();
            }

            switch (_settings.ConnectionType)
            {
                case ConnectionType.Serial:
                    try
                    {
                        Connection?.Close();
                    }
                    catch
                    {
                    }
                    Connection?.Dispose();
                    Connection = null;
                    break;
                case ConnectionType.Ethernet:
                    try
                    {
                        Connection?.Close();
                        ClientEthernet?.Close();
                    }
                    catch
                    {
                    }
                    Connection = null;
                    ClientEthernet = null;
                    break;
                default:
                    throw new Exception("Invalid Connection Type");
            }

            IsHomed = false;
            IsHoming = false;

            MachinePosition = new Vector3();
            WorkOffset = new Vector3();
            G54Offset = new Vector3();
            FeedRateRealtime = 0;
            CurrentTLO = 0;

            PositionUpdateReceived?.Invoke();

            Status = StatusDisconnected;
            DistanceMode = ParseDistanceMode.Absolute;
            Unit = ParseUnit.Metric;
            Plane = ArcPlane.XY;

            FeedOverride = Constants.OverrideDefaultPercent;
            RapidOverride = Constants.OverrideDefaultPercent;
            SpindleOverride = Constants.OverrideDefaultPercent;

            OverrideChanged?.Invoke();

            PinStateLimitX = false;
            PinStateLimitY = false;
            PinStateLimitZ = false;
            PinStateProbe = false;

            PinStateChanged?.Invoke();

            ToSend.Clear();
            ToSendPriority.Clear();

            lock (_bufferLock)
            {
                Sent.Clear();
                BufferState = 0;
            }
        }

        /// <summary>
        /// Clears a Probe mode left behind by the previous operation, which would otherwise
        /// stop the next file from streaming.
        /// </summary>
        public void EnsureManualMode()
        {
            if (Mode == OperatingMode.Probe)
            {
                Mode = OperatingMode.Manual;
            }
        }

        public void SendLine(string line)
        {
            if (!Connected)
            {
                RaiseEvent(Info, "Not Connected");
                return;
            }

            if (Mode != OperatingMode.Manual && Mode != OperatingMode.Probe)
            {
                RaiseEvent(Info, "Not in Manual Mode");
                return;
            }

            // One call must put exactly one line on the wire. An embedded newline or a
            // GRBL real-time byte reaching here would let anything that builds a command
            // from user input append commands of its own.
            if (ContainsControlCharacter(line))
            {
                RaiseEvent(NonFatalException, "Refused a command containing control characters.");
                return;
            }

            ToSend.Enqueue(line);
        }

        /// <summary>
        /// True if the line holds anything that would break it into more than one
        /// command, or that GRBL would read as a real-time instruction.
        /// </summary>
        public static bool ContainsControlCharacter(string line)
        {
            foreach (char c in line)
            {
                // C0 controls and DEL split the line or are protocol bytes. Tab is
                // legal G-code whitespace, so it is allowed through.
                if ((c < ' ' && c != '\t') || c == (char)0x7F)
                {
                    return true;
                }

                // GRBL's extended real-time commands live at 0x80-0xA0: jog cancel,
                // door, feed and spindle overrides. They act immediately, ahead of
                // anything queued, so they must never ride inside a command.
                if (c >= (char)0x80 && c <= (char)0xA0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// <see cref="G54Offset"/> is otherwise only as fresh as the last $#, which in the
        /// usual connect-probe-zero-mill sequence predates the operator setting their Z
        /// zero. A caller writing G54 back has to start from a current value, or it moves
        /// the origin instead of restoring it.
        /// </summary>
        /// <returns>False if GRBL did not answer in time; the caller must not rely on
        /// <see cref="G54Offset"/> in that case.</returns>
        public async Task<bool> RefreshWorkOffsetsAsync(int timeoutMs, CancellationToken ct = default)
        {
            if (!Connected)
            {
                return false;
            }

            var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _g54Waiter, waiter);

            SendLine(CmdViewParameters);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var completed = await Task.WhenAny(waiter.Task, Task.Delay(timeoutMs, timeout.Token)).ConfigureAwait(false);
            timeout.Cancel();

            if (completed != waiter.Task)
            {
                Interlocked.CompareExchange(ref _g54Waiter, null, waiter);
                return false;
            }

            return true;
        }

        public void SoftReset()
        {
            if (!Connected)
            {
                RaiseEvent(Info, "Not Connected");
                return;
            }

            Mode = OperatingMode.Manual;

            // A soft reset while the machine is moving loses the position GRBL was
            // tracking, so the homed origin no longer holds. Clearing IsHomed makes the
            // next job home again rather than run its G53 safety moves against a stale
            // reference.
            IsHomed = false;

            ToSend.Clear();
            ToSendPriority.Clear();

            // Cleared together with the byte count they describe, or the worker's
            // read-modify-write of BufferState races this to a negative value. A negative
            // count makes the send gate more permissive, which overruns GRBL's receive
            // buffer and mangles a line mid-cut.
            lock (_bufferLock)
            {
                Sent.Clear();
                BufferState = 0;
            }

            ToSendPriority.Enqueue(GrblProtocol.SoftReset);

            FeedOverride = Constants.OverrideDefaultPercent;
            RapidOverride = Constants.OverrideDefaultPercent;
            SpindleOverride = Constants.OverrideDefaultPercent;

            OverrideChanged?.Invoke();

            // No $G or $# here: either one queues ahead of the soft reset finishing and
            // comes back as a parse error. Connect sends both, after a wait.
        }

        public void SendControl(byte controlchar)
        {
            if (!Connected)
            {
                RaiseEvent(Info, "Not Connected");
                return;
            }

            ToSendPriority.Enqueue((char)controlchar);
        }

        public void FeedHold()
        {
            if (!Connected)
            {
                RaiseEvent(Info, "Not Connected");
                return;
            }

            ToSendPriority.Enqueue(GrblProtocol.FeedHold);
        }

        public void CycleStart()
        {
            if (!Connected)
            {
                RaiseEvent(Info, "Not Connected");
                return;
            }

            ToSendPriority.Enqueue(GrblProtocol.CycleStart);
        }

        public void JogCancel()
        {
            if (!Connected)
            {
                RaiseEvent(Info, "Not Connected");
                return;
            }

            ToSendPriority.Enqueue(GrblProtocol.JogCancel);
        }

        public void FeedOverrideReset()
        {
            if (!Connected)
            {
                return;
            }

            ToSendPriority.Enqueue(GrblProtocol.FeedOverrideReset);
        }

        public void FeedOverrideIncrease()
        {
            if (!Connected)
            {
                return;
            }

            ToSendPriority.Enqueue(GrblProtocol.FeedOverrideIncrease10);
        }

        public void FeedOverrideDecrease()
        {
            if (!Connected)
            {
                return;
            }

            ToSendPriority.Enqueue(GrblProtocol.FeedOverrideDecrease10);
        }

        public void Jog(char axis, double distance, double feed)
        {
            if (!Connected)
            {
                RaiseEvent(Info, "Not Connected");
                return;
            }

            string cmd = string.Format(Constants.DecimalOutputFormat, JogPrefix + "G91F{0:0.#}{1}{2:0.###}", feed, axis, distance);
            SendLine(cmd);
        }

        public void SetFile(IList<string> file)
        {
            if (Mode == OperatingMode.SendFile)
            {
                RaiseEvent(Info, "Can't change file while active");
                return;
            }

            bool[] pauselines = new bool[file.Count];

            for (int line = 0; line < file.Count; line++)
            {
                pauselines[line] = GCodeParser.ClassifyPauseLine(file[line]) != PauseMCode.None;
            }

            File = new ReadOnlyCollection<string>(file);
            PauseLines = new ReadOnlyCollection<bool>(pauselines);

            FilePosition = 0;

            RaiseEvent(FilePositionChanged);
        }

        public void ClearFile()
        {
            if (Mode == OperatingMode.SendFile)
            {
                RaiseEvent(Info, "Can't change file while active");
                return;
            }

            File = new ReadOnlyCollection<string>(new string[0]);
            FilePosition = 0;
            RaiseEvent(FilePositionChanged);
        }

        /// <summary>
        /// Begins streaming the loaded file. Returns false if it could not start - not
        /// connected, or not in Manual mode - so the caller can react instead of waiting
        /// for a stream that never began.
        /// </summary>
        public bool FileStart()
        {
            if (!Connected)
            {
                RaiseEvent(Info, "Not Connected");
                return false;
            }

            if (Mode != OperatingMode.Manual)
            {
                RaiseEvent(Info, "Not in Manual Mode");
                return false;
            }

            // // Log machine state and first 20 lines of file for debugging
            // Controllers.ControllerLog.Log($"FILE_START: Mode={Mode}, Status={Status}, FilePosition={FilePosition}, File.Count={File.Count}");
            // Controllers.ControllerLog.Log($"FILE_START: BufferState={BufferState}, ToSend.Count={ToSend.Count}, Sent.Count={Sent.Count}");
            // Controllers.ControllerLog.Log($"FILE_START: WorkPos=({WorkPosition.X:F3}, {WorkPosition.Y:F3}, {WorkPosition.Z:F3})");
            // Controllers.ControllerLog.Log($"FILE_START: MachinePos=({MachinePosition.X:F3}, {MachinePosition.Y:F3}, {MachinePosition.Z:F3})");
            // for (int i = 0; i < Math.Min(20, File.Count); i++)
            // {
            //     var line = File[i];
            //     var bytes = System.Text.Encoding.UTF8.GetBytes(line);
            //     var hex = BitConverter.ToString(bytes).Replace("-", " ");
            //     Controllers.ControllerLog.Log($"FILE[{i}]: \"{line}\" len={line.Length} hex=[{hex}]");
            // }

            Mode = OperatingMode.SendFile;
            return true;
        }

        public void FilePause()
        {
            if (!Connected)
            {
                RaiseEvent(Info, "Not Connected");
                return;
            }

            if (Mode != OperatingMode.SendFile)
            {
                RaiseEvent(Info, "Not in SendFile Mode");
                return;
            }

            Mode = OperatingMode.Manual;
        }

        /// <inheritdoc/>
        public bool ProbeStart()
        {
            if (!Connected)
            {
                RaiseEvent(Info, "Not Connected");
                return false;
            }

            // Compared and set in one step, so two taps of Probe Z cannot both find Manual
            // and both send a G38.2.
            if (Interlocked.CompareExchange(
                    ref _mode, (int)OperatingMode.Probe, (int)OperatingMode.Manual)
                != (int)OperatingMode.Manual)
            {
                RaiseEvent(Info, "Can't start probing while running!");
                return false;
            }

            RaiseEvent(OperatingModeChanged);
            return true;
        }

        public void ProbeStop()
        {
            if (!Connected)
            {
                RaiseEvent(Info, "Not Connected");
                return;
            }

            if (Mode != OperatingMode.Probe)
            {
                RaiseEvent(Info, "Not in Probe mode");
                return;
            }

            Mode = OperatingMode.Manual;
        }

        public void FileGoto(int lineNumber)
        {
            if (Mode == OperatingMode.SendFile)
            {
                return;
            }

            if (lineNumber >= File.Count || lineNumber < 0)
            {
                RaiseEvent(NonFatalException, "Line Number outside of file length");
                return;
            }

            FilePosition = lineNumber;
            RaiseEvent(FilePositionChanged);
        }

        public void ClearQueue()
        {
            if (Mode != OperatingMode.Manual)
            {
                RaiseEvent(Info, "Not in Manual mode");
                return;
            }

            ToSend.Clear();
        }

        private void UpdateStatus(string line)
        {
            if (!Connected)
            {
                return;
            }

            if (line.Contains("$J="))
            {
                return;
            }

            if (line.StartsWith(ResponseG54Prefix))
            {
                try
                {
                    G54Offset = Vector3.Parse(TrimToThreeAxes(
                        line.Substring(ResponseG54Prefix.Length).TrimEnd(']')));
                    Interlocked.Exchange(ref _g54Waiter, null)?.TrySetResult(true);
                }
                catch
                {
                    RaiseEvent(NonFatalException, "Error while Parsing Status Message");
                }
                return;
            }

            if (line.StartsWith(ResponseTloPrefix))
            {
                try
                {
                    CurrentTLO = double.Parse(line.Substring(5, line.Length - 6), Constants.DecimalParseFormat);
                    RaiseEvent(PositionUpdateReceived);
                }
                catch
                {
                    RaiseEvent(NonFatalException, "Error while Parsing Status Message");
                }
                return;
            }

            try
            {
                MatchCollection mc = GCodeSplitter.Matches(line);
                for (int i = 0; i < mc.Count; i++)
                {
                    Match m = mc[i];

                    if (m.Groups[1].Value != "G")
                    {
                        continue;
                    }

                    double code = double.Parse(m.Groups[2].Value, Constants.DecimalParseFormat);

                    if (code == PlaneXY)
                    {
                        Plane = ArcPlane.XY;
                    }
                    if (code == PlaneYZ)
                    {
                        Plane = ArcPlane.YZ;
                    }
                    if (code == PlaneZX)
                    {
                        Plane = ArcPlane.ZX;
                    }

                    if (code == UnitsInches)
                    {
                        Unit = ParseUnit.Imperial;
                    }
                    if (code == UnitsMillimeters)
                    {
                        Unit = ParseUnit.Metric;
                    }

                    if (code == DistanceAbsolute)
                    {
                        DistanceMode = ParseDistanceMode.Absolute;
                    }
                    if (code == DistanceIncremental)
                    {
                        DistanceMode = ParseDistanceMode.Incremental;
                    }

                    if (code == ToolLengthOffsetCancel)
                    {
                        CurrentTLO = 0;
                    }

                    if (code == ToolLengthOffsetDynamic)
                    {
                        if (mc.Count > (i + 1))
                        {
                            if (mc[i + 1].Groups[1].Value == "Z")
                            {
                                CurrentTLO = double.Parse(mc[i + 1].Groups[2].Value, Constants.DecimalParseFormat);
                                RaiseEvent(PositionUpdateReceived);
                            }
                            i += 1;
                        }
                    }
                }
            }
            catch
            {
                RaiseEvent(NonFatalException, "Error while Parsing Status Message");
            }
        }

        private void ParseStatus(string line)
        {
            MatchCollection statusMatch = StatusEx.Matches(line);

            if (statusMatch.Count == 0)
            {
                ReportBadStatus(line);
                return;
            }

            Interlocked.Increment(ref _statusReportCount);

            bool posUpdate = false;
            bool overrideUpdate = false;
            bool pinStateUpdate = false;
            bool resetPins = true;

            foreach (Match m in statusMatch)
            {
                if (m.Index == 1)
                {
                    SetStatus(m.Groups[1].Value, m.Groups[2].Value);
                    continue;
                }

                if (m.Groups[1].Value == FieldOverride)
                {
                    try
                    {
                        string[] parts = m.Groups[2].Value.Split(',');
                        FeedOverride = int.Parse(parts[0]);
                        RapidOverride = int.Parse(parts[1]);
                        SpindleOverride = int.Parse(parts[2]);
                        overrideUpdate = true;
                    }
                    catch { NonFatalException?.Invoke(string.Format("Received Bad Status: '{0}'", line)); }
                }
                else if (m.Groups[1].Value == FieldWorkCoordOffset)
                {
                    try
                    {
                        string OffsetString = m.Groups[2].Value;

                        if (_settings.IgnoreAdditionalAxes)
                        {
                            string[] parts = OffsetString.Split(',');
                            if (parts.Length > 3)
                            {
                                Array.Resize(ref parts, 3);
                                OffsetString = string.Join(",", parts);
                            }
                        }

                        WorkOffset = Vector3.Parse(OffsetString);
                        posUpdate = true;
                    }
                    catch { NonFatalException?.Invoke(string.Format("Received Bad Status: '{0}'", line)); }
                }
                else if (SyncBuffer && m.Groups[1].Value == FieldBuffer)
                {
                    try
                    {
                        int availableBytes = int.Parse(m.Groups[2].Value.Split(',')[1]);
                        int used = _settings.ControllerBufferSize - availableBytes;

                        if (used < 0)
                        {
                            used = 0;
                        }

                        lock (_bufferLock)
                        {
                            BufferState = used;
                        }
                        RaiseEvent(Info, $"Buffer State Synced ({availableBytes} bytes free)");
                    }
                    catch { NonFatalException?.Invoke(string.Format("Received Bad Status: '{0}'", line)); }
                }
                else if (m.Groups[1].Value == FieldPins)
                {
                    resetPins = false;

                    string states = m.Groups[2].Value;

                    bool stateX = states.Contains("X");
                    if (stateX != PinStateLimitX)
                    {
                        pinStateUpdate = true;
                    }
                    PinStateLimitX = stateX;

                    bool stateY = states.Contains("Y");
                    if (stateY != PinStateLimitY)
                    {
                        pinStateUpdate = true;
                    }
                    PinStateLimitY = stateY;

                    bool stateZ = states.Contains("Z");
                    if (stateZ != PinStateLimitZ)
                    {
                        pinStateUpdate = true;
                    }
                    PinStateLimitZ = stateZ;

                    bool stateP = states.Contains("P");
                    if (stateP != PinStateProbe)
                    {
                        pinStateUpdate = true;
                    }
                    PinStateProbe = stateP;
                }
                else if (m.Groups[1].Value == FieldFeed)
                {
                    try
                    {
                        FeedRateRealtime = double.Parse(m.Groups[2].Value, Constants.DecimalParseFormat);
                        posUpdate = true;
                    }
                    catch { NonFatalException?.Invoke(string.Format("Received Bad Status: '{0}'", line)); }
                }
                else if (m.Groups[1].Value == FieldFeedSpindle)
                {
                    try
                    {
                        string[] parts = m.Groups[2].Value.Split(',');
                        FeedRateRealtime = double.Parse(parts[0], Constants.DecimalParseFormat);
                        SpindleSpeedRealtime = double.Parse(parts[1], Constants.DecimalParseFormat);
                        posUpdate = true;
                    }
                    catch { NonFatalException?.Invoke(string.Format("Received Bad Status: '{0}'", line)); }
                }
            }

            SyncBuffer = false;

            Vector3 NewMachinePosition = MachinePosition;

            foreach (Match m in statusMatch)
            {
                if (m.Groups[1].Value == FieldMachinePos || m.Groups[1].Value == FieldWorkPos)
                {
                    try
                    {
                        string PositionString = TrimToThreeAxes(m.Groups[2].Value);

                        NewMachinePosition = Vector3.Parse(PositionString);

                        if (m.Groups[1].Value == FieldWorkPos)
                        {
                            NewMachinePosition += WorkOffset;
                        }

                        if (NewMachinePosition != MachinePosition)
                        {
                            posUpdate = true;
                            MachinePosition = NewMachinePosition;
                        }
                    }
                    catch { NonFatalException?.Invoke(string.Format("Received Bad Status: '{0}'", line)); }
                }
            }

            if (posUpdate && Connected)
            {
                PositionUpdateReceived?.Invoke();
            }

            if (overrideUpdate && Connected)
            {
                OverrideChanged?.Invoke();
            }

            if (resetPins)
            {
                pinStateUpdate = PinStateLimitX | PinStateLimitY | PinStateLimitZ | PinStateProbe;

                PinStateLimitX = false;
                PinStateLimitY = false;
                PinStateLimitZ = false;
                PinStateProbe = false;
            }

            if (pinStateUpdate && Connected)
            {
                PinStateChanged?.Invoke();
            }

            if (Connected)
            {
                StatusReceived?.Invoke(line);
            }

            // Door is never cleared here. GRBL reports Door when the safety interlock
            // opens, and a CycleStart there restarts the spindle and resumes motion while
            // the operator may be reaching into the enclosure.
            if (Mode == OperatingMode.Manual && EnableAutoStateClear)
            {
                long now = Environment.TickCount64;
                if (now - _lastStateClearAttemptMs > StateClearIntervalMs)
                {
                    if (Status.StartsWith(StatusAlarm))
                    {
                        _lastStateClearAttemptMs = now;
                        ToSend.Enqueue(CmdUnlock);
                    }
                }
            }
        }

        private void ParseProbe(string line)
        {
            var probeFinished = ProbeFinished;

            if (probeFinished == null)
            {
                return;
            }

            Match probeMatch = ProbeEx.Match(line);

            Group pos = probeMatch.Groups["Pos"];
            Group success = probeMatch.Groups["Success"];

            if (!probeMatch.Success || !(pos.Success & success.Success))
            {
                NonFatalException?.Invoke($"Received Bad Probe: '{line}'");
                return;
            }

            Vector3 ProbePos = Vector3.Parse(TrimToThreeAxes(pos.Value));
            LastProbePosMachine = ProbePos;

            ProbePos -= WorkOffset;
            ProbePos.X += _settings.ProbeOffsetX;
            ProbePos.Y += _settings.ProbeOffsetY;

            bool ProbeSuccess = success.Value == "1";

            probeFinished.Invoke(ProbePos, ProbeSuccess);
        }

        private void ParseStartup(string line)
        {
            Match m = StartupRegex.Match(line);

            int major, minor;
            char rev;

            if (!m.Success ||
                !int.TryParse(m.Groups[1].Value, out major) ||
                !int.TryParse(m.Groups[2].Value, out minor) ||
                !char.TryParse(m.Groups[3].Value, out rev))
            {
                RaiseEvent(Info, "Could not parse startup message.");
                return;
            }

            Version v = new Version(major, minor, (int)rev);
            if (v < Constants.MinimumGrblVersion)
            {
                ReportError("Outdated version of grbl detected!");
                ReportError($"Please upgrade to at least grbl v{Constants.MinimumGrblVersion.Major}.{Constants.MinimumGrblVersion.Minor}{(char)Constants.MinimumGrblVersion.Build}");
            }
        }

        /// <summary>Reads N out of "error:N", or -1 if the reply is not shaped that way.</summary>
        private static int ParseErrorCode(string line)
        {
            if (!line.StartsWith(ResponseErrorPrefix))
            {
                return -1;
            }

            return int.TryParse(line.Substring(ResponseErrorPrefix.Length).Trim(), out int code)
                ? code
                : -1;
        }

        private void ReportError(string error)
        {
            NonFatalException?.Invoke(GrblCodeTranslator.ExpandError(error));
        }

        private void ReportBadStatus(string line)
        {
            NonFatalException?.Invoke($"Received Bad Status: '{line}'");
        }

        private string TrimToThreeAxes(string positionString)
        {
            if (_settings.IgnoreAdditionalAxes)
            {
                string[] parts = positionString.Split(',');
                if (parts.Length > 3)
                {
                    Array.Resize(ref parts, 3);
                    return string.Join(",", parts);
                }
            }
            return positionString;
        }

        private void RaiseEvent(Action<string> action, string param)
        {
            action?.Invoke(param);
        }

        private void RaiseEvent(Action action)
        {
            action?.Invoke();
        }
    }
}
