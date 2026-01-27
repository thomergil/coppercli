#nullable enable

using System.IO.Ports;
using System.Net;
using System.Net.Sockets;

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
        private const int BufferSize = 4096;
        private const int ThreadSleepMs = 1;
        private const int HeartbeatIntervalMs = 10000;  // Send status query every 10s of inactivity
        private const int MaxMissedHeartbeats = 3;      // Disconnect after 3 missed heartbeats (30s)
        private const byte GrblStatusQuery = (byte)'?';

        // =========================================================================
        // Events
        // =========================================================================
        public event Action<string>? Info;
        public event Action<string>? Error;
        public event Action? ClientConnected;
        public event Action? ClientDisconnected;

        // =========================================================================
        // Public state properties
        // =========================================================================
        public bool IsRunning { get; private set; }
        public bool HasClient { get; private set; }
        public string? ClientAddress { get; private set; }
        public int TcpPort { get; private set; }
        public string SerialPortName { get; private set; } = string.Empty;
        public int BaudRate { get; private set; }

        // =========================================================================
        // Statistics
        // =========================================================================
        public long BytesFromClient { get; private set; }
        public long BytesToClient { get; private set; }
        public DateTime? ClientConnectedTime { get; private set; }

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
        private DateTime _lastClientActivity;
        private int _missedHeartbeats;

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
                // Open serial port
                _serialPort = new SerialPort(serialPort, baudRate)
                {
                    ReadTimeout = 100,
                    WriteTimeout = 1000
                };
                _serialPort.Open();
                RaiseInfo($"Opened serial port {serialPort} @ {baudRate}");

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
                _serialPort?.Close();
                _serialPort?.Dispose();
                _serialPort = null;
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
            _acceptThread?.Join(1000);
            _serialToTcpThread?.Join(1000);
            _tcpToSerialThread?.Join(1000);

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
        /// Thread that accepts incoming TCP connections.
        /// </summary>
        private void AcceptLoop()
        {
            while (_cts != null && !_cts.IsCancellationRequested)
            {
                try
                {
                    if (_listener == null)
                    {
                        break;
                    }

                    // Check for pending connection with timeout
                    if (!_listener.Pending())
                    {
                        Thread.Sleep(100);
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
                            try
                            {
                                newClient.Close();
                            }
                            catch
                            {
                                // Ignore close errors
                            }
                            continue;
                        }

                        _client = newClient;
                        _networkStream = _client.GetStream();
                        ClientAddress = newClientAddress;
                        ClientConnectedTime = DateTime.Now;
                        HasClient = true;
                        BytesFromClient = 0;
                        BytesToClient = 0;
                        _lastClientActivity = DateTime.Now;
                        _missedHeartbeats = 0;
                    }

                    RaiseInfo($"Client connected: {ClientAddress}");
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
            var buffer = new byte[BufferSize];

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
                        Thread.Sleep(ThreadSleepMs);
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
            var buffer = new byte[BufferSize];
            var lastHeartbeatCheck = DateTime.Now;

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
                    if (socket.Poll(100000, SelectMode.SelectRead)) // 100ms timeout
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
                        _lastClientActivity = DateTime.Now;
                        _missedHeartbeats = 0;

                        if (_serialPort != null && _serialPort.IsOpen)
                        {
                            _serialPort.Write(buffer, 0, count);
                            BytesFromClient += count;
                        }
                    }
                    else
                    {
                        // Poll timed out - check if we need to send a heartbeat
                        var now = DateTime.Now;
                        var idleTime = now - _lastClientActivity;

                        if (idleTime.TotalMilliseconds >= HeartbeatIntervalMs)
                        {
                            // Only check once per heartbeat interval
                            if ((now - lastHeartbeatCheck).TotalMilliseconds >= HeartbeatIntervalMs)
                            {
                                lastHeartbeatCheck = now;
                                _missedHeartbeats++;

                                if (_missedHeartbeats >= MaxMissedHeartbeats)
                                {
                                    RaiseInfo($"Client timeout ({_missedHeartbeats} missed heartbeats)");
                                    break;
                                }

                                // Send status query to keep connection alive and verify client
                                if (_serialPort != null && _serialPort.IsOpen)
                                {
                                    _serialPort.Write(new[] { GrblStatusQuery }, 0, 1);
                                }
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
        /// Handles client disconnection.
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
            ClientDisconnected?.Invoke();
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
            HasClient = false;
            ClientAddress = null;
            ClientConnectedTime = null;
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
