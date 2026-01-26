using coppercli.Core.GCode;
using coppercli.Core.GCode.GCodeCommands;
using coppercli.Core.Settings;
using coppercli.Core.Util;
using static coppercli.Core.Util.GrblProtocol;
using static coppercli.Core.GCode.GCodeNumbers;
using System;
using System.Collections;
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
    public class Machine
    {
        public enum OperatingMode
        {
            Manual,
            SendFile,
            Probe,
            Disconnected,
            SendMacro
        }

        public event Action<Vector3, bool> ProbeFinished;
        public event Action<string> NonFatalException;
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

        // =========================================================================
        // Compiled regex patterns for GRBL response parsing
        // =========================================================================
        private static readonly Regex GCodeSplitter = new Regex(@"([GZ])\s*(\-?\d+\.?\d*)", RegexOptions.Compiled);
        private static readonly Regex StatusEx = new Regex(@"(?<=[<|])(\w+):?([^|>]*)?(?=[|>])", RegexOptions.Compiled);
        private static readonly Regex ProbeEx = new Regex(@"\[PRB:(?'Pos'\-?[0-9\.]*(?:,\-?[0-9\.]*)+):(?'Success'0|1)\]", RegexOptions.Compiled);
        private static readonly Regex StartupRegex = new Regex("grbl v([0-9])\\.([0-9])([a-z])", RegexOptions.Compiled);

        public Vector3 MachinePosition { get; private set; } = new Vector3();
        public Vector3 WorkOffset { get; private set; } = new Vector3();
        public Vector3 WorkPosition { get { return MachinePosition - WorkOffset; } }

        public Vector3 LastProbePosMachine { get; private set; }
        public Vector3 LastProbePosWork { get; private set; }

        public int FeedOverride { get; private set; } = 100;
        public int RapidOverride { get; private set; } = 100;
        public int SpindleOverride { get; private set; } = 100;

        public bool PinStateProbe { get; private set; } = false;
        public bool PinStateLimitX { get; private set; } = false;
        public bool PinStateLimitY { get; private set; } = false;
        public bool PinStateLimitZ { get; private set; } = false;

        public double FeedRateRealtime { get; private set; } = 0;
        public double SpindleSpeedRealtime { get; private set; } = 0;

        public double CurrentTLO { get; private set; } = 0;

        private Calculator _calculator;
        public Calculator Calculator { get { return _calculator; } }

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

        private OperatingMode _mode = OperatingMode.Disconnected;
        public OperatingMode Mode
        {
            get { return _mode; }
            private set
            {
                if (_mode == value)
                {
                    return;
                }

                _mode = value;
                RaiseEvent(OperatingModeChanged);
            }
        }

        private string _status = "Disconnected";
        public string Status
        {
            get { return _status; }
            private set
            {
                if (_status == value)
                {
                    return;
                }
                _status = value;
                RaiseEvent(StatusChanged);
            }
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
                    Mode = OperatingMode.Disconnected;
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
            if (Log != null)
            {
                try
                {
                    Log.WriteLine(message);
                }
                catch { throw; }
            }
        }

        public Machine(MachineSettings settings = null)
        {
            _settings = settings ?? new MachineSettings();
            _calculator = new Calculator(this);
        }

        Queue Sent = Queue.Synchronized(new Queue());
        Queue ToSend = Queue.Synchronized(new Queue());
        Queue ToSendPriority = Queue.Synchronized(new Queue());
        Queue ToSendMacro = Queue.Synchronized(new Queue());

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
                DateTime LastStatusPoll = DateTime.Now + TimeSpan.FromSeconds(0.5);
                DateTime StartTime = DateTime.Now;

                DateTime LastFilePosUpdate = DateTime.Now;
                bool filePosChanged = false;

                bool SendMacroStatusReceived = false;

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

                        while (ToSendPriority.Count > 0)
                        {
                            writer.Write((char)ToSendPriority.Dequeue());
                            writer.Flush();
                        }

                        if (Mode == OperatingMode.SendFile)
                        {
                            if (File.Count > FilePosition && (File[FilePosition].Length + 1) < (ControllerBufferSize - BufferState))
                            {
                                string sendLine = File[FilePosition].Replace(" ", "");

                                writer.Write(sendLine);
                                writer.Write('\n');
                                writer.Flush();

                                RecordLog("> " + sendLine);

                                RaiseEvent(UpdateStatus, sendLine);
                                RaiseEvent(LineSent, sendLine);

                                BufferState += sendLine.Length + 1;

                                Sent.Enqueue(sendLine);

                                if (PauseLines[FilePosition] && _settings.PauseFileOnHold)
                                {
                                    Mode = OperatingMode.Manual;
                                }

                                if (++FilePosition >= File.Count)
                                {
                                    Mode = OperatingMode.Manual;
                                }

                                filePosChanged = true;
                            }
                        }
                        else if (Mode == OperatingMode.SendMacro)
                        {
                            switch (Status)
                            {
                                case StatusIdle:
                                    if (BufferState == 0 && SendMacroStatusReceived)
                                    {
                                        SendMacroStatusReceived = false;

                                        string sendLine = (string)ToSendMacro.Dequeue();

                                        sendLine = Calculator.Evaluate(sendLine, out bool success);

                                        if (!success)
                                        {
                                            ReportError("Error while evaluating macro!");
                                            ReportError(sendLine);

                                            ToSendMacro.Clear();
                                        }
                                        else
                                        {
                                            sendLine = sendLine.Replace(" ", "");

                                            writer.Write(sendLine);
                                            writer.Write('\n');
                                            writer.Flush();

                                            RecordLog("> " + sendLine);

                                            RaiseEvent(UpdateStatus, sendLine);
                                            RaiseEvent(LineSent, sendLine);

                                            BufferState += sendLine.Length + 1;

                                            Sent.Enqueue(sendLine);
                                        }
                                    }
                                    break;
                                case StatusRun:
                                case StatusHold:
                                    break;
                                default:
                                    ToSendMacro.Clear();
                                    break;
                            }

                            if (ToSendMacro.Count == 0)
                            {
                                Mode = OperatingMode.Manual;
                            }
                        }
                        else if (ToSend.Count > 0 && (((string)ToSend.Peek()).Length + 1) < (ControllerBufferSize - BufferState))
                        {
                            string sendLine = ((string)ToSend.Dequeue()).Replace(" ", "");

                            writer.Write(sendLine);
                            writer.Write('\n');
                            writer.Flush();

                            RecordLog("> " + sendLine);

                            RaiseEvent(UpdateStatus, sendLine);
                            RaiseEvent(LineSent, sendLine);

                            BufferState += sendLine.Length + 1;

                            Sent.Enqueue(sendLine);
                        }

                        DateTime Now = DateTime.Now;

                        if ((Now - LastStatusPoll).TotalMilliseconds > StatusPollInterval)
                        {
                            writer.Write(StatusQuery);
                            writer.Flush();
                            LastStatusPoll = Now;
                        }

                        if (filePosChanged && (Now - LastFilePosUpdate).TotalMilliseconds > 500)
                        {
                            RaiseEvent(FilePositionChanged);
                            LastFilePosUpdate = Now;
                            filePosChanged = false;
                        }

                        Thread.Sleep(WaitTime);
                    }

                    string line = lineTask.Result;

                    // Null line indicates connection was closed
                    if (line == null)
                    {
                        RaiseEvent(Info, "Connection closed by remote end");
                        break;
                    }

                    RecordLog("< " + line);

                    if (line == ResponseOk)
                    {
                        if (Sent.Count != 0)
                        {
                            BufferState -= ((string)Sent.Dequeue()).Length + 1;
                        }
                        else
                        {
                            // This can happen during startup (initial $G/$# aren't queued)
                            // or if buffer state gets out of sync - just reset it
                            BufferState = 0;
                        }
                    }
                    else
                    {
                        if (line.StartsWith(ResponseErrorPrefix))
                        {
                            if (Sent.Count != 0)
                            {
                                string errorline = (string)Sent.Dequeue();

                                RaiseEvent(ReportError, $"{line}: {errorline}");
                                BufferState -= errorline.Length + 1;
                            }
                            else
                            {
                                if ((DateTime.Now - StartTime).TotalMilliseconds > 200)
                                    RaiseEvent(ReportError, $"Received <{line}> without anything in the Sent Buffer");

                                BufferState = 0;
                            }

                            Mode = OperatingMode.Manual;
                        }
                        else if (line.StartsWith("<"))
                        {
                            RaiseEvent(ParseStatus, line);
                            SendMacroStatusReceived = true;
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
                            RaiseEvent(ReportError, line);
                            Mode = OperatingMode.Manual;
                            ToSend.Clear();
                            ToSendMacro.Clear();
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
                // Ensure cleanup happens whether we exit via break, return, or exception
                if (Connected)
                {
                    RaiseEvent(() => Disconnect());
                }
            }
        }

        public void Connect()
        {
            if (Connected)
            {
                throw new Exception("Can't Connect: Already Connected");
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
                        RaiseEvent(NonFatalException, $"Access denied to {_settings.SerialPortName} (port may be in use)");
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
                    Log = new StreamWriter(Constants.LogFile);
                }
                catch (Exception e)
                {
                    NonFatalException?.Invoke("could not open logfile: " + e.Message);
                }
            }

            ToSend.Clear();
            ToSendPriority.Clear();
            Sent.Clear();
            ToSendMacro.Clear();

            Mode = OperatingMode.Manual;

            PositionUpdateReceived?.Invoke();

            WorkerThread = new Thread(Work);
            WorkerThread.Priority = ThreadPriority.AboveNormal;
            WorkerThread.Start();
        }

        public void Disconnect()
        {
            if (Log != null)
            {
                Log.Close();
            }
            Log = null;

            Connected = false;

            // Only join if we're not on the worker thread (to avoid deadlock)
            if (WorkerThread != null && WorkerThread != Thread.CurrentThread)
            {
                WorkerThread.Join();
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
                        // Ignore close errors during disconnect - we're cleaning up anyway
                    }
                    Connection?.Dispose();
                    Connection = null;
                    break;
                case ConnectionType.Ethernet:
                    if (Connection != null)
                    {
                        Connection.Close();
                        ClientEthernet?.Close();
                    }
                    Connection = null;
                    break;
                default:
                    throw new Exception("Invalid Connection Type");
            }

            Mode = OperatingMode.Disconnected;

            MachinePosition = new Vector3();
            WorkOffset = new Vector3();
            FeedRateRealtime = 0;
            CurrentTLO = 0;

            PositionUpdateReceived?.Invoke();

            Status = "Disconnected";
            DistanceMode = ParseDistanceMode.Absolute;
            Unit = ParseUnit.Metric;
            Plane = ArcPlane.XY;
            BufferState = 0;

            FeedOverride = 100;
            RapidOverride = 100;
            SpindleOverride = 100;

            OverrideChanged?.Invoke();

            PinStateLimitX = false;
            PinStateLimitY = false;
            PinStateLimitZ = false;
            PinStateProbe = false;

            PinStateChanged?.Invoke();

            ToSend.Clear();
            ToSendPriority.Clear();
            Sent.Clear();
            ToSendMacro.Clear();
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

            ToSend.Enqueue(line);
        }

        public void SoftReset()
        {
            if (!Connected)
            {
                RaiseEvent(Info, "Not Connected");
                return;
            }

            Mode = OperatingMode.Manual;

            ToSend.Clear();
            ToSendPriority.Clear();
            Sent.Clear();
            ToSendMacro.Clear();
            ToSendPriority.Enqueue(GrblProtocol.SoftReset);

            BufferState = 0;

            FeedOverride = 100;
            RapidOverride = 100;
            SpindleOverride = 100;

            OverrideChanged?.Invoke();

            SendLine(CmdViewGCodeState);
            SendLine(CmdViewParameters);
        }

        public void SendMacroLines(params string[] lines)
        {
            if (Mode != OperatingMode.Manual)
            {
                RaiseEvent(Info, "Not in Manual Mode");
                return;
            }

            foreach (string line in lines)
            {
                ToSendMacro.Enqueue(line.Trim());
            }

            Mode = OperatingMode.SendMacro;
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
                var matches = GCodeParser.GCodeSplitter.Matches(file[line]);

                foreach (Match m in matches)
                {
                    if (m.Groups[1].Value == "M")
                    {
                        int code = int.MinValue;

                        if (int.TryParse(m.Groups[2].Value, out code))
                        {
                            if (IsPauseMCode(code))
                                pauselines[line] = true;
                        }
                    }
                }
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

        public void FileStart()
        {
            if (!Connected)
            {
                RaiseEvent(Info, "Not Connected");
                return;
            }

            if (Mode != OperatingMode.Manual)
            {
                RaiseEvent(Info, "Not in Manual Mode");
                return;
            }

            Mode = OperatingMode.SendFile;
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

        public void ProbeStart()
        {
            if (!Connected)
            {
                RaiseEvent(Info, "Not Connected");
                return;
            }

            if (Mode != OperatingMode.Manual)
            {
                RaiseEvent(Info, "Can't start probing while running!");
                return;
            }

            Mode = OperatingMode.Probe;
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

            if (line.StartsWith(ResponseTloPrefix))
            {
                try
                {
                    CurrentTLO = double.Parse(line.Substring(5, line.Length - 6), Constants.DecimalParseFormat);
                    RaiseEvent(PositionUpdateReceived);
                }
                catch { RaiseEvent(NonFatalException, "Error while Parsing Status Message"); }
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
            catch { RaiseEvent(NonFatalException, "Error while Parsing Status Message"); }
        }

        private void ParseStatus(string line)
        {
            MatchCollection statusMatch = StatusEx.Matches(line);

            if (statusMatch.Count == 0)
            {
                ReportBadStatus(line);
                return;
            }

            bool posUpdate = false;
            bool overrideUpdate = false;
            bool pinStateUpdate = false;
            bool resetPins = true;

            foreach (Match m in statusMatch)
            {
                if (m.Index == 1)
                {
                    Status = m.Groups[1].Value;
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

                        BufferState = used;
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
                        string PositionString = m.Groups[2].Value;

                        if (_settings.IgnoreAdditionalAxes)
                        {
                            string[] parts = PositionString.Split(',');
                            if (parts.Length > 3)
                            {
                                Array.Resize(ref parts, 3);
                                PositionString = string.Join(",", parts);
                            }
                        }

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
        }

        private void ParseProbe(string line)
        {
            if (ProbeFinished == null)
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

            string PositionString = pos.Value;

            if (_settings.IgnoreAdditionalAxes)
            {
                string[] parts = PositionString.Split(',');
                if (parts.Length > 3)
                {
                    Array.Resize(ref parts, 3);
                    PositionString = string.Join(",", parts);
                }
            }

            Vector3 ProbePos = Vector3.Parse(PositionString);
            LastProbePosMachine = ProbePos;

            ProbePos -= WorkOffset;
            ProbePos.X += _settings.ProbeOffsetX;
            ProbePos.Y += _settings.ProbeOffsetY;
            LastProbePosWork = ProbePos;

            bool ProbeSuccess = success.Value == "1";

            ProbeFinished.Invoke(ProbePos, ProbeSuccess);
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

        private void ReportError(string error)
        {
            NonFatalException?.Invoke(GrblCodeTranslator.ExpandError(error, _settings.FirmwareType));
        }

        private void ReportBadStatus(string line)
        {
            NonFatalException?.Invoke($"Received Bad Status: '{line}'");
        }

        // Event helpers - direct invocation instead of WPF Dispatcher
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
