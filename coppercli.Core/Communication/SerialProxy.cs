#nullable enable

using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using coppercli.Core.Util;

namespace coppercli.Core.Communication
{
    /// <summary>
    /// Serial-to-TCP proxy that bridges a serial port to a TCP listener,
    /// allowing remote GRBL clients to connect over the network.
    /// </summary>
    public class SerialProxy : IDisposable
    {
        // =========================================================================
        // Constants
        // =========================================================================
        /// <summary>How long a client may send nothing before that counts as one silence.</summary>
        private const int ClientSilenceIntervalMs = 10000;

        /// <summary>How many of those in a row mean the client has gone.</summary>
        private const int MaxSilentIntervals = 3;
        private const int HealthCheckIntervalMs = 5000; // Check health every 5 seconds
        private const int RecoveryDelayMs = 1000;       // Wait before attempting recovery
        private const byte GrblStatusQuery = (byte)'?';
        private const int SerialOpenTimeoutMs = 5000;     // Timeout for serial port open
        private const int ThreadJoinTimeoutMs = 1000;     // How long to wait for a worker thread to end

        // =========================================================================
        // Events
        // =========================================================================
        public event Action<string>? Info;
        public event Action<string>? Error;
        public event Action? ClientConnected;
        public event Action? ClientDisconnected;

        // =========================================================================
        // Callbacks
        // =========================================================================

        /// <summary>
        /// Optional callback to check if the serial port is in use by another component
        /// (e.g., web server's Machine connection). If set and returns true, the proxy
        /// will reject new TUI clients with a specific message instead of attempting
        /// to open the serial port.
        /// </summary>
        public Func<bool>? IsSerialPortInUse { get; set; }

        // =========================================================================
        // Public state properties
        // =========================================================================
        public bool IsRunning { get; private set; }

        /// <summary>
        /// Whether a client is attached, read from the connection itself.
        /// </summary>
        public bool HasClient
        {
            get { lock (_clientLock) { return _client != null; } }
        }
        public string? ClientAddress { get; private set; }
        public int TcpPort { get; private set; }
        public string SerialPortName { get; private set; } = string.Empty;
        public int BaudRate { get; private set; }

        /// <summary>
        /// Returns true if the proxy appears healthy (listener bound, serial port open when client connected).
        /// Useful for checking state after system suspend/resume.
        /// </summary>
        public bool IsHealthy
        {
            get
            {
                if (!IsRunning)
                {
                    return false;
                }

                // Check if serial port is still open (only required when client is connected)
                if (HasClient && (_serialPort == null || !_serialPort.IsOpen))
                {
                    return false;
                }

                // Check if TCP listener is still bound
                if (_listener == null || !_listener.Server.IsBound)
                {
                    return false;
                }

                return true;
            }
        }

        // =========================================================================
        // Statistics
        // =========================================================================
        public long BytesFromClient { get; private set; }
        public long BytesToClient { get; private set; }
        /// <summary>
        /// When the client attached, on the monotonic clock, or null with none attached.
        /// A screen shows how long ago that was, which a wall clock gets wrong the moment it
        /// steps.
        ///
        /// Private, and read through <see cref="ClientConnectedFor"/>: three threads null it
        /// under the lock, so a caller that tests then reads can find it gone between the two.
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

        // =========================================================================
        // Private state
        // =========================================================================
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
        /// When the client last sent anything, on the monotonic clock: a wall clock that
        /// steps would disconnect it or never notice it had gone.
        /// </summary>
        private long _lastClientActivityMs;

        /// <summary>
        /// How many intervals in a row the client has sent nothing. The '?' sent below goes
        /// to the serial port, not to the client, so it verifies nothing about the peer.
        /// </summary>
        private int _silentIntervals;

        /// <summary>
        /// Starts the proxy, opening the serial port and TCP listener.
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
                // Validate serial port is accessible (open then close) with timeout
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

                // Re-throw any exception from the task
                if (openTask.IsFaulted && openTask.Exception != null)
                {
                    throw openTask.Exception.InnerException ?? openTask.Exception;
                }

                RaiseInfo($"Validated serial port {serialPort} @ {baudRate}");

