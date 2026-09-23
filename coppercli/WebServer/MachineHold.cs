using System;
using System.Threading;
using System.Threading.Tasks;
using coppercli.Helpers;
using static coppercli.WebServer.WebConstants;

namespace coppercli.WebServer;

/// <summary>
/// Who has the serial port: the server, which holds the machine connection for as long as it
/// runs, or a terminal on another computer that took the machine over through the proxy. A
/// disconnect makes coppercli forget homing and work zero, so only a terminal's takeover
/// disconnects the server. It reconnects once the proxy returns the port, or after
/// <see cref="TerminalTakeoverWindowMs"/> if no terminal claims it.
/// </summary>
internal sealed class MachineHold
{
    private readonly Func<bool> _isConnected;
    private readonly Action _connect;
    private readonly Action _disconnect;
    private readonly int _takeoverWindowMs;
    private readonly int _reconnectIntervalMs;

    // Guards _yieldedAtMs, _yieldCount and _terminalHasPort. Never held while connecting,
    // which can take seconds.
    private readonly object _yieldLock = new();

    // Held for every connect and disconnect, and while the proxy claims the port, so the two
    // never both open it.
    private readonly object _connectionLock = new();

    // The Environment.TickCount64 at which a terminal asked for the machine, or 0 once the
    // server has taken it back.
    private long _yieldedAtMs;

    // Counts yields, so a connect ends only the yield it saw expire, not one that arrived
    // while it was connecting.
    private long _yieldCount;

    // From the proxy's claim until it returns the port after its safety stop.
    private bool _terminalHasPort;

    private volatile bool _stopped;

    // True while a connect runs. A yield does not wait for one; the connect disconnects when
    // it finishes.
    private volatile bool _connecting;

    private string? _lastConnectFailure;

    /// <param name="connect">Opens the connection, and throws with the reason when it cannot.</param>
    public MachineHold(
        Func<bool> isConnected,
        Action connect,
        Action disconnect,
        int takeoverWindowMs = TerminalTakeoverWindowMs,
        int reconnectIntervalMs = ReconnectIntervalMs)
    {
        _isConnected = isConnected;
        _connect = connect;
        _disconnect = disconnect;
        _takeoverWindowMs = takeoverWindowMs;
        _reconnectIntervalMs = reconnectIntervalMs;
    }

    /// <summary>
    /// True while the server should have the machine: no terminal has the port, and no
    /// takeover is waiting for one, which lasts until <see cref="TerminalTakeoverWindowMs"/>
    /// passes or a browser reclaims the machine.
    /// </summary>
    public bool IsHeld
    {
        get
        {
            lock (_yieldLock)
            {
                return IsHeldUnlocked();
            }
        }
    }

    /// <summary>
    /// A terminal asked for the machine and no browser has taken it back since. The proxy may
    /// still be closing the port after a browser took it back; that is not a yield.
    /// </summary>
    public bool IsYieldedToTerminal
    {
        get
        {
            lock (_yieldLock)
            {
                return _yieldedAtMs != 0;
            }
        }
    }

    /// <summary>
    /// A terminal asked for the machine: refused while <paramref name="machineInUse"/> is
    /// true, with nothing changed. Otherwise sets the yield and disconnects, or leaves a
    /// connect under way to disconnect when it finishes.
    /// </summary>
    /// <param name="machineInUse">Checked under the same lock that sets the yield, so the
    /// loop never sees a yield that is then refused.</param>
    /// <returns>False when refused.</returns>
    public bool TryYieldToTerminal(Func<bool> machineInUse)
    {
        lock (_yieldLock)
        {
            if (machineInUse())
            {
                return false;
            }

            _yieldedAtMs = Environment.TickCount64;
            _yieldCount++;
        }

        DisconnectUnlessConnecting();
        Logger.Log("MachineHold: yielded to a terminal");
        return true;
    }

    /// <summary>
    /// A browser took the machine back: ends a yield at once. The server reconnects when the
    /// proxy returns the port, if a terminal had it.
    /// </summary>
    public void Reclaim()
    {
        lock (_yieldLock)
        {
            _yieldedAtMs = 0;
        }
    }

