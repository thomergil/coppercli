#nullable enable

using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using coppercli.Core.Util;

namespace coppercli.Core.Communication
{
    /// <summary>
    /// Forwards bytes between one TCP client and a serial port, so a remote GRBL client can
    /// drive a machine attached to this host. One client at a time; a second is rejected.
    /// </summary>
    public class SerialProxy : IDisposable
    {
        /// <summary>How long a client may send nothing before that counts as one silence.</summary>
        private const int ClientSilenceIntervalMs = 10000;

        /// <summary>How many of those in a row mean the client has gone.</summary>
        private const int MaxSilentIntervals = 3;
        private const int HealthCheckIntervalMs = 5000;
        private const int RecoveryDelayMs = 1000;
        private const byte GrblStatusQuery = (byte)'?';
        private const int SerialOpenTimeoutMs = 5000;
        private const int ThreadJoinTimeoutMs = 1000;

        public event Action<string>? Info;
        public event Action<string>? Error;

        /// <summary>
        /// Set by the host that shares the serial port. Asked before a client is admitted; a
        /// false answer refuses the client. Unset, every client may have the port.
        /// </summary>
        public Func<bool>? TryClaimSerialPort { get; set; }

        /// <summary>
        /// Set with <see cref="TryClaimSerialPort"/>. Called once the machine is stopped and the
        /// port closed behind an admitted client, however its session ended.
        /// </summary>
        public Action? ReleaseSerialPort { get; set; }

        /// <summary>Opens the serial port for a session. A test replaces it to stand in for the hardware.</summary>
        internal Func<string, int, ISerialLink> OpenSerialLink { get; set; } = OpenSerialPort;

        public bool IsRunning { get; private set; }

        /// <summary>Derived from the connection, so no second flag can fall out of step.</summary>
        public bool HasClient
        {
            get { lock (_clientLock) { return _client != null; } }
        }
        public string? ClientAddress { get; private set; }
        public int TcpPort { get; private set; }
        public string SerialPortName { get; private set; } = string.Empty;
        public int BaudRate { get; private set; }

        public long BytesFromClient { get; private set; }
        public long BytesToClient { get; private set; }
        /// <summary>
        /// When the client attached, on the monotonic clock, or null with none attached; a
        /// wall clock would misreport the elapsed time the moment it steps. Read through
        /// <see cref="ClientConnectedFor"/>, because three threads null it under the lock
        /// and a caller that tests it then reads it can find it gone between the two.
        /// </summary>
        private long? _clientConnectedAtMs;

        /// <summary>
        /// How long the client has been attached, or null with none attached. One read under
        /// the lock, so a disconnect mid-draw cannot take the screen down.
        /// </summary>
        public TimeSpan? ClientConnectedFor
        {
            get
            {
                lock (_clientLock)
                {
                    return _clientConnectedAtMs is long since
                        ? TimeSpan.FromMilliseconds(Environment.TickCount64 - since)
                        : null;
                }
            }
        }

        private ISerialLink? _serialPort;
        private TcpListener? _listener;
        private TcpClient? _client;
        private NetworkStream? _networkStream;
        private Thread? _acceptThread;
        private Thread? _sessionThread;

        // From a client's admission until its session has stopped the machine and released
        // the port. Under _clientLock, so the accept loop sees the release and the end of the
        // session together.
        private bool _sessionActive;
        private Thread? _serialToTcpThread;
        private Thread? _tcpToSerialThread;
        private CancellationTokenSource? _cts;
        private readonly object _clientLock = new();
        private bool _disposed;
        /// <summary>
        /// When the client last sent anything, on the monotonic clock. A wall clock that
        /// steps would either drop a live client at once or never time out a dead one.
        /// </summary>
        private long _lastClientActivityMs;

        /// <summary>
        /// How many intervals in a row the client has sent nothing. The '?' sent below goes
        /// to the serial port, not to the client, so it proves nothing about the peer.
        /// </summary>
        private int _silentIntervals;

        /// <summary>
        /// Checks the serial port can be opened, then starts the TCP listener. The port is
        /// held open only while a client is attached.
        /// </summary>
        public void Start(string serialPort, int baudRate, int tcpPort)
        {
            if (IsRunning)
            {
                throw new InvalidOperationException("Proxy is already running");
            }

            SerialPortName = serialPort;
            BaudRate = baudRate;
            TcpPort = tcpPort;

            try
            {
                using (OpenSerialLink(serialPort, baudRate))
                {
                }

                RaiseInfo($"Validated serial port {serialPort} @ {baudRate}");

                _listener = new TcpListener(IPAddress.Any, tcpPort);
                _listener.Start();
                RaiseInfo($"Listening on TCP port {tcpPort}");

                _cts = new CancellationTokenSource();

                _acceptThread = new Thread(AcceptLoop)
                {
                    Name = "ProxyAccept",
                    IsBackground = true
                };
                _acceptThread.Start();

                IsRunning = true;
            }
            catch (Exception ex)
            {
                _listener?.Stop();
                _listener = null;
                throw new InvalidOperationException($"Failed to start proxy: {ex.Message}", ex);
            }
        }