                // Start TCP listener
                _listener = new TcpListener(IPAddress.Any, tcpPort);
                _listener.Start();
                RaiseInfo($"Listening on TCP port {tcpPort}");

                // Create cancellation token
                _cts = new CancellationTokenSource();

                // Start accept thread
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
                // Cleanup on failure
                _listener?.Stop();
                _listener = null;
                throw new InvalidOperationException($"Failed to start proxy: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Stops the proxy and releases all resources.
        /// </summary>
        public void Stop()
        {
            if (!IsRunning)
            {
                return;
            }

            RaiseInfo("Stopping proxy...");

            // Signal threads to stop
            _cts?.Cancel();

            // Close client connection
            CloseClient();

            // Stop listener
            try
            {
                _listener?.Stop();
            }
            catch
            {
                // Ignore errors during shutdown
            }

            // Wait for threads to finish
            _acceptThread?.Join(ThreadJoinTimeoutMs);
            _serialToTcpThread?.Join(ThreadJoinTimeoutMs);
            _tcpToSerialThread?.Join(ThreadJoinTimeoutMs);

            // Close serial port
            try
            {
                _serialPort?.Close();
                _serialPort?.Dispose();
            }
            catch
            {
                // Ignore errors during shutdown
            }

            _serialPort = null;
            _listener = null;
            _cts?.Dispose();
            _cts = null;

            IsRunning = false;
            RaiseInfo("Proxy stopped");
        }

        /// <summary>
        /// Force-disconnects the current client (if any) to allow another client to connect.
        /// Sends a force-disconnect message before closing so the client can exit gracefully.
        /// Returns true if a client was disconnected, false if no client was connected.
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

                // Send message before closing so client knows to exit
                SendMessage(_client, Constants.ProxyForceDisconnect);

                // Graceful TCP shutdown: signal "no more data" but let client read pending data
                try
                {
                    _client.Client.Shutdown(System.Net.Sockets.SocketShutdown.Send);
                }
                catch
                {
                    // Ignore shutdown errors
                }

                // Brief delay to ensure message is received before connection closes
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
                // Ignore errors - serial port may be in bad state
            }
        }