    /// <summary>
    /// The proxy asks before it admits a terminal. Granted only while the server has yielded,
    /// is disconnected, and has not lent the port already; a grant stands until
    /// <see cref="ReleaseSerialPort"/>.
    /// </summary>
    public bool TryClaimSerialPort()
    {
        lock (_connectionLock)
        {
            lock (_yieldLock)
            {
                if (_stopped || _terminalHasPort || IsHeldUnlocked() || _isConnected())
                {
                    return false;
                }

                _terminalHasPort = true;
                return true;
            }
        }
    }

    /// <summary>The proxy has stopped the machine and closed the port behind its terminal.</summary>
    public void ReleaseSerialPort()
    {
        lock (_yieldLock)
        {
            _terminalHasPort = false;
        }
    }

    /// <summary>
    /// Disconnects for good, waiting for a connect under way to finish first, so the caller
    /// can connect the machine itself once this returns. Call after canceling
    /// <see cref="KeepConnectedAsync"/>.
    /// </summary>
    public void Stop()
    {
        lock (_connectionLock)
        {
            _stopped = true;
            if (_isConnected())
            {
                _disconnect();
            }
        }
    }

    /// <summary>
    /// Keeps the connection in line with <see cref="IsHeld"/>, connecting or disconnecting,
    /// until canceled. Returns to its caller before the first connect, which can take seconds.
    /// </summary>
    public async Task KeepConnectedAsync(CancellationToken ct)
    {
        await Task.Yield();

        while (!ct.IsCancellationRequested)
        {
            // Nothing else reconnects the machine, so one failure must not end the loop.
            try
            {
                Settle();
            }
            catch (Exception ex)
            {
                Logger.Log("MachineHold: settling the connection failed - {0}", ex);
            }

            try
            {
                await Task.Delay(_reconnectIntervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private bool IsHeldUnlocked() =>
        !_terminalHasPort
        && (_yieldedAtMs == 0 || Environment.TickCount64 - _yieldedAtMs >= _takeoverWindowMs);

    /// <summary>
    /// A connect under way checks for a yield when it finishes and disconnects then, so this
    /// does not wait for it, which can take seconds. Any other holder of the lock is brief.
    /// </summary>
    private void DisconnectUnlessConnecting()
    {
        if (_connecting)
        {
            return;
        }

        lock (_connectionLock)
        {
            if (_isConnected())
            {
                _disconnect();
            }
        }
    }

    private void Settle()
    {
        lock (_connectionLock)
        {
            bool held;
            long yieldCount;
            lock (_yieldLock)
            {
                held = !_stopped && IsHeldUnlocked();
                yieldCount = _yieldCount;
            }

            if (!held)
            {
                if (_isConnected())
                {
                    _disconnect();
                }
                return;
            }

            if (_isConnected() || !TryConnect())
            {
                return;
            }

            bool stillHeld;
            lock (_yieldLock)
            {
                // A yield or a stop that arrived during the connect stands.
                stillHeld = !_stopped && _yieldCount == yieldCount;
                if (stillHeld)
                {
                    _yieldedAtMs = 0;
                }
            }

            if (!stillHeld)
            {
                _disconnect();
            }
        }
    }

    /// <summary>
    /// Logs a failure only when its reason changes, so a machine left off overnight does not
    /// fill the log.
    /// </summary>
    private bool TryConnect()
    {
        _connecting = true;
        try
        {
            _connect();
        }
        catch (Exception ex)
        {
            if (ex.Message != _lastConnectFailure)
            {
                Logger.Log("MachineHold: connect failed, retrying every {0}ms - {1}",
                    _reconnectIntervalMs, ex);
                _lastConnectFailure = ex.Message;
            }
            return false;
        }
        finally
        {
            _connecting = false;
        }

        _lastConnectFailure = null;
        Logger.Log("MachineHold: machine connected");
        return true;
    }
}
