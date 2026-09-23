#nullable enable
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using coppercli.WebServer;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// MachineHold decides who has the serial port. If it reconnects while a terminal has the
    /// port, the server and the terminal both write to the controller; if it never reconnects,
    /// the browser shows a disconnected machine with nothing that will reconnect it.
    /// </summary>
    [Collection(TimingSensitiveCollection.Name)]
    public class MachineHoldTests
    {
        // The window spans several reconnect intervals, so the loop checks it more than once
        // before it expires and a test can tell a reconnect inside it from one after it.
        private const int TakeoverWindowMs = 200;
        private const int ReconnectIntervalMs = 30;

        // For a test that must still be inside the window when it acts or checks; a slow
        // machine can take longer than TakeoverWindowMs between two lines of a test.
        private const int WindowOutlastingTheTestMs = 60_000;

        // Long enough for the loop to run several times, to show something did not happen.
        private const int QuietPeriodMs = ReconnectIntervalMs * 4;

        // Far above what any case needs, so a hold that never acts fails on its reason
        // instead of hanging the suite.
        private const int WaitTimeoutMs = 3000;

        // A first connect on a real serial port can take seconds; this stands in for one.
        private const int SlowConnectMs = 2000;

        // Well under SlowConnectMs, so a caller that is not blocked is plainly not blocked.
        private const int CallerReturnBudgetMs = 300;

        /// <summary>
        /// A connection double shared by the loop's thread and the test. Connect sets
        /// Connected, or throws for the first FailuresRemaining calls, as the server's connect
        /// does on failure. Gate, when set, holds Connect open until the test releases it.
        /// </summary>
        private sealed class FakeConnection
        {
            private volatile bool _connected;
            private int _connectAttempts;

            public bool Connected
            {
                get => _connected;
                set => _connected = value;
            }

            public int ConnectAttempts => Volatile.Read(ref _connectAttempts);
            public volatile int FailuresRemaining;
            public volatile int ConnectDelayMs;
            public ManualResetEventSlim? Gate;
            public readonly ManualResetEventSlim ConnectEntered = new(false);

            public void Connect()
            {
                Interlocked.Increment(ref _connectAttempts);
                if (FailuresRemaining > 0)
                {
                    FailuresRemaining--;
                    throw new InvalidOperationException("FakeConnection: simulated connect failure");
                }

                ConnectEntered.Set();
                Gate?.Wait(WaitTimeoutMs);
                if (ConnectDelayMs > 0)
                {
                    Thread.Sleep(ConnectDelayMs);
                }

                _connected = true;
            }

            public void Disconnect() => _connected = false;
        }

        /// <summary>A hold with its loop running, canceled when the test ends.</summary>
        private sealed class RunningHold : IAsyncDisposable
        {
            private readonly CancellationTokenSource _cts = new();
            private readonly Task _loop;

            public RunningHold(FakeConnection connection, int takeoverWindowMs = TakeoverWindowMs)
            {
                Connection = connection;
                Hold = new MachineHold(() => connection.Connected, connection.Connect,
                    connection.Disconnect, takeoverWindowMs, ReconnectIntervalMs);
                _loop = Hold.KeepConnectedAsync(_cts.Token);
            }

            public FakeConnection Connection { get; }
            public MachineHold Hold { get; }

            public Task WaitConnectedAsync(string what) =>
                AsyncWait.WaitUntilAsync(() => Connection.Connected, what, WaitTimeoutMs);

            public Task StaysDisconnectedAsync(string because) =>
                AsyncWait.AssertStaysTrueAsync(() => !Connection.Connected, QuietPeriodMs, because);

            public async ValueTask DisposeAsync()
            {
                Connection.Gate?.Set();
                _cts.Cancel();
                await _loop;
            }
        }

        private static async Task<RunningHold> ConnectedHoldAsync(int takeoverWindowMs = TakeoverWindowMs)
        {
            var running = new RunningHold(new FakeConnection(), takeoverWindowMs);
            await running.WaitConnectedAsync("the hold to connect at start");
            return running;
        }

        /// <summary>
        /// Without a connect at start, the operator opens the browser on a machine the server
        /// never connected.
        /// </summary>
        [Fact]
        public async Task AMachineNotYetConnected_IsConnectedAsSoonAsTheLoopStarts()
        {
            await using var running = await ConnectedHoldAsync();
            Assert.True(running.Hold.IsHeld);
        }

        /// <summary>
        /// The server starts the loop before it serves any page. A loop that ran its first
        /// connect on the caller's thread would hold the whole server back for a slow port.
        /// </summary>
        [Fact]
        public async Task ASlowFirstConnect_DoesNotBlockTheCallerOfKeepConnectedAsync()
        {
            var connection = new FakeConnection { ConnectDelayMs = SlowConnectMs };

            var stopwatch = Stopwatch.StartNew();
            await using var running = new RunningHold(connection);
            long returnedAfterMs = stopwatch.ElapsedMilliseconds;

            Assert.True(returnedAfterMs < CallerReturnBudgetMs,
                $"KeepConnectedAsync held its caller for {returnedAfterMs}ms");
            await AsyncWait.WaitUntilAsync(() => connection.Connected, "the slow connect to finish",
                SlowConnectMs + WaitTimeoutMs);
        }

        /// <summary>
        /// A cable pulled and put back must not leave the machine disconnected until someone
        /// reconnects it by hand.
        /// </summary>
        [Fact]
        public async Task ADroppedConnection_IsReconnectedWhileHeld()
        {
            await using var running = await ConnectedHoldAsync();

            running.Connection.Connected = false;

            await running.WaitConnectedAsync("the hold to reconnect after the connection dropped");
        }

        /// <summary>A controller that is off when the server starts is connected once it is on.</summary>
        [Fact]
        public async Task AConnectThatThrows_IsRetriedUntilItSucceeds()
        {
            var connection = new FakeConnection { FailuresRemaining = 3 };
            await using var running = new RunningHold(connection);

            await running.WaitConnectedAsync("the hold to connect once the failures ran out");
            Assert.True(connection.ConnectAttempts > 3, "a failed connect was not retried");
        }

        /// <summary>
        /// A terminal needs the port the moment it asks; a reconnect inside the window would
        /// open the port for the server while the terminal is still connecting through the
        /// proxy.
        /// </summary>
        [Fact]
        public async Task AYield_DisconnectsAtOnce_AndNoReconnectDuringTheTakeoverWindow()
        {
            await using var running = await ConnectedHoldAsync(WindowOutlastingTheTestMs);

            Assert.True(running.Hold.TryYieldToTerminal(() => false));

            Assert.False(running.Connection.Connected, "the yield did not disconnect");
            Assert.False(running.Hold.IsHeld);
            await running.StaysDisconnectedAsync("the server reconnected inside the takeover window");
        }

        /// <summary>
        /// A takeover refused because the machine is in use must leave everything as it was,
        /// or the running job loses its connection anyway.
        /// </summary>
        [Fact]
        public async Task AYield_IsRefusedWhileTheMachineIsInUse_AndChangesNothing()
        {
            await using var running = await ConnectedHoldAsync();

            Assert.False(running.Hold.TryYieldToTerminal(() => true));

            Assert.True(running.Hold.IsHeld, "a refused takeover left the machine yielded");
            await AsyncWait.AssertStaysTrueAsync(() => running.Connection.Connected, QuietPeriodMs,
                "a refused takeover disconnected the machine");
        }

        /// <summary>
        /// The busy check and the yield happen under one lock, so nothing reading the hold
        /// meanwhile, the connect loop included, sees a yield that is then refused. Seen, the
        /// loop disconnects the running job the refusal was meant to protect.
        /// </summary>
        [Fact]
        public async Task ARefusedTakeover_IsNeverSeenAsAYield()
        {
            await using var running = await ConnectedHoldAsync();
            using var checkStarted = new ManualResetEventSlim(false);
            bool sawYield = false;

            var reader = Task.Run(async () =>
            {
                checkStarted.Wait(WaitTimeoutMs);
                var stopwatch = Stopwatch.StartNew();
                while (stopwatch.ElapsedMilliseconds < QuietPeriodMs)
                {
                    sawYield |= !running.Hold.IsHeld;
                    await Task.Delay(1);
                }
            });

            Assert.False(running.Hold.TryYieldToTerminal(() =>
            {
                checkStarted.Set();
                Thread.Sleep(QuietPeriodMs);
                return true;
            }));
            await reader;

            Assert.False(sawYield, "a reader saw the yield of a takeover that was refused");
            Assert.True(running.Connection.Connected, "a refused takeover disconnected the machine");
        }

        /// <summary>
        /// A terminal that asks and never attaches must not leave the server off the machine
        /// for good.
        /// </summary>
        [Fact]
        public async Task ATerminalThatNeverClaimsThePort_GetsTheMachineBackAfterTheWindow()
        {
            await using var running = await ConnectedHoldAsync();

            running.Hold.TryYieldToTerminal(() => false);

            await running.WaitConnectedAsync("the server to reconnect after the takeover window");
            Assert.True(running.Hold.IsHeld);
            Assert.False(running.Hold.TryClaimSerialPort(),
                "a terminal could still claim the port after the server reconnected");
        }

        /// <summary>
        /// The proxy may open the port only while the server has let go of it; any other
        /// answer puts two writers on the controller.
        /// </summary>
        [Fact]
        public async Task TheClaim_IsGrantedOnlyWhileYieldedAndDisconnected()
        {
            await using var running = await ConnectedHoldAsync(WindowOutlastingTheTestMs);

            Assert.False(running.Hold.TryClaimSerialPort(), "the port was lent while the server held it");

            running.Hold.TryYieldToTerminal(() => false);

            Assert.True(running.Hold.TryClaimSerialPort(), "the port was refused during the takeover");
        }

        /// <summary>
        /// A grant stands until its release. A second grant would let one terminal's release
        /// end another's claim, and the server would reconnect under it.
        /// </summary>
        [Fact]
        public async Task ASecondClaim_IsRefusedWhileTheFirstStands()
        {
            await using var running = await ConnectedHoldAsync(WindowOutlastingTheTestMs);
            running.Hold.TryYieldToTerminal(() => false);
            Assert.True(running.Hold.TryClaimSerialPort());

            Assert.False(running.Hold.TryClaimSerialPort(),
                "a second terminal was lent the port while the first still has it");
        }

        /// <summary>
        /// While a terminal has the port the server must stay off it, past the window and
        /// even after a browser reclaims the machine, until the proxy has stopped the machine
        /// and closed the port.
        /// </summary>
        [Fact]
        public async Task ATerminalWithThePort_KeepsTheServerOffUntilThePortIsReleased()
        {
            await using var running = await ConnectedHoldAsync();
            running.Hold.TryYieldToTerminal(() => false);
            Assert.True(running.Hold.TryClaimSerialPort());

            await AsyncWait.AssertStaysTrueAsync(() => !running.Connection.Connected,
                TakeoverWindowMs + QuietPeriodMs, "the server reconnected past the window while a terminal had the port");

            running.Hold.Reclaim();
            await running.StaysDisconnectedAsync("the server reconnected after a reclaim while the proxy still had the port");

            running.Hold.ReleaseSerialPort();

            await running.WaitConnectedAsync("the server to reconnect once the proxy released the port");
            Assert.False(running.Hold.TryClaimSerialPort(),
                "a terminal could claim the port again without asking for the machine");
        }

        /// <summary>
        /// A takeover that arrives while a reconnect is opening the port must still end with
        /// the machine disconnected, and must not wait for that connect, which can take
        /// seconds on a real port.
        /// </summary>
        [Fact]
        public async Task AYieldArrivingWhileAConnectIsInFlight_EndsDisconnectedAndNotHeld()
        {
            var connection = new FakeConnection { Gate = new ManualResetEventSlim(false) };
            await using var running = new RunningHold(connection, WindowOutlastingTheTestMs);
            Assert.True(connection.ConnectEntered.Wait(WaitTimeoutMs), "the connect never started");

            var stopwatch = Stopwatch.StartNew();
            Assert.True(running.Hold.TryYieldToTerminal(() => false));
            Assert.True(stopwatch.ElapsedMilliseconds < CallerReturnBudgetMs, "the yield waited for the connect");

            connection.Gate.Set();

            // The late connect is connected for a moment before the hold disconnects it.
            await AsyncWait.WaitUntilAsync(() => !connection.Connected,
                "the connect that finished during the yield to be disconnected", WaitTimeoutMs);
            await running.StaysDisconnectedAsync("the connect that finished during the yield reconnected");
            Assert.False(running.Hold.IsHeld, "the connect that finished during the yield cleared it");
        }

        /// <summary>
        /// Shutdown calls Stop while a reconnect may be opening the port, then the terminal
        /// connects the same machine itself. Stop must wait for that connect and disconnect
        /// it, or the late connect and the terminal's own connect overlap.
        /// </summary>
        [Fact]
        public async Task Stop_DuringAConnect_WaitsForIt_AndLeavesTheMachineDisconnected()
        {
            var connection = new FakeConnection { Gate = new ManualResetEventSlim(false) };
            await using var running = new RunningHold(connection);
            Assert.True(connection.ConnectEntered.Wait(WaitTimeoutMs), "the connect never started");

            var stop = Task.Run(running.Hold.Stop);
            await Task.Delay(QuietPeriodMs);
            Assert.False(stop.IsCompleted, "Stop returned while the connect was still running");

            connection.Gate.Set();
            await stop;

            Assert.False(connection.Connected, "Stop returned with the late connect left connected");
            await running.StaysDisconnectedAsync("the loop reconnected after Stop");
        }

        /// <summary>
        /// Shutdown stops the hold before its loop has ended, so the loop must not reconnect
        /// in between.
        /// </summary>
        [Fact]
        public async Task Stop_Disconnects_AndTheLoopDoesNotReconnect()
        {
            await using var running = await ConnectedHoldAsync();

            running.Hold.Stop();

            Assert.False(running.Connection.Connected, "Stop did not disconnect");
            await running.StaysDisconnectedAsync("the loop reconnected after Stop");
            Assert.False(running.Hold.TryClaimSerialPort(), "the port was lent by a stopped server");
        }

        /// <summary>Shutdown waits on the loop; one that ignored cancellation would hang it.</summary>
        [Fact]
        public async Task Canceling_EndsTheLoop()
        {
            var connection = new FakeConnection();
            var hold = new MachineHold(() => connection.Connected, connection.Connect,
                connection.Disconnect, TakeoverWindowMs, ReconnectIntervalMs);
            using var cts = new CancellationTokenSource();
            var loop = hold.KeepConnectedAsync(cts.Token);

            cts.Cancel();

            Assert.Same(loop, await Task.WhenAny(loop, Task.Delay(WaitTimeoutMs)));
        }
    }
}