        /// <summary>
        /// Checks if resources are healthy and attempts recovery if not.
        /// Called periodically from AcceptLoop to handle suspend/resume.
        /// Returns true if healthy or recovery succeeded, false if unrecoverable.
        /// Only attempts to recover the TCP listener - serial port issues will
        /// naturally disconnect the client without recovery attempts.
        /// </summary>
        private bool TryRecoverIfNeeded()
        {
            bool listenerOk = _listener != null && _listener.Server.IsBound;

            if (listenerOk)
            {
                return true;
            }

            // TCP listener is down - attempt recovery
            RaiseInfo("TCP listener unhealthy, attempting recovery...");
            Thread.Sleep(RecoveryDelayMs);

            try
            {
                _listener?.Stop();
            }
            catch
            {
                // Ignore cleanup errors
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

        /// <summary>
        /// Thread that accepts incoming TCP connections.
        /// </summary>
        private void AcceptLoop()
        {
            long lastHealthCheck = Environment.TickCount64;

            while (_cts != null && !_cts.IsCancellationRequested)
            {
                try
                {
                    // Periodic health check and recovery (e.g., after system suspend/resume)
                    if (Environment.TickCount64 - lastHealthCheck >= HealthCheckIntervalMs)
                    {
                        lastHealthCheck = Environment.TickCount64;
                        if (!TryRecoverIfNeeded())
                        {
                            // Recovery failed, exit loop
                            break;
                        }
                    }

                    if (_listener == null)
                    {
                        break;
                    }

                    // Check for pending connection with timeout
                    if (!_listener.Pending())
                    {
                        Thread.Sleep(Constants.ProxyAcceptLoopSleepMs);
                        continue;
                    }

                    var newClient = _listener.AcceptTcpClient();
                    var newClientAddress = ((IPEndPoint?)newClient.Client.RemoteEndPoint)?.ToString() ?? "unknown";

                    lock (_clientLock)
                    {
                        // Reject new connections if a client is already connected
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

                    // Check if serial port is in use by web client before attempting to open
                    if (IsSerialPortInUse?.Invoke() == true)
                    {
                        RaiseInfo("Rejected: serial port in use by web client");
                        SendMessage(newClient, Constants.ProxySerialPortInUse);
                        lock (_clientLock) { CloseClientUnlocked(); }
                        continue;
                    }

                    // Open serial port now that a client is connected
                    try
                    {
                        _serialPort = new SerialPort(SerialPortName, BaudRate)
                        {
                            ReadTimeout = Constants.SerialReadTimeoutMs,
                            WriteTimeout = Constants.SerialWriteTimeoutMs
                        };

                        // Open with timeout to avoid hanging if port is stuck
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

                    // Start forwarding threads
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

                    // Wait for forwarding threads to finish (client disconnect)
                    _serialToTcpThread.Join();
                    _tcpToSerialThread.Join();

                    // Close serial port when client disconnects
                    try
                    {
                        _serialPort?.Close();
                        _serialPort?.Dispose();
                        RaiseInfo("Closed serial port");
                    }
                    catch
                    {
                        // Ignore close errors
                    }
                    _serialPort = null;
                }
                catch (SocketException) when (_cts?.IsCancellationRequested == true)
                {
                    // Expected during shutdown
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
        /// Thread that forwards data from serial port to TCP client.
        /// </summary>
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

                    // Check if client is still connected
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
                    // Normal - serial read timeout
                }
                catch (IOException)
                {
                    // Serial port or network disconnected
                    break;
                }
                catch (InvalidOperationException)
                {
                    // Port closed
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

        /// <summary>
        /// Thread that forwards data from TCP client to serial port.
        /// </summary>
        private void TcpToSerialLoop()
        {
            var buffer = new byte[Constants.ProxyBufferSize];
            long lastHeartbeatCheck = Environment.TickCount64;

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

                    // Use Socket.Poll to check for data or disconnection
                    // Poll returns true if: connection closed, data available, or error
                    var socket = client.Client;
                    if (socket.Poll(Constants.SocketPollTimeoutMicroseconds, SelectMode.SelectRead))
                    {
                        if (socket.Available == 0)
                        {
                            // Poll returned true but no data = connection closed
                            RaiseInfo("Client connection closed");
                            break;
                        }

                        int count = stream.Read(buffer, 0, buffer.Length);
                        if (count == 0)
                        {
                            // Client disconnected gracefully
                            RaiseInfo("Client disconnected gracefully");
                            break;
                        }

                        // Update activity tracking - client is alive
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
                        // Poll timed out - check if we need to send a heartbeat
                        long now = Environment.TickCount64;

                        if (now - _lastClientActivityMs >= ClientSilenceIntervalMs
                            && now - lastHeartbeatCheck >= ClientSilenceIntervalMs)
                        {
                            lastHeartbeatCheck = now;
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
                    // Client disconnected
                    break;
                }
                catch (SocketException)
                {
                    // Socket error (disconnection)
                    break;
                }
                catch (ObjectDisposedException)
                {
                    // Stream closed
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

            // Client disconnected - clean up
            HandleClientDisconnect();
        }

        /// <summary>
        /// Handles client disconnection. Sends feed hold to stop any in-progress movement.
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

        /// <summary>
        /// Sends a message to a client. Does not close the connection.
        /// </summary>
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
                // Ignore write errors
            }
        }

        /// <summary>
        /// Sends a message to a client and closes the connection.
        /// Used for rejection messages before the client is fully accepted.
        /// </summary>
        private static void SendMessageAndClose(TcpClient client, string message)
        {
            SendMessage(client, message);
            try
            {
                client.Close();
            }
            catch
            {
                // Ignore close errors
            }
        }

        /// <summary>
        /// Closes the current client connection.
        /// </summary>
        private void CloseClient()
        {
            lock (_clientLock)
            {
                CloseClientUnlocked();
            }
        }

        /// <summary>
        /// Closes the current client connection (must hold _clientLock).
        /// </summary>
        private void CloseClientUnlocked()
        {
            try
            {
                _networkStream?.Close();
                _client?.Close();
            }
            catch
            {
                // Ignore errors during close
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