        public void Stop()
        {
            if (!IsRunning)
            {
                return;
            }

            RaiseInfo("Stopping proxy...");

            _cts?.Cancel();

            CloseClient();

            try
            {
                _listener?.Stop();
            }
            catch
            {
            }

            _acceptThread?.Join(ThreadJoinTimeoutMs);
            _sessionThread?.Join(ThreadJoinTimeoutMs);
            _serialToTcpThread?.Join(ThreadJoinTimeoutMs);
            _tcpToSerialThread?.Join(ThreadJoinTimeoutMs);

            // A no-op when the session has already stopped the machine and closed the port.
            StopMachineAndClosePort();

            _listener = null;
            _cts?.Dispose();
            _cts = null;

            IsRunning = false;
            RaiseInfo("Proxy stopped");
        }

        /// <summary>
        /// Drops the attached client so another may connect, sending it a message first so
        /// it can exit cleanly. False when no client was attached.
        /// </summary>
        public bool ForceDisconnectClient()
        {
            lock (_clientLock)
            {
                if (_client == null)
                {
                    return false;
                }

                RaiseInfo("Force-disconnecting client");

                SendMessage(_client, Constants.ProxyForceDisconnect);

                // Shutdown(Send) signals no more data while leaving the client able to
                // read what is already queued to it.
                try
                {
                    _client.Client.Shutdown(System.Net.Sockets.SocketShutdown.Send);
                }
                catch
                {
                }

                // The message has to reach the client before the socket closes.
                Thread.Sleep(Constants.ForceDisconnectMessageDelayMs);

                CloseClientUnlocked();
            }

            return true;
        }

        /// <summary>
        /// Feed hold then soft reset, written straight to the port, then the port is closed.
        /// Sent whenever a client's session ends, because GRBL keeps working through its
        /// planner buffer. The port is taken from the field first, so of two callers only one
        /// sends the stop and closes it.
        /// </summary>
        private void StopMachineAndClosePort()
        {
            var port = Interlocked.Exchange(ref _serialPort, null);
            if (port == null)
            {
                return;
            }

            try
            {
                Machine.WriteStopSequence(port.BaseStream);
                RaiseInfo("Feed hold + soft reset sent (safety stop)");
            }
            catch
            {
                // The port may already be unusable; the close below still runs.
            }

            try
            {
                port.Close();
                port.Dispose();
                RaiseInfo("Closed serial port");
            }
            catch
            {
            }
        }

        /// <summary>
        /// Opens the port on a task with a deadline, because SerialPort.Open can hang outright
        /// on a stuck port.
        /// </summary>
        private static ISerialLink OpenSerialPort(string name, int baudRate)
        {
            var port = new SerialPortLink(name, baudRate)
            {
                ReadTimeout = Constants.SerialReadTimeoutMs,
                WriteTimeout = Constants.SerialWriteTimeoutMs
            };

            var open = Task.Run(port.Open);
            try
            {
                if (!open.Wait(SerialOpenTimeoutMs))
                {
                    // Disposed once the open returns: disposing now would close nothing and
                    // leave open the handle that the open then gets.
                    open.ContinueWith(_ => port.Dispose());
                    throw new TimeoutException(
                        $"Timeout opening serial port {name} (it may be held by another process)");
                }
            }
            catch (AggregateException ex)
            {
                port.Dispose();
                throw ex.InnerException ?? ex;
            }

            return port;
        }

        /// <summary>
        /// Rebinds the TCP listener when it is no longer bound, which a suspend and resume
        /// can leave it. A serial port fault is not recovered here: it disconnects the
        /// client, and the port is reopened on the next connection.
        /// </summary>
        private bool TryRecoverIfNeeded()
        {
            bool listenerOk = _listener != null && _listener.Server.IsBound;

            if (listenerOk)
            {
                return true;
            }

            RaiseInfo("TCP listener unhealthy, attempting recovery...");
            Thread.Sleep(RecoveryDelayMs);

            try
            {
                _listener?.Stop();
            }
            catch
            {
            }

            try
            {
                _listener = new TcpListener(IPAddress.Any, TcpPort);
                _listener.Start();
                RaiseInfo($"TCP listener recovered on port {TcpPort}");
                return true;
            }
            catch (Exception ex)
            {
                RaiseError($"TCP listener recovery failed: {ex.Message}");
                return false;
            }
        }

