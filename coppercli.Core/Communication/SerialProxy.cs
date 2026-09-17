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
        public event Action? ClientConnected;
        public event Action? ClientDisconnected;

        /// <summary>
        /// Set by the host to report whether something else holds the serial port, such as
        /// the web server's Machine connection. A true reply rejects the new client instead
        /// of opening the port a second time.
        /// </summary>
        public Func<bool>? IsSerialPortInUse { get; set; }

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

        /// <summary>
        /// The listener is bound, and the serial port is open whenever a client is attached.
        /// Checked after a suspend and resume, which can leave either one closed.
        /// </summary>
        public bool IsHealthy
        {
            get
            {
                if (!IsRunning)
                {
                    return false;
                }

                if (HasClient && (_serialPort == null || !_serialPort.IsOpen))
                {
                    return false;
                }

                if (_listener == null || !_listener.Server.IsBound)
                {
                    return false;
                }

                return true;
            }
        }

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

        private SerialPort? _serialPort;
        private TcpListener? _listener;
        private TcpClient? _client;
        private NetworkStream? _networkStream;
        private Thread? _acceptThread;
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
                var openTask = Task.Run(() =>
                {
                    using var testPort = new SerialPort(serialPort, baudRate);
                    testPort.Open();
                    testPort.Close();
                });

                if (!openTask.Wait(SerialOpenTimeoutMs))
                {
                    throw new TimeoutException($"Timeout opening serial port {serialPort} (may be held by another process)");
                }

                if (openTask.IsFaulted && openTask.Exception != null)
                {
                    throw openTask.Exception.InnerException ?? openTask.Exception;
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
            _serialToTcpThread?.Join(ThreadJoinTimeoutMs);
            _tcpToSerialThread?.Join(ThreadJoinTimeoutMs);

            try
            {
                _serialPort?.Close();
                _serialPort?.Dispose();
            }
            catch
            {
            }

            _serialPort = null;
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

            // The client that was driving the machine is gone, so stop the machine here.
            SendSafetyStop();
            return true;
        }

        /// <summary>
        /// Feed hold then soft reset, written straight to the port. Sent when the client
        /// driving the machine disconnects, because GRBL keeps working through its planner
        /// buffer.
        /// </summary>
        private void SendSafetyStop()
        {
            try
            {
                if (_serialPort == null)
                {
                    return;
                }

                Machine.WriteStopSequence(_serialPort.BaseStream);
                RaiseInfo("Feed hold + soft reset sent (safety stop)");
            }
            catch
            {
                // The port may already be unusable, and nothing else can stop the machine.
            }
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
                        if (_client != null)
                        {
                            RaiseInfo($"Rejected connection from {newClientAddress} (client already connected)");
                            SendMessageAndClose(newClient, Constants.ProxyConnectionRejected);
                            continue;
                        }

                        _client = newClient;
                        _networkStream = _client.GetStream();
                        ClientAddress = newClientAddress;
                        _clientConnectedAtMs = Environment.TickCount64;
                        BytesFromClient = 0;
                        BytesToClient = 0;
                        _lastClientActivityMs = Environment.TickCount64;
                        _silentIntervals = 0;
                    }

                    RaiseInfo($"Client connected: {ClientAddress}");

                    if (IsSerialPortInUse?.Invoke() == true)
                    {
                        RaiseInfo("Rejected: serial port in use by web client");
                        SendMessage(newClient, Constants.ProxySerialPortInUse);
                        lock (_clientLock) { CloseClientUnlocked(); }
                        continue;
                    }

                    try
                    {
                        _serialPort = new SerialPort(SerialPortName, BaudRate)
                        {
                            ReadTimeout = Constants.SerialReadTimeoutMs,
                            WriteTimeout = Constants.SerialWriteTimeoutMs
                        };

                        // Opened on a task with a deadline: SerialPort.Open can hang
                        // outright on a stuck port.
                        var openTask = Task.Run(() => _serialPort.Open());
                        if (!openTask.Wait(SerialOpenTimeoutMs))
                        {
                            _serialPort.Dispose();
                            _serialPort = null;
                            throw new TimeoutException($"Timeout opening {SerialPortName}");
                        }
                        if (openTask.IsFaulted && openTask.Exception != null)
                        {
                            throw openTask.Exception.InnerException ?? openTask.Exception;
                        }

                        RaiseInfo($"Opened serial port {SerialPortName}");
                    }
                    catch (Exception ex)
                    {
                        var errorMsg = $"Cannot access {SerialPortName}: {ex.Message}\r\n";
                        RaiseError(errorMsg.TrimEnd());
                        SendMessage(newClient, errorMsg);
                        lock (_clientLock) { CloseClientUnlocked(); }
                        continue;
                    }

                    ClientConnected?.Invoke();

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

                    // Both loops return when the client disconnects.
                    _serialToTcpThread.Join();
                    _tcpToSerialThread.Join();

                    try
                    {
                        _serialPort?.Close();
                        _serialPort?.Dispose();
                        RaiseInfo("Closed serial port");
                    }
                    catch
                    {
                    }
                    _serialPort = null;
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
        /// Stops the machine before dropping the client, because GRBL keeps working through
        /// whatever it has already buffered.
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

            SendSafetyStop();

            RaiseInfo($"Client disconnected: {address}");
            ClientDisconnected?.Invoke();
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