        private void AcceptLoop()
        {
            long lastHealthCheck = Environment.TickCount64;

            while (_cts != null && !_cts.IsCancellationRequested)
            {
                try
                {
                    if (Environment.TickCount64 - lastHealthCheck >= HealthCheckIntervalMs)
                    {
                        lastHealthCheck = Environment.TickCount64;
                        if (!TryRecoverIfNeeded())
                        {
                            break;
                        }
                    }

                    if (_listener == null)
                    {
                        break;
                    }

                    if (!_listener.Pending())
                    {
                        Thread.Sleep(Constants.ProxyAcceptLoopSleepMs);
                        continue;
                    }

                    var newClient = _listener.AcceptTcpClient();
                    var newClientAddress = ((IPEndPoint?)newClient.Client.RemoteEndPoint)?.ToString() ?? "unknown";

                    lock (_clientLock)
                    {
                        // A session keeps the slot until it has stopped the machine and
                        // released the port, which is after its client is gone.
                        if (_sessionActive)
                        {
                            RaiseInfo($"Rejected connection from {newClientAddress} (client already connected)");
                            SendMessageAndClose(newClient, Constants.ProxyConnectionRejected);
                            continue;
                        }
                    }

                    // Claimed before the client goes into _client, so HasClient is never true
                    // for a client the host refused.
                    if (TryClaimSerialPort?.Invoke() == false)
                    {
                        RaiseInfo($"Rejected connection from {newClientAddress} (the server has the machine)");
                        SendMessageAndClose(newClient, Constants.ProxySerialPortInUse);
                        continue;
                    }

                    try
                    {
                        lock (_clientLock)
                        {
                            _sessionActive = true;
                            _client = newClient;
                            _networkStream = _client.GetStream();
                            ClientAddress = newClientAddress;
                            _clientConnectedAtMs = Environment.TickCount64;
                            BytesFromClient = 0;
                            BytesToClient = 0;
                            _lastClientActivityMs = Environment.TickCount64;
                            _silentIntervals = 0;
                        }

                        RaiseInfo($"Client connected: {newClientAddress}");

                        // On its own thread, so this loop goes on answering: a second client is
                        // told the proxy is taken instead of waiting unanswered.
                        _sessionThread = new Thread(RunSession)
                        {
                            Name = "ProxySession",
                            IsBackground = true
                        };
                        _sessionThread.Start();
                    }
                    catch
                    {
                        // Admitted but no session started, so nothing else gives the port back.
                        EndSession();
                        throw;
                    }
                }
                catch (SocketException) when (_cts?.IsCancellationRequested == true)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_cts?.IsCancellationRequested != true)
                    {
                        RaiseError($"Accept error: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// The admitted client's session, until the machine is stopped and the port closed
        /// behind it, however the session ends.
        /// </summary>
        private void RunSession()
        {
            try
            {
                try
                {
                    _serialPort = OpenSerialLink(SerialPortName, BaudRate);
                    RaiseInfo($"Opened serial port {SerialPortName}");
                }
                catch (Exception ex)
                {
                    // The exception's own text goes to the log only; the screen and the client
                    // get a sentence that says what to check.
                    Controllers.ControllerLog.Log("SerialProxy: opening {0} failed - {1}", SerialPortName, ex);
                    var errorMsg = string.Format(Constants.ProxyCannotOpenSerialPort, SerialPortName);
                    RaiseError(errorMsg.TrimEnd());
                    lock (_clientLock)
                    {
                        if (_client != null)
                        {
                            SendMessage(_client, errorMsg);
                        }
                    }
                    return;
                }

                _serialToTcpThread = new Thread(SerialToTcpLoop)
                {
                    Name = "ProxySerialToTcp",
                    IsBackground = true
                };
                _tcpToSerialThread = new Thread(TcpToSerialLoop)
                {
                    Name = "ProxyTcpToSerial",
                    IsBackground = true
                };

                _serialToTcpThread.Start();
                _tcpToSerialThread.Start();

                // Both loops return when the client disconnects, however it left.
                _serialToTcpThread.Join();
                _tcpToSerialThread.Join();
            }
            finally
            {
                // The loops have stopped writing and the port is still open, so the stop goes
                // out before the close.
                StopMachineAndClosePort();
                EndSession();
            }
        }

        /// <summary>Drops the client, gives the port back, and frees the slot, in one step.</summary>
        private void EndSession()
        {
            lock (_clientLock)
            {
                CloseClientUnlocked();
                ReleaseSerialPort?.Invoke();
                _sessionActive = false;
            }
        }

        private void SerialToTcpLoop()
        {
            var buffer = new byte[Constants.ProxyBufferSize];

            while (_cts != null && !_cts.IsCancellationRequested)
            {
                try
                {
                    if (_serialPort == null || !_serialPort.IsOpen)
                    {
                        break;
                    }

                    lock (_clientLock)
                    {
                        if (_client == null || _networkStream == null)
                        {
                            break;
                        }
                    }

                    if (_serialPort.BytesToRead > 0)
                    {
                        int count = _serialPort.Read(buffer, 0, buffer.Length);
                        if (count > 0)
                        {
                            lock (_clientLock)
                            {
                                if (_networkStream != null && _client != null)
                                {
                                    _networkStream.Write(buffer, 0, count);
                                    BytesToClient += count;
                                }
                                else
                                {
                                    break;
                                }
                            }
                        }
                    }
                    else
                    {
                        Thread.Sleep(Constants.ProxyThreadSleepMs);
                    }
                }
                catch (TimeoutException)
                {
                }
                catch (IOException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    // SerialPort throws this once the port has been closed.
                    break;
                }
                catch (Exception ex)
                {
                    if (_cts?.IsCancellationRequested != true)
                    {
                        RaiseError($"Serial read error: {ex.Message}");
                    }
                    break;
                }
            }
        }

        private void TcpToSerialLoop()
        {
            var buffer = new byte[Constants.ProxyBufferSize];
            long lastSilenceCheck = Environment.TickCount64;

            while (_cts != null && !_cts.IsCancellationRequested)
            {
                try
                {
                    NetworkStream? stream;
                    TcpClient? client;
                    lock (_clientLock)
                    {
                        stream = _networkStream;
                        client = _client;
                    }

                    if (stream == null || client == null)
                    {
                        break;
                    }

                    // Poll returns true for data available, connection closed or error
                    // alike, so Available below is what separates them.
                    var socket = client.Client;
                    if (socket.Poll(Constants.SocketPollTimeoutMicroseconds, SelectMode.SelectRead))
                    {
                        if (socket.Available == 0)
                        {
                            RaiseInfo("Client connection closed");
                            break;
                        }

                        int count = stream.Read(buffer, 0, buffer.Length);
                        if (count == 0)
                        {
                            RaiseInfo("Client disconnected gracefully");
                            break;
                        }

                        _lastClientActivityMs = Environment.TickCount64;
                        _silentIntervals = 0;

                        if (_serialPort != null && _serialPort.IsOpen)
                        {
                            _serialPort.Write(buffer, 0, count);
                            BytesFromClient += count;
                        }
                    }
                    else
                    {
                        long now = Environment.TickCount64;

                        if (now - _lastClientActivityMs >= ClientSilenceIntervalMs
                            && now - lastSilenceCheck >= ClientSilenceIntervalMs)
                        {
                            lastSilenceCheck = now;
                            _silentIntervals++;

                            if (_silentIntervals >= MaxSilentIntervals)
                            {
                                RaiseInfo($"Client sent nothing for {_silentIntervals} intervals");
                                break;
                            }

                            // Keeps GRBL reporting, so the client has something to read if
                            // it is still there.
                            if (_serialPort != null && _serialPort.IsOpen)
                            {
                                _serialPort.Write(new[] { GrblStatusQuery }, 0, 1);
                            }
                        }
                    }
                }
                catch (IOException)
                {
                    break;
                }
                catch (SocketException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_cts?.IsCancellationRequested != true)
                    {
                        RaiseError($"TCP read error: {ex.Message}");
                    }
                    break;
                }
            }

            HandleClientDisconnect();
        }

        /// <summary>
        /// Drops the client. RunSession stops the machine once both loops have returned.
        /// </summary>
        private void HandleClientDisconnect()
        {
            string? address;
            lock (_clientLock)
            {
                if (!HasClient)
                {
                    return;
                }

                address = ClientAddress;
                CloseClientUnlocked();
            }

            RaiseInfo($"Client disconnected: {address}");
        }

        /// <summary>Sends a message and leaves the connection open.</summary>
        private static void SendMessage(TcpClient client, string message)
        {
            try
            {
                var stream = client.GetStream();
                var bytes = System.Text.Encoding.UTF8.GetBytes(message);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
            catch
            {
            }
        }

        /// <summary>Sends a rejection, before the client has been fully accepted.</summary>
        private static void SendMessageAndClose(TcpClient client, string message)
        {
            SendMessage(client, message);
            try
            {
                client.Close();
            }
            catch
            {
            }
        }

        private void CloseClient()
        {
            lock (_clientLock)
            {
                CloseClientUnlocked();
            }
        }

        /// <summary>The caller must hold _clientLock.</summary>
        private void CloseClientUnlocked()
        {
            try
            {
                _networkStream?.Close();
                _client?.Close();
            }
            catch
            {
            }

            _networkStream = null;
            _client = null;
            ClientAddress = null;
            _clientConnectedAtMs = null;
        }

        private void RaiseInfo(string message)
        {
            Info?.Invoke(message);
        }

        private void RaiseError(string message)
        {
            Error?.Invoke(message);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Stop();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
