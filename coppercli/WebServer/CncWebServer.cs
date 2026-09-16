using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using coppercli.Core.Communication;
using coppercli.Core.Controllers;
using coppercli.Core.GCode;
using coppercli.Core.Util;
using coppercli.Helpers;
using Spectre.Console;
using static coppercli.CliConstants;
using static coppercli.Core.Util.Constants;
using static coppercli.Core.Util.GrblProtocol;
using static coppercli.WebServer.WebConstants;

namespace coppercli.WebServer;

/// <summary>
/// Embedded web server for browser-based CNC control.
/// Serves static files and provides WebSocket API for real-time communication.
/// </summary>
public static class CncWebServer
{
    private static HttpListener? _listener;
    private static CancellationTokenSource? _cts;

    /// <summary>One connected browser.</summary>
    private sealed class ClientConnection
    {
        public required WebSocket Socket { get; init; }

        /// <summary>The browser's own id from its cookie, or null if it sent none.</summary>
        public string? Id { get; init; }

        /// <summary>Last heard from. Read and written under <see cref="_clientsLock"/>.</summary>
        public DateTime LastActivity { get; set; }

        /// <summary>
        /// A WebSocket takes one send at a time, and several threads write to this one. Not
        /// disposed when the client goes: a send still queued behind it would throw, and
        /// nothing here takes the wait handle that disposing would release.
        /// </summary>
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }

    private static readonly List<ClientConnection> _clients = new();
    // Track clients that have been served the page but haven't connected WebSocket yet
    private static readonly Dictionary<string, DateTime> _pendingClients = new();
    private static readonly object _clientsLock = new();
    private const int WebSocketTimeoutMs = 30000;  // 30 seconds without activity = stale
    private const string ClientIdCookieName = "coppercli_client_id";
    private static Machine? _machine;

    /// <summary>
    /// True when a machine object exists and is connected. Single source of truth for the
    /// "is the machine usable" guard shared across every web handler. The MemberNotNullWhen
    /// attribute lets callers write <c>if (!MachineConnected) return;</c> and still have the
    /// compiler treat <c>_machine</c> as non-null afterward.
    /// </summary>
    [MemberNotNullWhen(true, nameof(_machine))]
    private static bool MachineConnected => _machine != null && _machine.Connected;

    private static string _serialPort = "";
    private static int _baudRate = Constants.DefaultBaudRate;
    private static bool _isReconnecting = false;
    private static bool _forceDisconnected = false;  // Suppress auto-reconnect after force disconnect
    private static readonly object _reconnectLock = new();

    // Milling controller cancellation (for stopping operations). Only ever assigned a
    // fresh instance synchronously in HandleMillStart, before the run that uses it is
    // scheduled - see the compare-and-clear remarks on that assignment.
    private static CancellationTokenSource? _millCts;

    // The Task backing the in-flight controller.StartAsync() started by HandleMillStart,
    // so StopMillingAsync can wait for that run's own cancellation-driven cleanup instead
    // of racing it with a second, independent StopAsync/Reset.
    private static Task? _millRunTask;

    // Serializes StopMillingAsync so only one caller drives the milling controller's FSM
    // at a time: the combined stop path (HandleMillStopAsync) and a tool change that ends
    // without success on its own can both reach it around the same moment.
    private static readonly SemaphoreSlim _millStopLock = new(1, 1);

    // Tool change controller cancellation and pending user input. _toolChangeCts is only
    // ever assigned a fresh instance synchronously in HandleMillStart's onToolChange
    // callback, before the run that uses it is scheduled.
    private static CancellationTokenSource? _toolChangeCts;

    // The Task backing the in-flight StartToolChangeControllerAsync started when M6 is
    // detected, so HandleMillStopAsync can wait for that run's own cancellation-driven
    // cleanup (including the Reset back to Idle it performs) instead of racing it with a
    // second, independent Reset() from here.
    private static Task? _toolChangeRunTask;

    // Serializes HandleMillStopAsync, the single entry point for an operator-initiated
    // stop (the Stop button and the tool-change dialog's Abort button both funnel
    // through it - see its remarks), so two concurrent stop/abort requests do not both
    // try to drive both controllers' teardown at once.
    private static readonly SemaphoreSlim _toolChangeAbortLock = new(1, 1);

    // The in-flight probe controller run - a grid probe or an outline trace, which drive the
    // one controller and so can never both be running. Released by that run's own finally
    // under a compare-and-clear (see ReleaseProbeRunAsync), not disposed: a stop whose bounded
    // wait times out leaves the run still holding this token.
    private static CancellationTokenSource? _probeCts;
    private static Task? _probeTask;

    // Idle disconnect timer - disconnects Machine if no clients after operation completes
    private static CancellationTokenSource? _idleDisconnectCts;

    // Track the connected web client's address
    private static string? _webClientAddress;

    /// <summary>
    /// Optional callback to force-disconnect the proxy's current client (TUI).
    /// Set by ServerMenu.RunServer() to wire up to SerialProxy.ForceDisconnectClient().
    /// </summary>
    public static Func<bool>? ForceDisconnectProxyClient { get; set; }

    /// <summary>
    /// Optional callback to check if proxy has a connected client (TUI).
    /// Set by ServerMenu.RunServer() to wire up to SerialProxy.HasClient.
    /// </summary>
    public static Func<bool>? HasProxyClient { get; set; }

    /// <summary>
    /// Returns true if a WebSocket client is connected.
    /// Only one web client is allowed at a time.
    /// </summary>
    public static bool HasWebClient
    {
        get
        {
            lock (_clientsLock)
            {
                return _clients.Count > 0;
            }
        }
    }

    /// <summary>
    /// Returns the address of the connected web client, or null if none.
    /// </summary>
    public static string? WebClientAddress
    {
        get
        {
            lock (_clientsLock)
            {
                return _webClientAddress;
            }
        }
    }

    /// <summary>
    /// Runs the web server on the specified port.
    /// Blocks until Ctrl+C or exit signal.
    /// </summary>
    /// <param name="port">HTTP port to listen on.</param>
    /// <param name="serialPort">Serial port name for display.</param>
    /// <param name="baudRate">Baud rate for display.</param>
    /// <param name="startedSignal">Optional signal to set when server is ready.</param>
    public static void Run(int port, string serialPort, int baudRate, ManualResetEvent? startedSignal = null)
    {
        Logger.Log("CncWebServer.Run: starting on port {0}", port);
        _serialPort = serialPort;
        _baudRate = baudRate;
        _machine = AppState.Machine;
        _cts = new CancellationTokenSource();

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{port}/");
        Logger.Log("CncWebServer.Run: HttpListener created");

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            // Try localhost only if binding to all interfaces fails
            AnsiConsole.MarkupLine($"[{ColorWarning}]Could not bind to all interfaces: {ex.Message}[/]");
            AnsiConsole.MarkupLine($"[{ColorDim}]Trying localhost only...[/]");

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
        }

        // Only show connection info if not in server mode (server mode has its own display)
        if (startedSignal == null)
        {
            var localIps = NetworkHelpers.GetLocalIPAddresses();
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[{ColorSuccess}]Web server started[/]");
            AnsiConsole.MarkupLine($"[{ColorDim}]Serial: {_serialPort} @ {_baudRate}[/]");
            AnsiConsole.WriteLine();

            if (localIps.Count > 0)
            {
                AnsiConsole.MarkupLine($"[{ColorInfo}]Open in browser:[/]");
                foreach (var ip in localIps)
                {
                    AnsiConsole.MarkupLine($"  [{ColorSuccess}]http://{ip}:{port}[/]");
                }
            }
            else
            {
                AnsiConsole.MarkupLine($"[{ColorInfo}]Open in browser:[/]");
                AnsiConsole.MarkupLine($"  [{ColorSuccess}]http://localhost:{port}[/]");
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[{ColorDim}]Press Ctrl+C to stop[/]");
        }

        // Handle Ctrl+C only when running standalone (not in server mode)
        // In server mode, MonitorServer handles exit and calls CncWebServer.Stop()
        if (startedSignal == null)
        {
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                _cts.Cancel();
            };
        }

        // Start status broadcast task BEFORE signaling ready
        Logger.Log("CncWebServer.Run: starting BroadcastStatusLoop");
        _ = BroadcastStatusLoop(_cts.Token);

        // Signal that server is ready
        Logger.Log("CncWebServer.Run: signaling ready");
        startedSignal?.Set();

        // Main request loop
        Logger.Log("CncWebServer.Run: entering main request loop");
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var contextTask = _listener.GetContextAsync();
                // Wait for request, checking cancellation periodically
                while (!contextTask.IsCompleted && !_cts.Token.IsCancellationRequested)
                {
                    contextTask.Wait(RequestPollTimeoutMs, _cts.Token);
                }

                if (contextTask.IsCompletedSuccessfully)
                {
                    _ = HandleRequest(contextTask.Result);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            Logger.Log($"Server error: {ex.Message}");
        }
        finally
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[{ColorDim}]Stopping web server...[/]");
            Logger.Log("CncWebServer: shutdown starting");

            // Start a watchdog that forces exit if shutdown hangs
            var shutdownCts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(ShutdownTimeoutMs, shutdownCts.Token);
                    Logger.Log("CncWebServer: shutdown timeout - forcing exit");
                    Environment.Exit(1);
                }
                catch (OperationCanceledException)
                {
                    // Normal - shutdown completed before timeout
                }
            });

            try
            {
                // Cancel first and let the runs unwind while the machine is still reachable:
                // their teardown is what stops it and lifts the tool. A run left going would
                // otherwise send moves to the next connection.
                var running = new[] { _probeTask, _millRunTask, _toolChangeRunTask }
                    .Where(task => task != null)
                    .ToArray();

                _probeCts?.Cancel();
                _millCts?.Cancel();
                _toolChangeCts?.Cancel();

                if (running.Length > 0)
                {
                    Logger.Log("CncWebServer: waiting for {0} run(s) to unwind", running.Length);
                    try
                    {
                        Task.WaitAll(running!, Constants.ControllerCancelTimeoutMs);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log("CncWebServer: a run faulted while unwinding: {0}", ex);
                    }
                }

                Logger.Log("CncWebServer: checking machine connection");
                if (_machine?.Connected == true)
                {
                    Logger.Log("CncWebServer: stopping machine and disconnecting");
                    _machine.Disconnect();
                    Logger.Log("CncWebServer: machine disconnected");
                }

                Logger.Log("CncWebServer: stopping listener");
                _listener.Stop();
                Logger.Log("CncWebServer: listener stopped");

                // Clear static state for clean restart
                Logger.Log("CncWebServer: cancelling idle timer");
                CancelIdleDisconnectTimer();
                Logger.Log("CncWebServer: clearing clients");
                lock (_clientsLock)
                {
                    _clients.Clear();
                    _pendingClients.Clear();
                    _webClientAddress = null;
                }
                Logger.Log("CncWebServer: shutdown complete");
            }
            finally
            {
                shutdownCts.Cancel();
            }
        }
    }

    /// <summary>
    /// Stops the web server if running.
    /// </summary>
    public static void Stop()
    {
        _cts?.Cancel();
    }

    /// <summary>
    /// Starts the idle disconnect timer. Called when an operation completes.
    /// If no browser clients reconnect within the timeout, Machine is disconnected
    /// to free the serial port for TUI clients.
    /// </summary>
    private static void StartIdleDisconnectTimer()
    {
        // Only start if no clients are connected and Machine is connected
        int clientCount;
        lock (_clientsLock)
        {
            clientCount = _clients.Count;
        }

        if (clientCount > 0 || !MachineConnected)
        {
            return;
        }

        // Cancel any existing timer
        _idleDisconnectCts?.Cancel();
        _idleDisconnectCts?.Dispose();
        _idleDisconnectCts = new CancellationTokenSource();

        var token = _idleDisconnectCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                Logger.Log($"No clients connected, starting {IdleDisconnectTimeoutMs / 1000}s idle disconnect timer");
                await Task.Delay(IdleDisconnectTimeoutMs, token);

                // Check again - a client might have connected
                int currentClients;
                lock (_clientsLock)
                {
                    currentClients = _clients.Count;
                }

                if (currentClients == 0 && MachineConnected && !AnyOperationRunning())
                {
                    Logger.Log("Idle disconnect timer expired, disconnecting Machine");
                    _machine.Disconnect();
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Log("Idle disconnect timer cancelled (client reconnected)");
            }
        });
    }

    /// <summary>
    /// Cancels the idle disconnect timer. Called when a browser client connects.
    /// </summary>
    private static void CancelIdleDisconnectTimer()
    {
        if (_idleDisconnectCts != null)
        {
            _idleDisconnectCts.Cancel();
            _idleDisconnectCts.Dispose();
            _idleDisconnectCts = null;
        }
    }

    private static async Task HandleRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            var path = request.Url?.AbsolutePath ?? "/";
            bool isWebSocket = request.IsWebSocketRequest && path == WsPath;
            bool isApi = path.StartsWith(ApiPathPrefix);

            ApplySecurityHeaders(response);

            if (!RequestGuard.IsAllowed(request))
            {
                Logger.Log("Refused {0} {1} from {2}: host={3} origin={4} site={5}",
                    request.HttpMethod, path, request.RemoteEndPoint?.ToString() ?? "unknown",
                    request.UserHostName ?? "none",
                    request.Headers[HeaderOrigin] ?? "none",
                    request.Headers[HeaderSecFetchSite] ?? "none");
                response.StatusCode = HttpStatusForbidden;

                // Answer in the channel the caller used: a refused page load is read by a
                // person, who should see the sentence rather than a JSON envelope.
                if (isWebSocket || isApi)
                {
                    await WriteJson(response, new { error = ErrorForbidden });
                }
                else
                {
                    await WriteText(response, ErrorForbidden);
                }

                return;
            }

            if (isWebSocket)
            {
                // The upgrade takes ownership of the connection; nothing may touch it after.
                await HandleWebSocket(context);
                return;
            }

            try
            {
                if (isApi)
                {
                    await HandleApi(context, path);
                }
                else
                {
                    await ServeStaticFile(context, path);
                }
            }
            catch (Exception ex)
            {
                // Answered here rather than in the outer catch: the close below would
                // otherwise run first on the way out, and writing the failure to a closed
                // response silently turns it into an empty 200 that the UI cannot read.
                Logger.Log("Request handler failed for {0}: {1}", path, ex);
                response.StatusCode = HttpStatusServerError;
                await WriteJson(response, new { error = ErrorServerFailure });
            }
            finally
            {
                // An endpoint reached by a method it does not answer writes nothing at all.
                // Without this the caller waits on a connection that never closes, and a
                // handful of those exhaust a browser's connections to this origin.
                response.Close();
            }
        }
        catch (Exception ex)
        {
            // Nothing was written yet, or writing the failure itself failed. Either way the
            // connection is unusable, so drop it rather than half-answer.
            Logger.Log("Request failed before it could be answered: {0}", ex);
            response.Abort();
        }
    }

    /// <summary>
    /// Headers applied to every response. The web UI drives a machine from large on-screen
    /// buttons, so a page that framed it could sit an invisible copy under the operator's
    /// thumb: inside the frame the UI runs at our own origin, and every request it makes is
    /// genuinely same-origin. Refusing to be framed at all is the only reliable answer.
    /// </summary>
    private static void ApplySecurityHeaders(HttpListenerResponse response)
    {
        response.Headers[HeaderFrameOptions] = FrameOptionsDeny;
        response.Headers[HeaderContentSecurityPolicy] = CspFrameAncestorsNone;
        response.Headers[HeaderContentTypeOptions] = ContentTypeOptionsNoSniff;
        response.Headers[HeaderReferrerPolicy] = ReferrerPolicyNone;
    }

    /// <summary>True if <paramref name="candidate"/> resolves to somewhere inside
    /// <paramref name="root"/>, after any ".." segments are collapsed.</summary>
    private static bool IsContainedIn(string candidate, string root)
    {
        string fullCandidate = Path.GetFullPath(candidate);
        string fullRoot = Path.GetFullPath(root);

        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
        {
            fullRoot += Path.DirectorySeparatorChar;
        }

        return fullCandidate.StartsWith(fullRoot, StringComparison.Ordinal);
    }

    /// <summary>
    /// A command that hands one instruction to the machine and needs nothing else from the
    /// request. An HTTP endpoint and a WebSocket command run the same entry, so they cannot
    /// answer it differently.
    /// </summary>
    /// <param name="Path">The HTTP endpoint that runs it.</param>
    /// <param name="WsCommand">The WebSocket command that runs it, or null for none.</param>
    /// <param name="Run">What it asks of the machine.</param>
    /// <param name="DuringRun">
    /// Whether it may be sent while a workflow is driving the machine. True only for the
    /// controls an operator reaches for because a job is running: stop, hold, resume,
    /// unlock, feed override. Anything that starts a move of its own is false.
    /// </param>
    internal sealed record DirectCommand(
        string Path, string? WsCommand, Action<Machine> Run, bool DuringRun = false);

    private static readonly DirectCommand[] DirectCommands =
    {
        new(ApiHome, WsCmdHome, machine => MachineCommands.HomeAndWait(machine)),
        new(ApiUnlock, WsCmdUnlock, MachineCommands.Unlock, DuringRun: true),
        new(ApiReset, WsCmdReset, machine => machine.SoftReset(), DuringRun: true),
        new(ApiFeedhold, WsCmdFeedhold, machine => machine.FeedHold(), DuringRun: true),
        new(ApiResume, WsCmdResume, machine => machine.CycleStart(), DuringRun: true),

        // X0 Y0, leaving Z where it is - the same as the TUI's key for it.
        new(ApiGotoOrigin, WsCmdGotoOrigin, machine => MachineCommands.GotoWorkOriginXY(machine)),
        new(ApiGotoCenter, WsCmdGotoCenter,
            machine => MachineCommands.GotoFileCenterXY(machine, AppState.CurrentFile)),
        new(ApiGotoSafe, WsCmdGotoSafe,
            machine => MachineCommands.MoveToSafeHeight(machine, Constants.RetractZMm)),
        new(ApiGotoRef, WsCmdGotoRef,
            machine => MachineCommands.MoveToSafeHeight(machine, ReferenceZHeightMm)),
        new(ApiGotoZ0, WsCmdGotoZ0, machine => MachineCommands.MoveToSafeHeight(machine, 0)),
        new(ApiProbeZ, WsCmdProbeZ, _ => ProbeZSingle()),

        // Feed override has no WebSocket command: the mill screen adjusts it over HTTP.
        new(ApiFeedIncrease, null, machine => machine.FeedOverrideIncrease(), DuringRun: true),
        new(ApiFeedDecrease, null, machine => machine.FeedOverrideDecrease(), DuringRun: true),
        new(ApiFeedReset, null, machine => machine.FeedOverrideReset(), DuringRun: true),
    };

    private static DirectCommand? FindDirectCommand(Func<DirectCommand, bool> match) =>
        DirectCommands.FirstOrDefault(match);

    /// <summary>
    /// Whether a stored connection is one this browser left behind, so a new connection from
    /// it replaces the old. A socket still open belongs to a second tab; dropping that would
    /// leave the tab sending commands with no status.
    /// </summary>
    internal static bool IsSupersededClient(
        string? storedId, WebSocketState storedState, string newClientId) =>
        storedId == newClientId && storedState != WebSocketState.Open;

    /// <summary>
    /// The command a WebSocket message of this type runs, or null for none. A message with
    /// no type names no command: matching null would pick out the entries that have none.
    /// </summary>
    internal static DirectCommand? FindWsCommand(string? type) =>
        type == null ? null : FindDirectCommand(command => command.WsCommand == type);

    /// <summary>
    /// Runs a direct command, or says why it could not. <paramref name="offTheCallingThread"/>
    /// is for the WebSocket: homing blocks for as long as homing takes, and the same socket
    /// carries the Stop button.
    /// </summary>
    /// <returns>Null once the command has been sent, or the reason it was refused.</returns>
    private static string? RunDirectCommand(DirectCommand command, bool offTheCallingThread = false)
    {
        if (!MachineConnected)
        {
            return ErrorMachineNotConnected;
        }

        if (!command.DuringRun && MachineIsBeingDriven())
        {
            return ErrorMachineBusy;
        }

        var machine = _machine;
        if (!offTheCallingThread)
        {
            command.Run(machine);
            return null;
        }

        _ = Task.Run(() =>
        {
            try
            {
                command.Run(machine);
            }
            catch (Exception ex)
            {
                Logger.Log("Command {0} failed: {1}", command.Path, ex);
            }
        });

        return null;
    }

    /// <summary>
    /// True if the request may be answered here. A wrong method gets a 405 rather than an
    /// empty 200, which the browser reads as a lost connection.
    /// </summary>
    private static async Task<bool> RequireMethod(
        HttpListenerResponse response, string method, params string[] allowed)
    {
        if (allowed.Contains(method))
        {
            return true;
        }

        response.StatusCode = HttpStatusMethodNotAllowed;
        await WriteJson(response, new { error = ErrorMethodNotAllowed });
        return false;
    }

    /// <summary>
    /// Answers a request to start something: success, or the reason it was refused. The
    /// browser puts its screen up on this answer.
    /// </summary>
    private static async Task WriteStartResult(HttpListenerResponse response, string? refusal)
    {
        if (refusal == null)
        {
            await WriteJson(response, new { success = true });
            return;
        }

        response.StatusCode = HttpStatusConflict;
        await WriteJson(response, new { success = false, error = refusal });
    }

    /// <summary>
    /// What the caller asked for, else where that browser was last looking, else home.
    /// </summary>
    private static string BrowseDirectory(string? requested, string? lastVisited)
    {
        if (!string.IsNullOrEmpty(requested))
        {
            return requested;
        }

        return string.IsNullOrEmpty(lastVisited)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : lastVisited;
    }

    /// <summary>
    /// A count from a query parameter, bounded by <paramref name="max"/>. Anything absent,
    /// unparseable or out of range gives <paramref name="fallback"/>.
    /// </summary>
    private static int QueryInt(HttpListenerRequest request, string name, int fallback, int max) =>
        int.TryParse(request.QueryString[name], out int value) && value > 0
            ? Math.Min(value, max)
            : fallback;

    private static async Task HandleApi(HttpListenerContext context, string path)
    {
        var request = context.Request;
        var response = context.Response;
        var method = request.HttpMethod;

        response.ContentType = ContentTypeJson;

        // Commands that hand one instruction to the machine and need nothing else from the
        // request are answered from the table they share with the WebSocket, rather than
        // spelling the same case out a dozen times here.
        var direct = FindDirectCommand(command => command.Path == path);
        if (direct != null)
        {
            if (await RequireMethod(response, method, MethodPost))
            {
                await WriteStartResult(response, RunDirectCommand(direct));
            }
            return;
        }

        switch (path)
        {
            case ApiStatus:
                if (await RequireMethod(response, method, MethodGet))
                {
                    await WriteJson(response, GetStatus());
                }
                break;

            case ApiConfig:
                if (await RequireMethod(response, method, MethodGet))
                {
                    await WriteJson(response, GetConfig());
                }
                break;

            case ApiConstants:
                if (await RequireMethod(response, method, MethodGet))
                {
                    await WriteJson(response, GetSharedConstants());
                }
                break;

            case ApiPorts:
                if (await RequireMethod(response, method, MethodGet))
                {
                    var ports = Menus.ConnectionMenu.GetAvailablePorts();
                    await WriteJson(response, new { ports });
                }
                break;

            case ApiConnect:
                if (await RequireMethod(response, method, MethodPost))
                {
                    var connectReq = await ReadBody<ConnectRequest>(request, response);
                    if (connectReq != null)
                    {
                        await HandleConnect(response, connectReq);
                    }
                }
                break;

            case ApiDisconnect:
                if (await RequireMethod(response, method, MethodPost))
                {
                    HandleDisconnect();
                    await WriteJson(response, new { success = true });
                }
                break;

            case ApiZero:
                if (await RequireMethod(response, method, MethodPost))
                {
                    var zeroReq = await ReadBody<ZeroRequest>(request, response);
                    if (zeroReq != null)
                    {
                        await WriteStartResult(response, HandleZero(zeroReq));
                    }
                }
                break;

            // File browsers - G-code and saved probe grids, same listing with a different
            // filter and a browse directory of its own.
            case ApiFiles:
            case ApiProbeFiles:
                if (await RequireMethod(response, method, MethodGet))
                {
                    bool probeFiles = path == ApiProbeFiles;
                    var dir = BrowseDirectory(
                        request.QueryString[QueryParamPath],
                        probeFiles
                            ? AppState.Session.LastProbeBrowseDirectory
                            : AppState.Session.LastBrowseDirectory);
                    await WriteJson(response, probeFiles ? GetProbeFiles(dir) : GetFiles(dir));
                }
                break;

            case ApiFileLoad:
                if (await RequireMethod(response, method, MethodPost))
                {
                    var loadReq = await ReadBody<LoadFileRequest>(request, response);
                    if (loadReq != null)
                    {
                        await HandleLoadFile(response, loadReq);
                    }
                }
                break;

            case ApiFileUpload:
                if (await RequireMethod(response, method, MethodPost))
                {
                    await HandleFileUpload(request, response);
                }
                break;

            case ApiFileInfo:
                if (await RequireMethod(response, method, MethodGet))
                {
                    await WriteJson(response, GetFileInfo() ?? new { error = ErrorNoFileLoaded });
                }
                break;

            // Milling control
            case ApiMillPreflight:
                if (await RequireMethod(response, method, MethodGet))
                {
                    await WriteJson(response, HandleMillPreflight());
                }
                break;

            case ApiMillStart:
                if (await RequireMethod(response, method, MethodPost))
                {
                    await WriteStartResult(response, await StartMilling());
                }
                break;

            case ApiMillPause:
                if (await RequireMethod(response, method, MethodPost))
                {
                    var pauseController = AppState.Milling;
                    if (pauseController.State == ControllerState.Running)
                    {
                        pauseController.Pause();
                        await WriteJson(response, new { success = true });
                    }
                    else
                    {
                        response.StatusCode = HttpStatusBadRequest;
                        await WriteJson(response, new { error = ErrorCannotPauseNotRunning });
                    }
                }
                break;

            case ApiMillResume:
                if (await RequireMethod(response, method, MethodPost))
                {
                    var resumeController = AppState.Milling;

                    // A tool change leaves the milling controller Paused for its own
                    // reasons. Resuming here while one is under way would restart file
                    // streaming behind its back, mid tool-swap. Gated on the milling
                    // controller's own Phase rather than DetectToolChange (the tool
                    // change controller's status): Phase flips to ToolChange before the
                    // Paused transition and before the event that starts the tool change
                    // controller fires, so this is race-free - DetectToolChange lags up
                    // to a few seconds behind it (see DetectToolChange's own remarks).
                    bool toolChangeActive = resumeController.Phase == MillingPhase.ToolChange;
                    if (resumeController.IsPaused && !toolChangeActive)
                    {
                        resumeController.Resume();
                        await WriteJson(response, new { success = true });
                    }
                    else
                    {
                        response.StatusCode = HttpStatusBadRequest;
                        await WriteJson(response, new
                        {
                            error = toolChangeActive ? ErrorCannotResumeToolChangeActive : ErrorCannotResumeNotPaused
                        });
                    }
                }
                break;

            case ApiMillStop:
                if (await RequireMethod(response, method, MethodPost))
                {
                    await WriteStopResult(response, await HandleMillStopAsync());
                }
                break;

            // Probing
            case ApiProbeSetup:
                if (await RequireMethod(response, method, MethodPost))
                {
                    var probeReq = await ReadBody<ProbeSetupRequest>(request, response);
                    if (probeReq != null)
                    {
                        await HandleProbeSetup(response, probeReq);
                    }
                }
                break;

            case ApiProbeTrace:
                if (await RequireMethod(response, method, MethodPost))
                {
                    await WriteStartResult(response, StartProbeTraceOutline());
                }
                break;

            case ApiProbeStart:
                if (await RequireMethod(response, method, MethodPost))
                {
                    await WriteStartResult(response, await StartProbing());
                }
                break;

            case ApiProbePause:
                if (await RequireMethod(response, method, MethodPost))
                {
                    var pauseProbeController = AppState.Probe;
                    if (pauseProbeController.State == ControllerState.Running)
                    {
                        pauseProbeController.Pause();
                        await WriteJson(response, new { success = true });
                    }
                    else
                    {
                        await WriteJson(response, new
                        {
                            success = false,
                            error = pauseProbeController.IsPaused
                                ? ErrorProbingAlreadyPaused
                                : ErrorProbingNotRunning
                        });
                    }
                }
                break;

            case ApiProbeResume:
                if (await RequireMethod(response, method, MethodPost))
                {
                    var resumeProbeController = AppState.Probe;
                    if (resumeProbeController.IsPaused)
                    {
                        resumeProbeController.Resume();
                        await WriteJson(response, new { success = true });
                    }
                    else
                    {
                        await WriteJson(response, new { success = false, error = ErrorProbingNotPaused });
                    }
                }
                break;

            case ApiProbeStop:
                if (await RequireMethod(response, method, MethodPost))
                {
                    await WriteStopResult(response, await HandleProbeStop());
                }
                break;

            case ApiProbeStatus:
                if (await RequireMethod(response, method, MethodGet))
                {
                    await WriteJson(response, GetProbeStatus());
                }
                break;

            case ApiProbeApply:
                if (await RequireMethod(response, method, MethodPost))
                {
                    // Applying rewrites the loaded G-code, which rewinds the file a paused
                    // run would resume from.
                    if (AnyOperationRunning())
                    {
                        response.StatusCode = HttpStatusConflict;
                        await WriteJson(response, new { success = false, error = ErrorMachineBusy });
                        break;
                    }

                    bool success = AppState.ApplyProbeData();
                    await WriteJson(response, new { success, applied = AppState.AreProbePointsApplied });
                }
                break;

            case ApiProbeSave:
                if (await RequireMethod(response, method, MethodPost))
                {
                    var saveReq = await ReadBody<ProbeSaveRequest>(request, response);
                    if (saveReq != null)
                    {
                        await HandleProbeSave(response, saveReq);
                    }
                }
                break;

            case ApiProbeLoad:
                if (await RequireMethod(response, method, MethodPost))
                {
                    var loadReq = await ReadBody<ProbeLoadRequest>(request, response);
                    if (loadReq != null)
                    {
                        await HandleProbeLoad(response, loadReq);
                    }
                }
                break;

            case ApiProbeClear:
            case ApiProbeDiscard:
                if (await RequireMethod(response, method, MethodPost))
                {
                    if (AnyOperationRunning())
                    {
                        response.StatusCode = HttpStatusConflict;
                        await WriteJson(response, new { success = false, error = ErrorMachineBusy });
                        break;
                    }

                    bool discarded = HandleProbeDiscard();
                    if (!discarded)
                    {
                        response.StatusCode = HttpStatusServerError;
                    }
                    await WriteJson(response, new
                    {
                        success = discarded,
                        error = discarded ? null : CliConstants.ProbeDiscardFailed
                    });
                }
                break;

            // Tool change

            case ApiMillToolChangeAbort:
                if (await RequireMethod(response, method, MethodPost))
                {
                    await WriteStopResult(response, await HandleToolChangeAbortAsync());
                }
                break;

            case ApiMillToolChangeUserInput:
                if (await RequireMethod(response, method, MethodPost))
                {
                    var inputReq = await ReadBody<ToolChangeUserInputRequest>(request, response);
                    if (inputReq != null)
                    {
                        await HandleToolChangeUserInput(response, inputReq);
                    }
                }
                break;

            // Depth adjustment
            case ApiMillDepth:
                if (!await RequireMethod(response, method, MethodGet, MethodPost))
                {
                    break;
                }
                if (method == MethodGet)
                {
                    await WriteJson(response, new { depth = AppState.DepthAdjustment });
                }
                else
                {
                    var depthReq = await ReadBody<DepthAdjustmentRequest>(request, response);
                    if (depthReq != null)
                    {
                        HandleDepthAdjustment(depthReq);
                        await WriteJson(response, new { success = true, depth = AppState.DepthAdjustment });
                    }
                }
                break;

            case ApiMillGrid:
                if (await RequireMethod(response, method, MethodGet))
                {
                    // Grid dimensions come from the client, which sizes them to its screen
                    // from the maxima this server published.
                    await WriteJson(response, new
                    {
                        cells = GetVisitedGridCells(
                            AppState.Milling,
                            QueryInt(request, QueryParamWidth, WebMillGridDefaultWidth, MillGridMaxWidth),
                            QueryInt(request, QueryParamHeight, WebMillGridDefaultHeight, MillGridMaxHeight)),
                        count = AppState.Milling.CuttingPath.Count
                    });
                }
                break;

            case ApiForceDisconnect:
                if (await RequireMethod(response, method, MethodPost))
                {
                    await HandleForceDisconnect(response);
                }
                break;

            case ApiSettings:
                if (!await RequireMethod(response, method, MethodGet, MethodPost))
                {
                    break;
                }
                if (method == MethodGet)
                {
                    await WriteJson(response, GetSettings());
                }
                else
                {
                    var settingsReq = await ReadBody<SettingsUpdateRequest>(request, response);
                    if (settingsReq != null)
                    {
                        await HandleSettingsUpdate(response, settingsReq);
                    }
                }
                break;

            case ApiProfiles:
                if (await RequireMethod(response, method, MethodGet))
                {
                    await WriteJson(response, GetMachineProfiles());
                }
                break;

            case ApiSessionRestore:
                if (!await RequireMethod(response, method, MethodGet, MethodPost))
                {
                    break;
                }
                if (method == MethodGet)
                {
                    await WriteJson(response, new
                    {
                        steps = SessionRestore.GetPendingSteps().Select(step => new
                        {
                            topic = step.Topic.ToString(),
                            question = step.Question,
                            detail = step.Detail,
                            defaultYes = step.DefaultYes
                        })
                    });
                }
                else
                {
                    var answer = await ReadBody<SessionRestoreAnswerRequest>(request, response);
                    if (answer == null)
                    {
                        break;
                    }

                    if (answer.topic == null
                        || !Enum.TryParse<SessionRestoreTopic>(answer.topic, out var topic))
                    {
                        response.StatusCode = HttpStatusBadRequest;
                        await WriteJson(response, new { error = ErrorInvalidRequest });
                        break;
                    }

                    SessionRestore.Answer(topic, answer.yes ?? false);
                    await WriteJson(response, new { success = true });
                }
                break;

            case ApiTrustWorkZero:
                if (await RequireMethod(response, method, MethodPost))
                {
                    await HandleTrustWorkZero(response);
                }
                break;

            case ApiProbeRecoverAutosave:
                if (await RequireMethod(response, method, MethodPost))
                {
                    await HandleProbeRecoverAutosave(response);
                }
                break;

            default:
                response.StatusCode = HttpStatusNotFound;
                await WriteJson(response, new { error = ErrorNotFound });
                break;
        }
    }

    /// <summary>
    /// Whether any workflow is under way, so the machine is not disconnected out from
    /// under one. Asked of the controllers, which own the answer; a task handle is nulled
    /// after a stop that timed out and would report idle while the run is still moving.
    ///
    /// A run waiting on the operator counts: the tool is in the work with the job half done.
    /// </summary>
    private static bool AnyOperationRunning() =>
        AppState.Probe.IsRunInProgress
        || AppState.Milling.IsRunInProgress
        || AppState.ToolChange.IsRunInProgress
        || (_machine?.IsHoming ?? false);

    /// <summary>
    /// Whether a workflow is driving the machine, so a move of the caller's own would land
    /// in the middle of one. Narrower than <see cref="AnyOperationRunning"/>.
    /// </summary>
    private static bool MachineIsBeingDriven()
    {
        if (AppState.Probe.IsActive || AppState.ToolChange.IsActive || (_machine?.IsHoming ?? false))
        {
            return true;
        }

        // A tool change is the one wait an operator is meant to jog through: it asks them to
        // go to the surface and set Z0. Every other hold leaves the tool where the job put
        // it, and a move from here would cut the rest of the pass from somewhere else.
        var milling = AppState.Milling;
        return milling.IsRunInProgress && milling.Phase != MillingPhase.ToolChange;
    }

    private static object GetStatus()
    {
        if (_machine == null)
        {
            return new
            {
                connected = false,
                status = "Disconnected",
                buttons = GetButtonStates(false)
            };
        }

        var controller = AppState.Milling;
        var controllerState = controller.State;
        var controllerPhase = controller.Phase;

        // Not over while the spindle waits on the operator, nor while the tool retracts.
        var isMilling = ControllerBase.IsRunInProgressState(controllerState);

        // Tool change state from AppState (set by controller event), falling back to a
        // pending bare M0/M1 prompt when there is no tool change. Both flow through the
        // same overlay client-side, and this field is the only way a client that reloaded
        // or reconnected mid-prompt can recover it - the toolchange:input WS broadcast
        // that announced it live is one-shot and already missed by then.
        var toolChange = DetectToolChange() ?? DetectOperatorPause();

        var settings = AppState.Settings;
        var profile = !string.IsNullOrEmpty(settings.MachineProfile)
            ? MachineProfiles.GetProfile(settings.MachineProfile)
            : null;

        return new
        {
            connected = _machine.Connected,
            status = _machine.Status,
            // Derived in Core so the browser and the terminal answer "is the door open"
            // the same way: GRBL reports Door either side of the operator closing it.
            doorOpen = MachineWait.IsDoorOpen(_machine),
            doorAwaitingResume = MachineWait.IsDoorAwaitingResume(_machine),
            machineProfile = profile?.Name,
            workPos = new
            {
                x = _machine.WorkPosition.X,
                y = _machine.WorkPosition.Y,
                z = _machine.WorkPosition.Z
            },
            machinePos = new
            {
                x = _machine.MachinePosition.X,
                y = _machine.MachinePosition.Y,
                z = _machine.MachinePosition.Z
            },
            feedOverride = _machine.FeedOverride,
            probePin = _machine.PinStateProbe,
            file = GetFileStatus(),
            probe = GetProbeStatusBrief(),
            probeApplied = AppState.AreProbePointsApplied,
            milling = isMilling,
            millingPhase = controllerPhase.ToString(),
            controllerState = controllerState.ToString(),
            cuttingPathCount = controller.CuttingPath.Count,  // Client uses this to know when to fetch grid
            probing = AppState.IsMeasuringGrid,
            tracingOutline = AppState.IsTracingOutline,
            toolChange = toolChange,
            depthAdjustment = AppState.DepthAdjustment,
            buttons = GetButtonStates(_machine.Connected),
            hasStoredWorkZero = AppState.Session.HasStoredWorkZero,
            isWorkZeroSet = AppState.IsWorkZeroSet
        };
    }

    /// <summary>
    /// Get tool change status from the ToolChangeController, for display only. The
    /// controller's Phase is the single source of truth, but StartToolChangeControllerAsync
    /// does not reach it until the tool-change run task is scheduled and picked up by the
    /// thread pool, so this lags the milling controller's own MillingPhase.ToolChange by
    /// up to a few seconds. A caller that needs to know synchronously whether a tool
    /// change is under way (e.g. gating /api/mill/resume) must check
    /// AppState.Milling.Phase directly instead - see that call site's remarks.
    ///
    /// Which phase puts what on screen is documented on <see cref="ToolChangePhase"/>.
    /// </summary>
    private static object? DetectToolChange()
    {
        var controller = AppState.ToolChange;
        var phase = controller.Phase;
        var state = controller.State;

        // Whether a run is under way is the run's state to answer. Reading it off the
        // phase asked one enum two questions, and the two could disagree.
        if (!controller.IsActive && !ControllerBase.IsWaitingForOperatorState(state))
        {
            return null;
        }

        // Tool change in progress - return phase and tool info
        var info = controller.CurrentToolChange;

        // Log when returning non-null with null tool info (the bug condition)
        if (info == null)
        {
            Logger.Log($"DetectToolChange: phase={phase}, state={state}, info=null (BUG!)");
        }

        // The prompt now waiting, if one is. A client that reloaded mid-tool-change missed
        // the toolchange:input broadcast, so this is the only way it learns which question
        // it is answering - and an answer has to name its question.
        var pending = PendingPrompt.Current;

        return new
        {
            phase = phase.ToString(),
            toolNumber = info?.ToolNumber,
            toolName = info?.ToolName,
            id = pending?.Id,
            options = pending?.Options
        };
    }

    /// <summary>
    /// Detects a bare M0/M1 prompt pending on the milling controller itself - the
    /// counterpart to DetectToolChange for the one case that controller doesn't cover.
    /// Shaped so the single client-side handler that already reconstructs a tool-change
    /// overlay from status.toolChange can reconstruct this one too, without needing to
    /// know which kind of prompt it is.
    /// </summary>
    private static object? DetectOperatorPause()
    {
        var pending = PendingPrompt.Current;
        if (!ControllerBase.IsWaitingForOperatorState(AppState.Milling.State) || pending == null)
        {
            return null;
        }

        return new
        {
            phase = PromptKindOperatorPause,
            title = pending.Title,
            message = pending.Message,
            options = pending.Options,
            id = pending.Id
        };
    }

    /// <summary>
    /// Returns button enablement states matching TUI menu logic.
    /// Each button has: enabled (bool), reason (string or null if enabled).
    /// Uses shared helpers from MenuHelpers to avoid duplicating validation logic.
    /// </summary>
    private static object GetButtonStates(bool isConnected)
    {
        // Jog: requires connection
        string? jogReason = !isConnected ? DisabledConnect : null;

        // Probe: requires connection, file loaded, work zero set
        string? probeReason = MenuHelpers.GetProbeDisabledReason();

        // Mill: requires connection, file loaded, probe data applied (if exists)
        string? millReason = MenuHelpers.GetMillDisabledReason();

        return new
        {
            jog = new { enabled = jogReason == null, reason = jogReason },
            probe = new { enabled = probeReason == null, reason = probeReason },
            mill = new { enabled = millReason == null, reason = millReason }
        };
    }

    private static object? GetProbeStatusBrief()
    {
        var (grid, state, hasUnsavedData) = ReadProbeStateSnapshot();

        if (grid == null)
        {
            return new
            {
                active = false,
                hasUnsavedData,
                state
            };
        }

        var controller = AppState.Probe;

        return new
        {
            active = AppState.IsMeasuringGrid,
            hasUnsavedData,
            progress = grid.Progress,
            total = grid.TotalPoints,
            sizeX = grid.SizeX,
            sizeY = grid.SizeY,
            phase = controller.Phase.ToString(),
            state,
            sourceGCodeMissing = AppState.IsProbeSourceGCodeMissing
        };
    }

    private static object GetConfig()
    {
        return new
        {
            jogModes = JogModes.Select(m => new
            {
                name = m.Name,
                feed = m.Feed,
                baseDistance = m.BaseDistance,
                maxMultiplier = m.MaxMultiplier
            }).ToArray(),
            defaultJogModeIndex = DefaultJogModeIndex,
            probeDefaults = new
            {
                margin = DefaultProbeMargin,
                gridSize = DefaultProbeGridSize
            },
            millGrid = new
            {
                maxWidth = MillGridMaxWidth,
                maxHeight = MillGridMaxHeight
            },
            version = AppVersion
        };
    }

    /// <summary>
    /// Returns constants that are shared between server and client.
    /// This ensures the JS client uses the same values as the server.
    /// </summary>
    private static object GetSharedConstants()
    {
        return new
        {
            // Status strings - must match GrblProtocol
            status = new
            {
                run = GrblProtocol.StatusRun,
                hold = GrblProtocol.StatusHold,
                idle = GrblProtocol.StatusIdle,
                alarm = GrblProtocol.StatusAlarm,
                door = GrblProtocol.StatusDoor
            },
            // Controller states - must match ControllerState enum
            controllerStates = new
            {
                idle = nameof(ControllerState.Idle),
                initializing = nameof(ControllerState.Initializing),
                running = nameof(ControllerState.Running),
                paused = nameof(ControllerState.Paused),
                waitingForUserInput = nameof(ControllerState.WaitingForUserInput),
                completing = nameof(ControllerState.Completing),
                completed = nameof(ControllerState.Completed),
                failed = nameof(ControllerState.Failed),
                cancelled = nameof(ControllerState.Cancelled)
            },
            // Prompt kinds the client keys behavior off, and the choice that carries one on.
            promptKinds = new
            {
                operatorPause = PromptKindOperatorPause
            },
            promptOptions = new
            {
                carryOn = ControllerConstants.OptionContinue
            },
            // The phases the client changes its display for. The rest are shown as they
            // arrive, so only these have to agree.
            phases = new
            {
                milling = nameof(MillingPhase.Milling),
                tracingOutline = nameof(ProbePhase.TracingOutline),
                waitingForToolChange = nameof(ToolChangePhase.WaitingForToolChange),
                waitingForZeroZ = nameof(ToolChangePhase.WaitingForZeroZ)
            },
            // WebSocket message types
            wsMessageTypes = new
            {
                status = WsMessageTypeStatus,
                millState = WsMessageTypeMillState,
                millProgress = WsMessageTypeMillProgress,
                millToolChange = WsMessageTypeMillToolChange,
                millError = WsMessageTypeMillError,
                toolChangeState = WsMessageTypeToolChangeState,
                toolChangeProgress = WsMessageTypeToolChangeProgress,
                toolChangeInput = WsMessageTypeToolChangeInput,
                toolChangeComplete = WsMessageTypeToolChangeComplete,
                toolChangeError = WsMessageTypeToolChangeError,
                probeError = WsMessageTypeProbeError,
                connectionError = WsMessageTypeConnectionError
            },
            // WebSocket close reasons
            wsCloseReasons = new
            {
                forceDisconnect = WsCloseReasonForceDisconnect
            },
            // Display formatting
            decimals = new
            {
                brief = PositionDecimalsBrief,
                full = PositionDecimalsFull
            },
            // Probe limits
            probe = new
            {
                minMargin = MinProbeMargin,
                maxMargin = MaxProbeMargin,
                minGridSize = MinProbeGridSize,
                maxGridSize = MaxProbeGridSize
            },
            // Probe states - 4-state model based on in-memory grid progress
            probeStates = new
            {
                none = ProbeStateNone,
                ready = ProbeStateReady,
                partial = ProbeStatePartial,
                complete = ProbeStateComplete
            },
            // Mill grid visualization - matches CliConstants.cs
            millGrid = new
            {
                maxWidth = MillGridMaxWidth,
                maxHeight = MillGridMaxHeight,
                cuttingDepthThreshold = MillCuttingDepthThreshold,
                minRangeThreshold = MillMinRangeThreshold
            },
            // Depth adjustment - matches CliConstants.cs
            depthAdjustment = new
            {
                increment = DepthAdjustmentIncrement,
                max = DepthAdjustmentMax
            },
            // Visualization thresholds - matches Constants.cs
            thresholds = new
            {
                heightRangeEpsilon = HeightRangeEpsilon,
                millMinRange = MillMinRangeThreshold
            },
            // WebSocket commands - for client to use
            commands = new
            {
                ping = WsCmdPing,
                jogMode = WsCmdJogMode,
                home = WsCmdHome,
                unlock = WsCmdUnlock,
                reset = WsCmdReset,
                feedhold = WsCmdFeedhold,
                resume = WsCmdResume,
                gotoOrigin = WsCmdGotoOrigin,
                gotoCenter = WsCmdGotoCenter,
                gotoSafe = WsCmdGotoSafe,
                gotoRef = WsCmdGotoRef,
                gotoZ0 = WsCmdGotoZ0,
                probeZ = WsCmdProbeZ
            }

            // API paths are not published. A path the client has wrong answers 404, which is
            // loud; a second copy of two dozen of them that nothing reads and nothing checks
            // is a set of facts that can quietly disagree.
        };
    }

    private static object? GetFileStatus()
    {
        var file = AppState.CurrentFile;
        if (file == null)
        {
            return null;
        }

        // Use machine's file count as source of truth (includes probe adjustments)
        // Fall back to original file count if machine not available
        int totalLines = _machine?.File.Count ?? file.Toolpath.Count;
        int currentLine = _machine?.FilePosition ?? 0;

        return new
        {
            name = Path.GetFileName(file.FileName),
            path = file.FileName,
            totalLines,
            currentLine,
            progress = totalLines > 0 ? (double)currentLine / totalLines : 0,
            // Bounds for grid visualization (use feed bounds if available for actual cutting area)
            minX = file.SizeFeed.X > MillMinRangeThreshold ? file.MinFeed.X : file.Min.X,
            maxX = file.SizeFeed.X > MillMinRangeThreshold ? file.MaxFeed.X : file.Max.X,
            minY = file.SizeFeed.Y > MillMinRangeThreshold ? file.MinFeed.Y : file.Min.Y,
            maxY = file.SizeFeed.Y > MillMinRangeThreshold ? file.MaxFeed.Y : file.Max.Y
        };
    }

    /// <summary>
    /// Get visited grid cells from cutting path (for mill visualization).
    /// Returns array of "x,y" strings for cells that have been milled.
    /// </summary>
    /// <param name="controller">The milling controller</param>
    /// <param name="maxWidth">Maximum grid width (from client based on screen size)</param>
    /// <param name="maxHeight">Maximum grid height (from client based on screen size)</param>
    private static string[] GetVisitedGridCells(IMillingController controller, int maxWidth, int maxHeight)
    {
        var file = AppState.CurrentFile;
        if (file == null)
        {
            return Array.Empty<string>();
        }

        var path = controller.CuttingPath;
        if (path.Count == 0)
        {
            return Array.Empty<string>();
        }

        // Use feed bounds (actual cutting area) if available, otherwise fall back to full bounds
        bool useFeedBounds = file.SizeFeed.X > MillMinRangeThreshold && file.SizeFeed.Y > MillMinRangeThreshold;
        double minX = useFeedBounds ? file.MinFeed.X : file.Min.X;
        double maxX = useFeedBounds ? file.MaxFeed.X : file.Max.X;
        double minY = useFeedBounds ? file.MinFeed.Y : file.Min.Y;
        double maxY = useFeedBounds ? file.MaxFeed.Y : file.Max.Y;

        // Calculate ranges
        double rangeX = Math.Max(maxX - minX, MillMinRangeThreshold);
        double rangeY = Math.Max(maxY - minY, MillMinRangeThreshold);
        double aspectRatio = rangeX / rangeY;

        // Calculate grid dimensions based on aspect ratio
        int gridWidth, gridHeight;
        if (aspectRatio > 1)
        {
            gridWidth = Math.Min(maxWidth, (int)Math.Ceiling(maxHeight * aspectRatio));
            gridHeight = maxHeight;
        }
        else
        {
            gridWidth = maxWidth;
            gridHeight = Math.Min(maxHeight, (int)Math.Ceiling(maxWidth / aspectRatio));
        }

        // Map points to grid cells
        var cells = new HashSet<string>();
        foreach (var point in path)
        {
            int gridX = MapToGrid(point.X, minX, rangeX, gridWidth);
            int gridY = MapToGrid(point.Y, minY, rangeY, gridHeight);
            cells.Add($"{gridX},{gridY}");
        }

        return cells.ToArray();
    }

    /// <summary>Map a coordinate to grid index.</summary>
    private static int MapToGrid(double value, double min, double range, int gridSize)
    {
        if (range < MillMinRangeThreshold)
        {
            return 0;
        }
        int index = (int)Math.Floor((value - min) / range * (gridSize - 1));
        return Math.Max(0, Math.Min(gridSize - 1, index));
    }

    private static async Task HandleConnect(HttpListenerResponse response, ConnectRequest req)
    {
        if (_machine == null)
        {
            response.StatusCode = HttpStatusBadRequest;
            await WriteJson(response, new { error = ErrorInvalidRequest });
            return;
        }

        var port = req.port ?? _serialPort;
        var baud = req.baud ?? _baudRate;

        try
        {
            // Update settings
            AppState.Settings.SerialPortName = port;
            AppState.Settings.SerialPortBaud = baud;

            _machine.Connect();
            await WriteJson(response, new { success = true });
        }
        catch (Exception ex)
        {
            await WriteFailure(response, "Connect", ex);
        }
    }

    private static void HandleDisconnect()
    {
        if (_machine?.Connected == true)
        {
            _machine.Disconnect();
        }
    }

    /// <summary>
    /// Handles mode-based jog commands. Client sends mode index and direction,
    /// server uses its own JogModes array for the actual values.
    /// This prevents client from sending arbitrary G-code parameters.
    /// </summary>
    private static void HandleJogWithMode(string? axisStr, int direction, int modeIndex)
    {
        if (!MachineConnected)
        {
            return;
        }

        // Validate axis
        var axis = axisStr?.ToUpperInvariant();
        if (axis != "X" && axis != "Y" && axis != "Z")
        {
            Logger.Log($"Invalid jog axis: {axisStr}");
            return;
        }

        // Block X/Y movement when probe is in contact (prevents dragging probe across workpiece)
        if ((axis == "X" || axis == "Y") && _machine.PinStateProbe)
        {
            Logger.Log($"Blocked {axis} jog: probe in contact");
            return;
        }

        // Validate direction (-1 or +1 only)
        if (direction != -1 && direction != 1)
        {
            Logger.Log($"Invalid jog direction: {direction}");
            return;
        }

        // Validate mode index and get mode from server-side array
        if (modeIndex < 0 || modeIndex >= JogModes.Length)
        {
            Logger.Log($"Invalid jog mode index: {modeIndex}");
            return;
        }

        var mode = JogModes[modeIndex];
        var distance = mode.BaseDistance * direction;
        var feed = mode.Feed;

        _machine.Jog(axis[0], distance, feed);
    }

    /// <returns>Null once the work zero is set, or the reason it was refused.</returns>
    private static string? HandleZero(ZeroRequest req)
    {
        Logger.Log($"HandleZero called: axes={string.Join(",", req.axes ?? Array.Empty<string>())}");

        if (!MachineConnected)
        {
            return ErrorMachineNotConnected;
        }

        // Re-datuming under a run would move the rest of the job relative to the part.
        if (MachineIsBeingDriven())
        {
            return ErrorMachineBusy;
        }

        var requested = req.axes ?? new[] { "X", "Y", "Z" };

        // Whitelist, the way HandleJogWithMode already does. These strings are
        // interpolated into a G-code line, so anything not X/Y/Z - a newline especially -
        // would append commands of the caller's choosing to the one we meant to send.
        var axesUpper = requested
            .Where(a => a != null)
            .Select(a => a.Trim().ToUpperInvariant())
            .Where(a => a == "X" || a == "Y" || a == "Z")
            .Distinct()
            .ToArray();

        if (axesUpper.Length == 0)
        {
            return ErrorInvalidRequest;
        }

        var axesStr = string.Join(" ", axesUpper.Select(a => $"{a}0"));
        Logger.Log($"HandleZero: axes={axesStr} workPos=({_machine.WorkPosition.X:F3},{_machine.WorkPosition.Y:F3},{_machine.WorkPosition.Z:F3}) machPos=({_machine.MachinePosition.X:F3},{_machine.MachinePosition.Y:F3},{_machine.MachinePosition.Z:F3})");

        // SetWorkZeroAndWait handles probe grid state (re-applies if Z-only, discards if XY)
        MachineCommands.SetWorkZeroAndWait(_machine, axesStr);
        Logger.Log($"HandleZero: after zero workPos=({_machine.WorkPosition.X:F3},{_machine.WorkPosition.Y:F3},{_machine.WorkPosition.Z:F3})");

        // Retract to safe height after zeroing Z or all axes (matches TUI behavior)
        bool includesZ = axesUpper.Contains("Z");
        Logger.Log($"HandleZero: axesUpper={string.Join(",", axesUpper)}, includesZ={includesZ}");
        if (includesZ)
        {
            // Fire-and-forget: send retract command, don't block HTTP handler
            // User sees Z moving via WebSocket status updates
            Logger.Log($"HandleZero: sending retract to Z={Constants.RetractZMm}");
            MachineCommands.MoveToSafeHeight(_machine, Constants.RetractZMm);
        }
        else
        {
            Logger.Log("HandleZero: no Z axis, skipping retract");
        }

        Logger.Log("HandleZero: done");
        return null;
    }

    private static void ProbeZSingle()
    {
        var controller = AppState.Probe;

        // Configure probe options
        controller.Options = ProbeOptions.FromSettings(AppState.Settings);

        // Run probe and handle result
        _ = Task.Run(async () =>
        {
            var (success, _) = await controller.ProbeZSingleAsync(CancellationToken.None);
            if (!success)
            {
                BroadcastMessage(WsMessageTypeProbeError, new { message = ControllerConstants.ErrorProbeNoContact });
            }
        });
    }

    private static object GetFiles(string dirPath) =>
        GetFilesWithFilter(dirPath, ext => GCodeExtensions.Contains(ext));

    private static object GetFilesWithFilter(string dirPath, Func<string, bool> extensionFilter)
    {
        try
        {
            if (!Directory.Exists(dirPath))
            {
                dirPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            var entries = new List<object>();

            // Add parent directory
            var parent = Directory.GetParent(dirPath);
            if (parent != null)
            {
                entries.Add(new { name = "..", path = parent.FullName, isDir = true });
            }

            // Add directories
            foreach (var dir in Directory.GetDirectories(dirPath).OrderBy(d => d))
            {
                var name = Path.GetFileName(dir);
                if (!name.StartsWith("."))
                {
                    entries.Add(new { name, path = dir, isDir = true });
                }
            }

            // Add matching files
            foreach (var file in Directory.GetFiles(dirPath).OrderBy(f => f))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (extensionFilter(ext))
                {
                    var info = new FileInfo(file);
                    entries.Add(new
                    {
                        name = Path.GetFileName(file),
                        path = file,
                        isDir = false,
                        size = info.Length,
                        modified = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
                    });
                }
            }

            return new { currentPath = dirPath, entries };
        }
        catch (Exception ex)
        {
            Logger.Log("Listing {0} failed: {1}", dirPath, ex);
            return new { error = ErrorServerFailure, currentPath = dirPath, entries = Array.Empty<object>() };
        }
    }

    private static async Task HandleFileUpload(HttpListenerRequest request, HttpListenerResponse response)
    {
        try
        {
            var body = await ReadBodyBytes(request, UploadMaxBytes);
            if (body == null)
            {
                response.StatusCode = HttpStatusPayloadTooLarge;
                await WriteJson(response, new
                {
                    error = string.Format(ErrorUploadTooLarge, UploadMaxMegabytes)
                });
                return;
            }

            // Parse content type for boundary
            var contentType = request.ContentType ?? "";
            if (!contentType.StartsWith("multipart/form-data"))
            {
                response.StatusCode = HttpStatusBadRequest;
                await WriteJson(response, new { error = ErrorExpectedMultipart });
                return;
            }

            // Extract boundary
            var boundaryMatch = System.Text.RegularExpressions.Regex.Match(contentType, @"boundary=(.+)");
            if (!boundaryMatch.Success)
            {
                response.StatusCode = HttpStatusBadRequest;
                await WriteJson(response, new { error = ErrorMissingBoundary });
                return;
            }

            var boundary = "--" + boundaryMatch.Groups[1].Value.Trim('"');
            var content = Encoding.UTF8.GetString(body);

            // Find file content between boundaries
            var parts = content.Split(new[] { boundary }, StringSplitOptions.RemoveEmptyEntries);
            string? fileName = null;
            string? fileContent = null;

            foreach (var part in parts)
            {
                if (part.Trim() == "--") continue; // End boundary

                // Look for Content-Disposition with filename
                var filenameMatch = System.Text.RegularExpressions.Regex.Match(
                    part, @"filename=""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (filenameMatch.Success)
                {
                    fileName = filenameMatch.Groups[1].Value;

                    // Find the content after double newline (CRLF CRLF or LF LF)
                    const string CrlfCrlf = "\r\n\r\n";
                    const string LfLf = "\n\n";
                    var headerEnd = part.IndexOf(CrlfCrlf);
                    int separatorLen = CrlfCrlf.Length;
                    if (headerEnd < 0)
                    {
                        headerEnd = part.IndexOf(LfLf);
                        separatorLen = LfLf.Length;
                    }
                    if (headerEnd >= 0)
                    {
                        fileContent = part.Substring(headerEnd + separatorLen).TrimEnd('\r', '\n', '-');
                    }
                    break;
                }
            }

            if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(fileContent))
            {
                response.StatusCode = HttpStatusBadRequest;
                await WriteJson(response, new { error = ErrorNoFileInUpload });
                return;
            }

            // Validate extension
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (!GCodeExtensions.Contains(ext))
            {
                response.StatusCode = HttpStatusBadRequest;
                await WriteJson(response, new { error = string.Format(ErrorInvalidFileType, ext) });
                return;
            }

            // Save to uploads directory
            var uploadsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "coppercli-uploads");
            Directory.CreateDirectory(uploadsDir);

            // Take only the leaf name: the header value may contain directory
            // separators or be rooted, and Path.Combine discards its first argument
            // entirely when the second is rooted.
            fileName = Path.GetFileName(fileName);

            if (string.IsNullOrWhiteSpace(fileName))
            {
                response.StatusCode = HttpStatusBadRequest;
                await WriteJson(response, new { error = ErrorInvalidRequest });
                return;
            }

            var savePath = Path.Combine(uploadsDir, fileName);

            if (!IsContainedIn(savePath, uploadsDir))
            {
                Logger.Log("Upload refused: {0} escapes the uploads directory", savePath);
                response.StatusCode = HttpStatusBadRequest;
                await WriteJson(response, new { error = ErrorInvalidRequest });
                return;
            }

            // Handle duplicate names
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            int counter = 1;
            while (File.Exists(savePath))
            {
                savePath = Path.Combine(uploadsDir, $"{baseName}_{counter}{ext}");
                counter++;
            }

            await File.WriteAllTextAsync(savePath, fileContent);

            // Load the file into machine (single source of truth for G-code loading)
            var file = GCodeFile.Load(savePath);
            AppState.LoadGCodeIntoMachine(file);
            AppState.Session.LastLoadedGCodeFile = savePath;
            AppState.Session.LastBrowseDirectory = uploadsDir;

            await WriteJson(response, FileSummary(file));
        }
        catch (Exception ex)
        {
            await WriteFailure(response, "File upload", ex);
        }
    }

    private static async Task HandleLoadFile(HttpListenerResponse response, LoadFileRequest req)
    {
        // Loading a file clears the applied height map and the depth adjustment while the
        // machine keeps streaming the old one.
        if (AnyOperationRunning())
        {
            response.StatusCode = HttpStatusConflict;
            await WriteJson(response, new { success = false, error = ErrorMachineBusy });
            return;
        }

        if (req.path == null)
        {
            response.StatusCode = HttpStatusBadRequest;
            await WriteJson(response, new { error = ErrorNoPathSpecified });
            return;
        }

        // Validate file extension
        var ext = Path.GetExtension(req.path).ToLowerInvariant();
        if (!GCodeExtensions.Contains(ext))
        {
            response.StatusCode = HttpStatusBadRequest;
            await WriteJson(response, new { error = string.Format(ErrorInvalidFileType, ext) });
            return;
        }

        // Validate file exists and is a regular file (not directory, symlink to sensitive location, etc.)
        if (!File.Exists(req.path))
        {
            response.StatusCode = HttpStatusNotFound;
            await WriteJson(response, new { error = ErrorFileNotFound });
            return;
        }

        try
        {
            // Load the file into machine (single source of truth for G-code loading)
            var file = GCodeFile.Load(req.path);
            AppState.LoadGCodeIntoMachine(file);
            AppState.Session.LastLoadedGCodeFile = req.path;
            AppState.Session.LastBrowseDirectory = Path.GetDirectoryName(req.path);

            await WriteJson(response, FileSummary(file));
        }
        catch (Exception ex)
        {
            await WriteFailure(response, "File load", ex);
        }
    }

    /// <summary>
    /// Single serialization of a loaded G-code file, shared by every file-related web
    /// response (upload, load, status). Keeps the wire shape identical across endpoints so
    /// the client sees one contract instead of three hand-copied anonymous objects.
    /// </summary>
    private static object FileSummary(GCodeFile file) => new
    {
        success = true,
        name = file.FileName,
        path = file.FilePath,
        lines = file.Toolpath.Count,
        bounds = new
        {
            minX = file.Min.X,
            minY = file.Min.Y,
            minZ = file.Min.Z,
            maxX = file.Max.X,
            maxY = file.Max.Y,
            maxZ = file.Max.Z
        },
        travelDistance = file.TravelDistance,
        estimatedTime = file.TotalTime.TotalMinutes
    };

    private static object? GetFileInfo()
    {
        var file = AppState.CurrentFile;
        if (file == null)
        {
            return null;
        }

        return FileSummary(file);
    }

    /// <summary>
    /// Removes pending web clients whose handshake window (<see cref="PendingClientTimeoutMs"/>)
    /// has elapsed. Caller MUST hold <see cref="_clientsLock"/>; the purge runs as part of the
    /// caller's existing hold on that lock.
    /// </summary>
    private static void PurgeExpiredPendingClients()
    {
        var expired = _pendingClients
            .Where(kvp => (DateTime.Now - kvp.Value).TotalMilliseconds > PendingClientTimeoutMs)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in expired)
        {
            _pendingClients.Remove(key);
        }
    }

    /// <summary>Turns a preflight failure into the sentence the operator sees.</summary>
    private static string DescribePreflightError(MillPreflightResult result) => result.Error switch
    {
        MillPreflightError.NotConnected => PreflightErrorNotConnected,
        MillPreflightError.NoFile => PreflightErrorNoFile,
        MillPreflightError.ProbeNotApplied => PreflightErrorProbeNotApplied,
        MillPreflightError.ProbeSetupChanged => PreflightErrorProbeSetupChanged,
        MillPreflightError.ProbeIncomplete => string.Format(PreflightErrorProbeIncomplete, result.ProbeProgress),
        MillPreflightError.AlarmState => PreflightErrorAlarm,
        _ => PreflightErrorUnknown
    };

    private static object HandleMillPreflight()
    {
        var result = MenuHelpers.ValidateMillPreflight();
        var warnings = new List<string>();
        var errors = new List<string>();

        // Map error code to API error message
        if (result.Error != MillPreflightError.None)
        {
            errors.Add(DescribePreflightError(result));
        }

        // Map warnings to API warning messages
        foreach (var warning in result.Warnings)
        {
            switch (warning)
            {
                case MillPreflightWarning.NotHomed:
                    warnings.Add(PreflightWarningNotHomed);
                    break;
                case MillPreflightWarning.DangerousCommands:
                    // Add the actual dangerous warning messages from the file
                    if (result.DangerousWarnings != null)
                    {
                        warnings.AddRange(result.DangerousWarnings);
                    }
                    break;
                case MillPreflightWarning.NoMachineProfile:
                    warnings.Add(PreflightWarningNoProfile);
                    break;
            }
        }

        // The same warning the TUI gives before a job.
        if (SleepPrevention.ShouldWarn())
        {
            warnings.Add($"{CliConstants.SleepPreventionWarning}. {WarningSleepPreventionAction}");
        }

        return new { canStart = result.CanStart, errors, warnings };
    }

    /// <summary>
    /// Starts a milling run, or says why it will not. Every refusal comes back to the
    /// caller, which answers the request with it.
    /// </summary>
    /// <returns>Null once the run is under way, or the reason it was refused.</returns>
    private static async Task<string?> StartMilling()
    {
        var controller = AppState.Milling;

        // A second start would cancel the first run's token and clear the prompt the
        // operator is standing in front of.
        if (controller.IsRunInProgress)
        {
            return ErrorMillingAlreadyRunning;
        }

        if (!MachineConnected)
        {
            return ErrorMachineNotConnected;
        }

        if (AppState.CurrentFile == null)
        {
            return ErrorNoFileLoaded;
        }

        // === SAFETY PREFLIGHT ===
        // The same gate the TUI enforces (MillMenu). /api/mill/preflight only reports
        // this to the browser; enforcing it here too means a direct POST cannot start a
        // job with an incomplete or unapplied height map, which would cut a warped board
        // at a depth nobody checked.
        var preflight = MenuHelpers.ValidateMillPreflight();
        if (!preflight.CanStart)
        {
            Logger.Log("Mill start refused by preflight: {0}", preflight.Error);
            return DescribePreflightError(preflight);
        }

        // === ENSURE MACHINE READY ===
        // Clear Door state, wait for Idle
        if (!MachineCommands.EnsureMachineReady(_machine))
        {
            Logger.Log("Mill start aborted: machine did not reach a settled, alarm-free state");
            return CliConstants.ErrorMachineNotReady;
        }

        // Clear the last run off the controller, whatever state it left behind.
        await controller.ReleaseAsync();

        // Create new cancellation token for this operation
        _millCts?.Cancel();
        _millCts = new CancellationTokenSource();

        // Disable auto state clear during milling (Door should pause operation, not auto-clear)
        if (_machine != null)
        {
            _machine.EnableAutoStateClear = false;
        }

        // Subscribe to controller events - broadcast to WebSocket clients
        Action<ControllerState> onStateChanged = state =>
        {
            Logger.Log("Mill controller state: {0}", state);
            BroadcastMessage(WsMessageTypeMillState, new { state = state.ToString() });
        };
        // Throttle progress broadcasts to avoid overwhelming WebSocket (controller emits at 10Hz)
        // But always broadcast phase changes immediately
        DateTime lastProgressBroadcast = DateTime.MinValue;
        string? lastProgressPhase = null;
        Action<ProgressInfo> onProgressChanged = progress =>
        {
            var now = DateTime.Now;
            bool phaseChanged = progress.Phase != lastProgressPhase;
            if (!phaseChanged && (now - lastProgressBroadcast).TotalMilliseconds < WebConstants.WebSocketBroadcastIntervalMs)
            {
                return;  // Skip this update, same phase and too soon since last broadcast
            }
            lastProgressBroadcast = now;
            lastProgressPhase = progress.Phase;
            BroadcastMessage(WsMessageTypeMillProgress, new
            {
                phase = progress.Phase,
                percentage = progress.Percentage,
                message = progress.Message,
                currentStep = progress.CurrentStep,
                totalSteps = progress.TotalSteps
            });
        };
        Action<ToolChangeInfo> onToolChange = info =>
        {
            Logger.Log("Mill controller tool change: T{0} at line {1}", info.ToolNumber, info.LineNumber);

            // Broadcast for informational purposes (UI can show "tool change starting")
            BroadcastMessage(WsMessageTypeMillToolChange, new
            {
                toolNumber = info.ToolNumber,
                toolName = info.ToolName,
                lineNumber = info.LineNumber
            });

            // Auto-start the tool change controller (no client API call needed). This is
            // the FSM-driven approach: server controls the workflow. toolChangeCts is
            // created and assigned to the shared field here, synchronously, before
            // Task.Run schedules the body that uses it - not inside that body - so a
            // caller can never observe the field unset while a run is starting. It is
            // also the identity StartToolChangeControllerAsync's finally compares
            // against before clearing the field (compare-and-clear), so a second tool
            // change - or this run finishing very quickly - can never erase a newer
            // run's handle out from under it. Tracked in _toolChangeRunTask so
            // HandleMillStopAsync can await this exact run instead of driving the same
            // FSM itself in parallel with it.
            var toolChangeCts = new CancellationTokenSource();
            _toolChangeCts = toolChangeCts;
            _toolChangeRunTask = Task.Run(async () =>
            {
                try
                {
                    await StartToolChangeControllerAsync(info, controller, toolChangeCts);
                }
                catch (Exception ex)
                {
                    Logger.Log("Tool change run failed: {0}", ex);
                }
            });
        };
        // The mill controller's own prompt (M0/M1) - distinct from a tool change, which
        // runs on the separate ToolChangeController instance handled by onToolChange
        // above. Shares the tool-change dialog's WS message and the one prompt slot (see
        // PendingPrompt) since the two prompts can never be pending at once.
        // The question this run last published, so its teardown takes down its own and not
        // one the tool change put up in the meantime.
        UserInputRequest? published = null;
        Action<UserInputRequest> onUserInputRequired = request =>
        {
            Logger.Log("Mill controller user input required: {0}", request.Message);

            // Wrap OnResponse so the dialog closes once answered. Unlike a tool change -
            // several prompts in sequence, closed only by the workflow's own
            // toolchange:complete once every step is done - this is always exactly one
            // prompt: Abort never reaches here (it goes through the separate tool-change
            // abort endpoint/button instead), so any response landing here is Continue,
            // and there is no next prompt to keep the dialog open for.
            // Id carries request's own GUID through so a client recovering this prompt
            // from GetStatus (see DetectOperatorPause) can compare it against this same
            // broadcast's id and tell them apart from an already-answered prompt.
            published = new UserInputRequest
            {
                Id = request.Id,
                Title = request.Title,
                Message = request.Message,
                Options = request.Options,
                OnResponse = response =>
                {
                    request.OnResponse(response);
                    BroadcastMessage(WsMessageTypeToolChangeComplete, new { success = true });
                }
            };
            PendingPrompt.Set(published);
            BroadcastMessage(WsMessageTypeToolChangeInput, new
            {
                title = request.Title,
                message = request.Message,
                options = request.Options,
                id = request.Id
            });
        };
        Action<ControllerError> onError = error =>
        {
            Logger.Log("Mill controller error: {0}", error.Message);
            BroadcastMessage(WsMessageTypeMillError, new
            {
                message = error.Message,
                isFatal = error.IsFatal
            });
        };

        controller.StateChanged += onStateChanged;
        controller.ProgressChanged += onProgressChanged;
        controller.ToolChangeDetected += onToolChange;
        controller.UserInputRequired += onUserInputRequired;
        controller.ErrorOccurred += onError;

        // Configure controller. The web UI has no per-start depth confirmation (the TUI does);
        // the server-side preflight gate above is what protects a web-initiated start.
        controller.Options = MillingOptions.Create(AppState.CurrentFile?.FileName,
            AppState.DepthAdjustment, _machine!.IsHomed);

        Logger.Log("Starting milling controller: RequireHoming={0}, DepthAdjustment={1:F3}",
            controller.Options.RequireHoming, controller.Options.DepthAdjustment);

        // Start sleep prevention
        SleepPrevention.Start();
        Logger.Log("Sleep prevention started: {0}", SleepPrevention.IsActive);

        // Start controller (fire and forget - events broadcast updates). millCts is
        // captured here, after the synchronous assignment above and before Task.Run
        // schedules the body that reads it, so the closure never observes _millCts
        // unset. It is also the identity the finally below compares against before
        // clearing the shared fields (compare-and-clear): a second /api/mill/start - or
        // this run finishing very quickly - can then never erase a newer run's handle.
        // The Task itself is tracked in _millRunTask so a stop/abort can await this
        // exact run winding down rather than driving the same FSM itself in parallel
        // with it (see HandleMillStopAsync).
        var millCts = _millCts;
        _millRunTask = Task.Run(async () =>
        {
            try
            {
                await controller.StartAsync(millCts.Token);
            }
            catch (Exception ex)
            {
                // Nobody awaits this task except a stop, which swallows faults so its own
                // teardown still runs. Without this the run would end with no trace at all.
                Logger.Log("Milling run failed: {0}", ex);
            }
            finally
            {
                // These handlers belong to this run's closure, so detaching them is
                // right whatever else is happening.
                controller.StateChanged -= onStateChanged;
                controller.ProgressChanged -= onProgressChanged;
                controller.ToolChangeDetected -= onToolChange;
                controller.UserInputRequired -= onUserInputRequired;
                controller.ErrorOccurred -= onError;

                // Everything below is shared with whichever run owns the machine now.
                // A run that outlives its successor's start must not take any of it
                // back: clearing the prompt strands the operator answering one, and
                // re-arming the door auto-clear hands a safety gate back to software
                // in the middle of a cut.
                if (ReferenceEquals(_millCts, millCts))
                {
                    _millCts = null;
                    _millRunTask = null;
                    PendingPrompt.ClearIfCurrent(published);

                    SleepPrevention.Stop();

                    if (_machine != null)
                    {
                        _machine.EnableAutoStateClear = true;
                    }

                    Logger.Log("Milling controller finished");
                    StartIdleDisconnectTimer();
                }
                else
                {
                    Logger.Log("Milling controller finished; a newer run owns the machine");
                }
            }
        });

        Logger.Log("Milling started (controller-based)");
        return null;
    }

    /// <summary>
    /// Tears down BOTH controllers, however the run was interrupted: an operator's Stop
    /// must stop a tool change too, not just the milling run it paused for, so a stray
    /// "$X" issued while cleaning up milling does not clear an alarm the tool change is
    /// about to drive straight through. The Stop button and the tool-change dialog's
    /// Abort button both funnel through this one method (see <see
    /// cref="HandleToolChangeAbortAsync"/>) - a second, independent path through either
    /// FSM is how the two front ends drift apart.
    ///
    /// Serialized on <see cref="_toolChangeAbortLock"/>, bounded so a caller that cannot
    /// get in gets a definite "not confirmed stopped" answer rather than hanging the
    /// request forever. Lock order is strictly this lock, THEN the tool-change run task's
    /// own unwind, THEN <see cref="_millStopLock"/> (acquired inside <see
    /// cref="StopMillingAsync"/>, once the tool change has already finished unwinding) -
    /// never the reverse, so the two locks cannot deadlock against each other.
    ///
    /// Nothing reachable from the tool-change run task itself may call back
    /// into this method - that would await this exact task from inside its own
    /// execution. A tool change that ends without success on its own (nobody at the Stop
    /// button) tears down the milling run via <see cref="StopMillingAsync"/> directly
    /// instead - see StartToolChangeControllerAsync.
    /// </summary>
    /// <returns>False if a lock, or a run's cancellation-driven unwind, did not complete
    /// within <see cref="ControllerCancelTimeoutMs"/> - the caller must not tell the
    /// operator the machine has stopped.</returns>
    private static async Task<bool> HandleMillStopAsync()
    {
        var budget = Stopwatch.StartNew();

        bool acquiredAbortLock = await _toolChangeAbortLock.WaitAsync(RemainingStopBudgetMs(budget));
        if (!acquiredAbortLock)
        {
            Logger.Log("Mill stop: timed out waiting for a previous stop/abort to finish");
            return false;
        }

        try
        {
            Logger.Log("Mill stop requested");

            var toolChangeRunTask = _toolChangeRunTask;
            _toolChangeCts?.Cancel();

            bool toolChangeStopped = toolChangeRunTask == null
                || await AwaitRunTeardownAsync(toolChangeRunTask, "Tool change", budget);

            bool millStopped = await StopMillingAsync(budget);

            Logger.Log("Mill stop complete");
            return toolChangeStopped && millStopped;
        }
        finally
        {
            _toolChangeAbortLock.Release();
        }
    }

    /// <summary>
    /// Tears down the milling controller alone, however its run ended. Split out from
    /// <see cref="HandleMillStopAsync"/> so a tool change that ends without success on
    /// its own (see StartToolChangeControllerAsync) can tear down the milling run it
    /// interrupted without calling back into the combined stop path and awaiting its own
    /// task from inside itself.
    ///
    /// Serialized on <see cref="_millStopLock"/>, bounded for the same reason as <see
    /// cref="HandleMillStopAsync"/>: cancelling _millCts wakes the controller
    /// .StartAsync() parked in HandleMillStart, which runs its own CleanupAsync +
    /// terminal-state transition as it unwinds. Also driving StopAsync/Reset from here
    /// at the same time races that unwind and can hit an illegal transition (e.g.
    /// Cancelled -> Cancelled, or Idle -> Cancelled), which throws. Awaiting the tracked
    /// run task lets that in-flight unwind own the transition, and the lock keeps two
    /// concurrent callers from both reaching Reset() afterward.
    /// </summary>
    /// <returns>False if the lock, or the run's unwind, did not complete within
    /// <see cref="ControllerCancelTimeoutMs"/>.</returns>
    private static async Task<bool> StopMillingAsync(Stopwatch? budget = null)
    {
        budget ??= Stopwatch.StartNew();

        if (_machine == null)
        {
            return true;
        }

        bool acquiredStopLock = await _millStopLock.WaitAsync(RemainingStopBudgetMs(budget));
        if (!acquiredStopLock)
        {
            Logger.Log("Mill stop: timed out waiting for a previous stop to finish");
            return false;
        }

        try
        {
            var controller = AppState.Milling;

            // Cancel unconditionally, before looking at State: Idle is also the state in
            // the window between /api/mill/start returning and the pool thread reaching
            // TransitionTo(Initializing), and a Stop arriving in that window must still
            // cancel the token the run is about to start honoring, not silently no-op.
            var runTask = _millRunTask;
            _millCts?.Cancel();

            if (runTask == null && controller.State == ControllerState.Idle)
            {
                return true;  // Nothing in flight and nothing left to reset.
            }

            Logger.Log("Mill stop tearing down at line {0}", _machine.FilePosition);

            bool stopped = true;
            if (runTask != null)
            {
                // A run is in flight: let its own cancellation unwind drive cleanup and
                // the terminal-state transition (see remarks above) instead of racing it.
                stopped = await AwaitRunTeardownAsync(runTask, "Mill", budget);
            }

            // Only once the run has finished unwinding. A teardown that overran is still
            // stopping the machine and lifting the tool, and a second reset would wipe the
            // lift it has queued - the same reason HandleProbeStop returns false instead.
            if (stopped)
            {
                await controller.ReleaseAsync();
            }

            Logger.Log("Mill stop complete");
            return stopped;
        }
        finally
        {
            _millStopLock.Release();
        }
    }

    /// <summary>
    /// What is left of a stop's time budget. A stop takes a lock, then another lock, then
    /// waits for the run to unwind; one budget spent across all three keeps a Stop from
    /// sitting unanswered for three times as long as the operator was promised.
    /// </summary>
    private static int RemainingStopBudgetMs(Stopwatch budget)
    {
        long left = ControllerCancelTimeoutMs - budget.ElapsedMilliseconds;
        return left > 0 ? (int)left : 0;
    }

    /// <summary>
    /// Waits for a controller's run task to unwind after cancellation, bounded so a stalled
    /// run cannot hang the caller. A fault is logged rather than rethrown, because the
    /// teardown that follows this call is the whole reason for waiting.
    /// </summary>
    /// <returns>True if the run task unwound within <see cref="ControllerCancelTimeoutMs"/>.</returns>
    private static async Task<bool> AwaitRunTeardownAsync(Task runTask, string label, Stopwatch? budget = null)
    {
        budget ??= Stopwatch.StartNew();
        int remaining = RemainingStopBudgetMs(budget);

        var completed = await Task.WhenAny(runTask, Task.Delay(remaining));
        if (completed != runTask)
        {
            Logger.Log("{0}: run task did not unwind within {1}ms of the stop budget", label, remaining);
            return false;
        }

        if (runTask.IsFaulted)
        {
            Logger.Log("{0}: run task faulted during teardown: {1}", label, runTask.Exception);
        }

        return true;
    }


    // Probe parameter limits are in WebConstants

    private static async Task HandleProbeSetup(HttpListenerResponse response, ProbeSetupRequest req)
    {
        // Setting up replaces the grid and deletes the autosave, which a running probe is
        // still writing into.
        if (AnyOperationRunning())
        {
            response.StatusCode = HttpStatusConflict;
            await WriteJson(response, new { success = false, error = ErrorMachineBusy });
            return;
        }

        if (AppState.CurrentFile == null)
        {
            response.StatusCode = HttpStatusBadRequest;
            await WriteJson(response, new { error = ErrorNoFileLoaded });
            return;
        }

        try
        {
            var file = AppState.CurrentFile;
            // Clamp values to safe ranges - client cannot specify arbitrary values
            var margin = Math.Clamp(req.margin ?? DefaultProbeMargin, MinProbeMargin, MaxProbeMargin);
            var gridSize = Math.Clamp(req.gridSize ?? DefaultProbeGridSize, MinProbeGridSize, MaxProbeGridSize);

            // Use shared setup method (single source of truth)
            var grid = AppState.SetupProbeGrid(
                new Vector2(file.Min.X, file.Min.Y),
                new Vector2(file.Max.X, file.Max.Y),
                margin,
                gridSize);

            await WriteJson(response, new
            {
                success = true,
                sizeX = grid.SizeX,
                sizeY = grid.SizeY,
                totalPoints = grid.TotalPoints,
                minX = grid.Min.X,
                minY = grid.Min.Y,
                maxX = grid.Max.X,
                maxY = grid.Max.Y
            });
        }
        catch (Exception ex)
        {
            await WriteFailure(response, "Probe setup", ex);
        }
    }

    /// <summary>Starts an outline trace, or says why it will not. See <see cref="StartMilling"/>.</summary>
    private static string? StartProbeTraceOutline()
    {
        if (!TryTakeProbeGrid(out var grid, out string? refusal))
        {
            return refusal;
        }

        // Published before the run is scheduled, so its finally cannot release a handle this
        // has not stored yet.
        var traceCts = new CancellationTokenSource();
        _probeCts = traceCts;
        _probeTask = Task.Run(() => TraceOutlineAsync(grid, traceCts));
        return null;
    }

    /// <summary>
    /// Releases what a probe run holds, if that run is still the current one. A run that
    /// outlives its successor's start clears nothing.
    /// </summary>
    private static async Task ReleaseProbeRunAsync(CancellationTokenSource cts)
    {
        if (!ReferenceEquals(_probeCts, cts))
        {
            Logger.Log("Probe run finished; a newer run owns the machine");
            return;
        }

        _probeCts = null;
        _probeTask = null;

        // Return the controller to Idle whatever the run left behind, so this server and
        // the controller cannot disagree about whether a run is going. Logged rather than
        // thrown: this runs from the run task's finally.
        try
        {
            await AppState.Probe.ReleaseAsync();
        }
        catch (Exception ex)
        {
            Logger.Log("Could not release the probe controller: {0}", ex.Message);
        }

        SleepPrevention.Stop();

        if (_machine != null)
        {
            _machine.EnableAutoStateClear = true;
        }

        StartIdleDisconnectTimer();
    }

    /// <summary>
    /// Takes the grid a probe run will work on, or says why there will be no run. The grid
    /// probe and the outline trace drive one controller, so only one may run.
    /// </summary>
    private static bool TryTakeProbeGrid(
        [NotNullWhen(true)] out ProbeGrid? grid, out string? refusal)
    {
        grid = null;

        // The same gate the terminal applies: grid coordinates are work coordinates, so
        // probing from an origin nobody set drives the tool to arbitrary XY.
        refusal = MenuHelpers.GetProbeDisabledReason();
        if (refusal != null)
        {
            return false;
        }

        if (AppState.Probe.IsRunInProgress)
        {
            Logger.Log("Probe start refused: controller is {0}, probe task {1}",
                AppState.Probe.State, _probeTask == null ? "absent" : "running");
            refusal = CliConstants.ProbeErrorAlreadyRunning;
            return false;
        }

        // Auto-load from autosave if probe data not in memory but exists on disk
        AppState.EnsureProbeDataLoaded();
        grid = AppState.ProbePoints;
        refusal = grid == null ? ErrorNoProbeGrid : null;
        return grid != null;
    }

    private static async Task TraceOutlineAsync(ProbeGrid grid, CancellationTokenSource cts)
    {
        var settings = AppState.Settings;
        var controller = AppState.Probe;

        // Start from Idle, whatever the last run left behind.
        await controller.ReleaseAsync();

        Logger.Log($"TraceOutline: tracing outline for {grid.SizeX}x{grid.SizeY} grid, " +
            $"traceHeight={settings.OutlineTraceHeight:F3}, traceFeed={settings.OutlineTraceFeed:F0}");

        // Configure controller with grid and trace options
        controller.LoadGrid(grid);
        controller.Options = ProbeOptions.FromSettings(settings, traceOutline: true);

        controller.ErrorOccurred += OnProbeError;

        // The tool moves for the length of the trace, so the door pauses it rather than
        // being cleared from under it, and the computer stays awake.
        if (_machine != null)
        {
            _machine.EnableAutoStateClear = false;
        }
        SleepPrevention.Start();

        try
        {
            await controller.TraceOutlineAsync(cts.Token);
            Logger.Log("TraceOutline: complete");
        }
        catch (OperationCanceledException)
        {
            Logger.Log("TraceOutline: cancelled");
        }
        catch (Exception ex)
        {
            Logger.Log("TraceOutline: failed - {0}", ex);
        }
        finally
        {
            controller.ErrorOccurred -= OnProbeError;
            await ReleaseProbeRunAsync(cts);
        }
    }

    /// <summary>Starts a grid probe, or says why it will not. See <see cref="StartMilling"/>.</summary>
    private static async Task<string?> StartProbing()
    {
        if (!TryTakeProbeGrid(out var grid, out string? refusal))
        {
            return refusal;
        }

        var controller = AppState.Probe;
        await controller.ReleaseAsync();

        Logger.Log($"StartProbing: starting grid probe {grid.SizeX}x{grid.SizeY} = {grid.TotalPoints} points");

        // Configure controller options
        // Web grid probe uses the same full settings mapping as the TUI.
        controller.Options = ProbeOptions.FromSettings(AppState.Settings, traceOutline: false);

        // Load the grid into controller (same object reference - updates in place)
        controller.LoadGrid(grid);

        // Wire up events for autosave
        controller.PointCompleted += OnProbePointCompleted;
        controller.ErrorOccurred += OnProbeError;

        // Disable auto state clear during probing
        if (_machine != null)
        {
            _machine.EnableAutoStateClear = false;
        }

        // A grid probe runs for tens of minutes with the probe down, and a suspend would
        // drop the link. The TUI does the same.
        SleepPrevention.Start();

        // Captured before Task.Run schedules the body that reads it, and compared against the
        // shared field before the finally releases anything. Same pattern as StartMilling.
        var probeCts = new CancellationTokenSource();
        _probeCts = probeCts;
        _probeTask = Task.Run(async () =>
        {
            try
            {
                await controller.StartAsync(probeCts.Token);

                // Complete - autosave already contains the data, no action needed
                if (controller.State == ControllerState.Completed)
                {
                    Logger.Log("StartProbing: probing complete, data in autosave");
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Log("StartProbing: probing cancelled");
            }
            catch (Exception ex)
            {
                Logger.Log($"StartProbing: error - {ex.Message}");
            }
            finally
            {
                controller.PointCompleted -= OnProbePointCompleted;
                controller.ErrorOccurred -= OnProbeError;
                await ReleaseProbeRunAsync(probeCts);
            }
        });

        return null;
    }

    private static void OnProbePointCompleted(int index, Vector2 coords, double z)
    {
        Persistence.SaveProbeProgress();
        Logger.Log($"Probe point {index + 1} complete: ({coords.X:F3}, {coords.Y:F3}) Z={z:F3}");
    }

    private static void OnProbeError(ControllerError error)
    {
        Logger.Log($"Probe error: {error.Message}");
        BroadcastMessage(WsMessageTypeProbeError, new { message = error.Message, isFatal = error.IsFatal });
    }

    /// <summary>
    /// Stops probing, and waits for the run's own teardown rather than driving the machine
    /// alongside it. That teardown stops the machine and then lifts the tool clear
    /// (ProbeController.CleanupAsync). Returns a Task rather than being async void, because
    /// an exception from an async void method is rethrown on the thread pool and ends the
    /// process while the machine is moving.
    /// </summary>
    /// <returns>False if the run did not unwind in time, so the machine may still be moving.</returns>
    private static async Task<bool> HandleProbeStop()
    {
        var runTask = _probeTask;
        _probeCts?.Cancel();

        if (runTask == null)
        {
            // No run in progress, so nothing has stopped the machine: stop it here.
            if (_machine != null)
            {
                await MachineWait.StopAndResetAsync(_machine);
            }

            // The controller can still be claiming a run this server has no task for.
            // Stop is the operator's way out of that, so it releases the controller here.
            try
            {
                await AppState.Probe.ReleaseAsync();
            }
            catch (Exception ex)
            {
                Logger.Log("Probe stop could not release the controller: {0}", ex.Message);
                return false;
            }

            return true;
        }

        // A teardown that overruns has already stopped the machine - that is its first act -
        // and is partway through the lift. Return false rather than send a second reset, which
        // would cancel the lift.
        return await AwaitRunTeardownAsync(runTask, "Probe");
    }

    private static object GetProbeStatus()
    {
        // state = 4-state model (none/ready/partial/complete); hasUnsavedData = autosave exists.
        var (grid, state, hasUnsavedData) = ReadProbeStateSnapshot();

        if (grid == null)
        {
            return new
            {
                active = false,
                hasUnsavedData,
                state
            };
        }

        var controller = AppState.Probe;
        var controllerState = controller.State;
        bool isPaused = ControllerBase.IsPausedState(controllerState);

        return new
        {
            active = AppState.IsMeasuringGrid,
            hasUnsavedData,
            paused = isPaused,
            progress = grid.Progress,
            total = grid.TotalPoints,
            sizeX = grid.SizeX,
            sizeY = grid.SizeY,
            hasHeights = grid.HasValidHeights,
            minHeight = grid.HasValidHeights ? grid.MinHeight : 0,
            maxHeight = grid.HasValidHeights ? grid.MaxHeight : 0,
            points = GetProbePointsArray(grid),
            colours = GetProbeColoursArray(grid),
            phase = controller.Phase.ToString(),
            state
        };
    }

    /// <summary>
    /// The values every probe-status response derives from the current grid, computed once
    /// so the brief and full status builders can never disagree on state or unsaved-data.
    /// </summary>
    private static (ProbeGrid? grid, string state, bool hasUnsavedData) ReadProbeStateSnapshot()
    {
        // One read answers both questions, so the state and the Save/Recover buttons
        // cannot describe different data. The autosave stands in when nothing is loaded,
        // reported without being adopted - see rule no-side-effect-on-get.
        var usableAutosave = AppState.ReadUsableAutosave();
        var grid = AppState.ProbePoints ?? usableAutosave;
        return (grid, ComputeProbeState(grid), usableAutosave != null);
    }

    private static string ComputeProbeState(ProbeGrid? grid)
    {
        if (grid == null)
        {
            return ProbeStateNone;
        }
        if (grid.HasCompleteData)
        {
            return ProbeStateComplete;
        }
        if (grid.Progress > 0)
        {
            return ProbeStatePartial;
        }
        return ProbeStateReady;
    }

    private static object?[][] GetProbePointsArray(ProbeGrid grid)
    {
        var result = new object?[grid.SizeX][];
        for (int x = 0; x < grid.SizeX; x++)
        {
            result[x] = new object?[grid.SizeY];
            for (int y = 0; y < grid.SizeY; y++)
            {
                result[x][y] = grid.Points[x, y];
            }
        }
        return result;
    }

    /// <summary>
    /// The color for each measured node, as CSS. Worked out here rather than in the
    /// browser so both views of the map are drawn from one calculation.
    ///
    /// Null where a node has no height yet, and null throughout until something has been
    /// measured, since a range needs two readings to mean anything.
    /// </summary>
    private static string?[][] GetProbeColoursArray(ProbeGrid grid)
    {
        double min = grid.MinHeight;
        double max = grid.MaxHeight;
        double range = max - min;
        bool hasRange = grid.HasValidHeights && range > HeightRangeEpsilon;

        var result = new string?[grid.SizeX][];
        for (int x = 0; x < grid.SizeX; x++)
        {
            result[x] = new string?[grid.SizeY];
            for (int y = 0; y < grid.SizeY; y++)
            {
                double? height = grid.Points[x, y];
                if (!height.HasValue)
                {
                    continue;
                }

                // A flat board has no range to spread the gradient over, so every
                // measured node takes the midpoint rather than an arbitrary end.
                double fraction = hasRange ? (height.Value - min) / range : 0.5;
                var (r, g, b) = HeightGradient.Colour(fraction);
                result[x][y] = $"rgb({r}, {g}, {b})";
            }
        }
        return result;
    }

    private static async Task HandleWebSocket(HttpListenerContext context)
    {
        ClientConnection? client = null;
        string? clientId = null;

        // Extract client ID from query string (e.g., /ws?clientId=abc123)
        var query = context.Request.QueryString;
        clientId = query[QueryParamClientId];

        // Check if another client is already connected (web or TUI via proxy)
        bool hasOtherClient = false;
        lock (_clientsLock)
        {
            // Clean up expired pending clients first
            PurgeExpiredPendingClients();

            // Count other web clients (different clientId)
            int otherClients = _clients.Count(c => c.Id != null && c.Id != clientId);
            int otherPending = _pendingClients.Count(kvp => kvp.Key != clientId);
            int anonymousClients = _clients.Count(c => c.Id == null);

            hasOtherClient = otherClients > 0 || otherPending > 0 || anonymousClients > 0;

            if (hasOtherClient)
            {
                Logger.Log("WebSocket: other web client detected: clientId={0}, otherClients={1}, otherPending={2}, anonymous={3}",
                    clientId ?? "null", otherClients, otherPending, anonymousClients);
            }
            else if (clientId != null)
            {
                // Reserve this slot by adding to pending (prevents race with other WebSocket requests)
                _pendingClients[clientId] = DateTime.Now;
            }
        }

        // Also check if TUI is connected via proxy
        bool proxyHasClient = HasProxyClient?.Invoke() ?? false;
        if (proxyHasClient)
        {
            hasOtherClient = true;
            Logger.Log("WebSocket: TUI client detected via proxy");
        }

        try
        {
            var wsContext = await context.AcceptWebSocketAsync(null);
            var webSocket = wsContext.WebSocket;
            client = new ClientConnection
            {
                Socket = webSocket,
                Id = clientId,
                LastActivity = DateTime.Now
            };

            var clientAddress = context.Request.RemoteEndPoint?.Address?.ToString();

            lock (_clientsLock)
            {
                _forceDisconnected = false;  // Reset: new client means normal reconnect behavior
                if (clientId != null)
                {
                    int stale = _clients.RemoveAll(
                        c => IsSupersededClient(c.Id, c.Socket.State, clientId));
                    if (stale > 0)
                    {
                        Logger.Log("Removed stale WebSocket for client {0}", clientId);
                    }
                    // Remove from pending - now fully connected
                    _pendingClients.Remove(clientId);
                }
                _clients.Add(client);
                _webClientAddress = clientAddress;
            }

            Logger.Log("WebSocket client connected (clientId={0}, address={1})", clientId ?? "none", clientAddress ?? "unknown");

            // If another client is connected (web or TUI), send error and let client show modal
            if (hasOtherClient)
            {
                Logger.Log("WebSocket: another client connected, sending connection error");
                var errorJson = JsonSerializer.Serialize(new
                {
                    type = WsMessageTypeConnectionError,
                    data = new { error = ProxyConnectionRejected }
                });
                await SendToClientAsync(client, Encoding.UTF8.GetBytes(errorJson));
                // Don't close immediately - let client handle the modal
                // Client will reload after force disconnect
            }

            // Cancel any pending idle disconnect timer
            CancelIdleDisconnectTimer();

            // Connect Machine to proxy if not already connected (and no other client blocking)
            Logger.Log($"WebSocket: _machine={(_machine == null ? "null" : "set")}, Connected={_machine?.Connected}, hasOtherClient={hasOtherClient}");
            if (_machine != null && !_machine.Connected && !hasOtherClient)
            {
                string? rejectionMessage = null;

                // Listen for rejection messages from proxy (same pattern as TUI's TryConnect)
                void OnLineReceived(string line)
                {
                    if (line.StartsWith(ProxyConnectionRejectedPrefix) || line.StartsWith(ProxySerialPortInUsePrefix))
                    {
                        rejectionMessage = line;
                    }
                }

                _machine.LineReceived += OnLineReceived;
                try
                {
                    Logger.Log("Connecting Machine to proxy for web client");
                    _machine.Connect();

                    // Wait briefly for rejection message (proxy sends it immediately after TCP connect)
                    await Task.Delay(ProxyRejectionCheckDelayMs);

                    if (rejectionMessage != null)
                    {
                        Logger.Log($"Connection rejected by proxy: {rejectionMessage}");
                        _machine.Disconnect();
                        BroadcastMessage(WsMessageTypeConnectionError, new { error = rejectionMessage });
                    }
                    else
                    {
                        _machine.EnableAutoStateClear = true;  // Auto-clear Door/Alarm states
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log("Failed to connect Machine: {0}", ex);
                    BroadcastMessage(WsMessageTypeConnectionError, new { error = ErrorMachineNotConnected });
                }
                finally
                {
                    _machine.LineReceived -= OnLineReceived;
                }
            }

            var buffer = new byte[WebSocketBufferSize];
            // A message larger than the buffer arrives in several frames, so it is
            // gathered here until the last one before anything tries to read it.
            var pending = new MemoryStream();
            bool discarding = false;
            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    _cts?.Token ?? CancellationToken.None);

                lock (_clientsLock)
                {
                    client.LastActivity = DateTime.Now;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                // Drop the remaining frames of a message already refused, so they are not
                // read as a message of their own.
                if (discarding)
                {
                    discarding = !result.EndOfMessage;
                    continue;
                }

                if (pending.Length + result.Count > WebSocketMaxMessageBytes)
                {
                    Logger.Log("WebSocket message over {0} bytes refused", WebSocketMaxMessageBytes);
                    pending.SetLength(0);
                    discarding = !result.EndOfMessage;
                    continue;
                }

                pending.Write(buffer, 0, result.Count);

                if (!result.EndOfMessage)
                {
                    continue;
                }

                HandleWebSocketMessage(Encoding.UTF8.GetString(pending.ToArray()));
                pending.SetLength(0);
            }
        }
        catch (WebSocketException ex)
        {
            Logger.Log($"WebSocket exception: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            Logger.Log("WebSocket cancelled (server shutting down)");
        }
        catch (Exception ex)
        {
            Logger.Log($"WebSocket unexpected exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (client != null)
            {
                int remainingClients;
                lock (_clientsLock)
                {
                    _clients.Remove(client);
                    remainingClients = _clients.Count;
                    if (remainingClients == 0)
                    {
                        _webClientAddress = null;
                    }
                }
                Logger.Log("WebSocket client disconnected");

                // Disconnect Machine when last web client disconnects (frees proxy slot for TUI)
                // BUT only if no operation is in progress
                bool operationInProgress = AnyOperationRunning();

                if (remainingClients == 0 && _machine != null && _machine.Connected && !operationInProgress)
                {
                    Logger.Log("Last web client disconnected, disconnecting Machine to free proxy slot");
                    _machine.Disconnect();
                }
                else if (remainingClients == 0 && operationInProgress)
                {
                    Logger.Log("Last web client disconnected, but operation in progress - keeping Machine connected");
                }
            }
        }
    }

    private static void HandleWebSocketMessage(string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;

            var type = ReadString(root, WsFieldType);

            // The commands that just hand an instruction to the machine live in the table
            // the HTTP endpoints use, so the two doors cannot drift apart.
            if (type == WsCmdPing)
            {
                // Receiving it already refreshed this client's activity; nothing else to do.
                return;
            }

            var direct = FindWsCommand(type);
            if (direct != null)
            {
                string? refusal = RunDirectCommand(direct, offTheCallingThread: true);
                if (refusal != null)
                {
                    Logger.Log("WebSocket command {0} refused: {1}", direct.WsCommand!, refusal);
                }
                return;
            }

            // Only the jog command is left here: it carries a payload of its own, and it is
            // the one command with nothing to answer. Zeroing can be refused, and a refusal
            // needs somewhere to go, so the jog screen asks for that over HTTP.
            if (type == WsCmdJogMode)
            {
                HandleJogWithMode(
                    ReadString(root, WsFieldAxis),
                    ReadInt(root, WsFieldDirection, 0),
                    ReadInt(root, WsFieldModeIndex, DefaultJogModeIndex));
            }
        }
        catch (JsonException)
        {
            // Invalid JSON, ignore
        }
    }

    /// <summary>
    /// A number from a client message, or <paramref name="fallback"/> when the field is
    /// absent or is not a number. A wrong-shaped field must not throw out of the receive
    /// loop, which would drop the connection.
    /// </summary>
    private static int ReadInt(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int parsed)
            ? parsed
            : fallback;

    /// <summary>True only if the field is present and is JSON true.</summary>
    private static bool ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>The field's text, or null if it is absent or is not a string.</summary>
    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// An array of strings, or null when the field is absent or is not an array. Elements
    /// that are not strings are dropped; every caller whitelists what it accepts anyway.
    /// </summary>
    private static string[]? ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return value.EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.String)
            .Select(element => element.GetString()!)
            .ToArray();
    }

    /// <summary>
    /// Sends one frame to one client. A WebSocket accepts one send at a time, so each
    /// client's sends queue behind its own lock. <paramref name="dropIfBusy"/> drops a
    /// message whose next copy is along shortly rather than queueing it.
    /// </summary>
    private static async Task SendToClientAsync(ClientConnection client, byte[] bytes, bool dropIfBusy = false)
    {
        if (dropIfBusy && !await client.SendLock.WaitAsync(0))
        {
            return;
        }

        if (!dropIfBusy)
        {
            await client.SendLock.WaitAsync();
        }

        try
        {
            if (client.Socket.State == WebSocketState.Open)
            {
                await client.Socket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None);
            }
        }
        catch
        {
            // The client went away mid-send; its receive loop removes it.
        }
        finally
        {
            client.SendLock.Release();
        }
    }

    /// <summary>
    /// Sends a message to every connected client. The one place a WebSocket message leaves
    /// this server.
    /// </summary>
    private static void BroadcastMessage(string type, object data, bool dropIfBusy = false)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, data }));

        List<ClientConnection> clientsCopy;
        lock (_clientsLock)
        {
            clientsCopy = _clients.ToList();
        }

        foreach (var client in clientsCopy)
        {
            _ = SendToClientAsync(client, bytes, dropIfBusy);
        }
    }

    private static async Task BroadcastStatusLoop(CancellationToken ct)
    {
        bool wasConnected = _machine?.Connected ?? false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(WebConstants.WebSocketBroadcastIntervalMs, ct);

                // Check for disconnection and attempt reconnect
                bool isConnected = _machine?.Connected ?? false;
                if (wasConnected && !isConnected)
                {
                    Logger.Log("BroadcastStatusLoop: machine disconnected, starting reconnect attempts");
                    _ = TryReconnectLoop(ct);
                }
                wasConnected = isConnected;

                List<ClientConnection> staleClients;
                lock (_clientsLock)
                {
                    var now = DateTime.Now;
                    staleClients = _clients
                        .Where(c => (now - c.LastActivity).TotalMilliseconds > WebSocketTimeoutMs)
                        .ToList();
                    _clients.RemoveAll(staleClients.Contains);
                }

                foreach (var stale in staleClients)
                {
                    Logger.Log("Closing stale WebSocket client (silent for {0}ms)", WebSocketTimeoutMs);
                    try
                    {
                        await stale.Socket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure, WsCloseReasonTimeout, ct)
                            .WaitAsync(TimeSpan.FromMilliseconds(ForceDisconnectCloseTimeoutMs), ct);
                    }
                    catch
                    {
                        // Already closed
                    }
                }

                // Dropped rather than queued for a client that has stopped reading: the
                // next snapshot is 300ms away and is the one worth having.
                BroadcastMessage(WsMessageTypeStatus, GetStatus(), dropIfBusy: true);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // The browser takes its screen lock from this stream, so it keeps running:
                // a snapshot that failed to build is better skipped than fatal.
                Logger.Log("Status broadcast failed: {0}", ex);
            }
        }
    }

    private static async Task TryReconnectLoop(CancellationToken ct)
    {
        // Skip reconnect if force-disconnected (TUI taking over)
        if (_forceDisconnected)
        {
            Logger.Log("TryReconnectLoop: skipping, force-disconnected by TUI");
            return;
        }

        lock (_reconnectLock)
        {
            if (_isReconnecting)
            {
                return; // Already reconnecting
            }
            _isReconnecting = true;
        }

        int attempts = 0;
        try
        {
            // Initial delay before first reconnect attempt (gives TUI time to take over if needed)
            await Task.Delay(ReconnectIntervalMs, ct);

            while (!ct.IsCancellationRequested && _machine != null && !_machine.Connected && !_forceDisconnected)
            {
                attempts++;
                Logger.Log($"TryReconnectLoop: attempt {attempts}");

                try
                {
                    // Reconnect existing machine
                    _machine.Connect();

                    if (_machine.Connected)
                    {
                        Logger.Log($"TryReconnectLoop: reconnected after {attempts} attempts");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"TryReconnectLoop: attempt {attempts} failed: {ex.Message}");
                }

                await Task.Delay(ReconnectIntervalMs, ct);

                if (ReconnectMaxAttempts > 0 && attempts >= ReconnectMaxAttempts)
                {
                    Logger.Log($"TryReconnectLoop: gave up after {attempts} attempts");
                    break;
                }
            }
        }
        finally
        {
            lock (_reconnectLock)
            {
                _isReconnecting = false;
            }
        }
    }

    private static async Task ServeStaticFile(HttpListenerContext context, string path)
    {
        var response = context.Response;

        // Default to index.html
        if (path == "/")
        {
            path = "/index.html";
        }

        // Check if this is a request that will serve index.html (direct or SPA fallback)
        bool willServeIndexHtml = path == "/index.html";

        // Remove leading slash for resource lookup
        var resourcePath = "coppercli.WebServer.wwwroot" + path.Replace('/', '.');

        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourcePath);

        // SPA routing: paths without extension that don't map to a file serve index.html
        if (stream == null && path.IndexOf('.') < 0)
        {
            willServeIndexHtml = true;
        }

        // If serving index.html and another client is connected, show "already connected" page
        if (willServeIndexHtml)
        {
            // Extract client ID from cookie if present
            string? requestClientId = null;
            var cookies = context.Request.Cookies;
            if (cookies[ClientIdCookieName] != null)
            {
                requestClientId = cookies[ClientIdCookieName]?.Value;
            }

            lock (_clientsLock)
            {
                PurgeExpiredPendingClients();

                Logger.Log("ServeStaticFile: path={0}, requestClientId={1}, clients={2}, clientIds={3}, pending={4}",
                    path, requestClientId ?? "null", _clients.Count,
                    _clients.Count(c => c.Id != null), _pendingClients.Count);
            }

            // The page is served whatever else is connected; the WebSocket handler detects
            // the conflict and the UI shows one force-disconnect modal.

            // Generate a client ID if this browser does not have one yet.
            if (requestClientId == null)
            {
                requestClientId = Guid.NewGuid().ToString("N");
            }

            // Deliberately no pending-client reservation here. Serving a page is a GET, and
            // a GET arrives from anywhere a browser can be pointed - an <img> on another
            // site reaches this line with no Origin to check. Reserving a slot per page
            // fetch let such a page fill the single client slot from a distance, so the
            // operator's own UI then found the machine "already connected" and offered a
            // force-disconnect mid-job. HandleWebSocket makes the reservation instead: the
            // upgrade always carries an Origin, so it is the first point that can be trusted.

            // Set/refresh the cookie
            response.SetCookie(new Cookie(ClientIdCookieName, requestClientId)
            {
                Path = "/",
                HttpOnly = false,  // JavaScript needs to read it for WebSocket
            });
        }

        if (stream == null)
        {
            // Try to serve index.html for SPA routing (already checked for other clients above)
            if (path.IndexOf('.') < 0)
            {
                resourcePath = "coppercli.WebServer.wwwroot.index.html";
                using var indexStream = assembly.GetManifestResourceStream(resourcePath);
                if (indexStream != null)
                {
                    response.ContentType = ContentTypeHtml;
                    await indexStream.CopyToAsync(response.OutputStream);
                    response.Close();
                    return;
                }
            }

            response.StatusCode = HttpStatusNotFound;
            await WriteText(response, ErrorNotFound);
            return;
        }

        // Set content type and disable caching
        response.ContentType = GetContentType(path);
        response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
        response.Headers.Add("Pragma", "no-cache");
        response.Headers.Add("Expires", "0");
        await stream.CopyToAsync(response.OutputStream);
        response.Close();
    }

    private static string GetContentType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".html" => ContentTypeHtml,
            ".css" => ContentTypeCss,
            ".js" => ContentTypeJs,
            ".json" => ContentTypeJson,
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            _ => "application/octet-stream"
        };
    }

    private static async Task WriteJson(HttpListenerResponse response, object data)
    {
        response.ContentType = ContentTypeJson;
        var json = JsonSerializer.Serialize(data);
        var bytes = Encoding.UTF8.GetBytes(json);
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    /// <summary>
    /// Answers a Stop or Abort request. <paramref name="stopped"/> is false when
    /// <see cref="HandleMillStopAsync"/> could not confirm both controllers actually
    /// finished tearing down within their time budget - the caller must not tell the
    /// operator the machine has stopped in that case.
    /// </summary>
    private static async Task WriteStopResult(HttpListenerResponse response, bool stopped)
    {
        if (stopped)
        {
            await WriteJson(response, new { success = true });
        }
        else
        {
            response.StatusCode = HttpStatusServerError;
            await WriteJson(response, new { error = CliConstants.StopTimedOutWarning });
        }
    }

    /// <summary>
    /// Answers a request that failed on something the operator cannot see. The exception
    /// goes to the log; a plain sentence goes to the screen.
    /// </summary>
    private static async Task WriteFailure(HttpListenerResponse response, string what, Exception ex)
    {
        Logger.Log("{0} failed: {1}", what, ex);
        response.StatusCode = HttpStatusServerError;
        await WriteJson(response, new { error = ErrorServerFailure });
    }

    /// <summary>Writes a bare sentence, for a response a person reads rather than the UI.</summary>
    private static async Task WriteText(HttpListenerResponse response, string text)
    {
        response.ContentType = ContentTypeText;
        var bytes = Encoding.UTF8.GetBytes(text);
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    /// <summary>
    /// Reads a request body into memory, up to <paramref name="maxBytes"/>. Content-Length is
    /// only a claim, so the read is capped as it goes rather than trusting it.
    /// </summary>
    /// <returns>The body, or null if it is larger than the limit.</returns>
    private static async Task<byte[]?> ReadBodyBytes(HttpListenerRequest request, int maxBytes)
    {
        if (request.ContentLength64 > maxBytes)
        {
            return null;
        }

        using var body = new MemoryStream();
        var chunk = new byte[BodyReadChunkBytes];
        int read;
        while ((read = await request.InputStream.ReadAsync(chunk)) > 0)
        {
            if (body.Length + read > maxBytes)
            {
                return null;
            }
            body.Write(chunk, 0, read);
        }

        return body.ToArray();
    }

    /// <summary>
    /// Reads and parses a JSON request body, answering the request itself when the body is
    /// too large, is not valid JSON, or is missing. A null result means it has been answered.
    /// </summary>
    private static async Task<T?> ReadBody<T>(HttpListenerRequest request, HttpListenerResponse response)
        where T : class
    {
        byte[]? body = await ReadBodyBytes(request, RequestBodyMaxBytes);
        if (body == null)
        {
            response.StatusCode = HttpStatusPayloadTooLarge;
            await WriteJson(response, new { error = ErrorBodyTooLarge });
            return null;
        }

        T? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<T>(
                (request.ContentEncoding ?? Encoding.UTF8).GetString(body));
        }
        catch (JsonException)
        {
            parsed = null;
        }

        if (parsed == null)
        {
            response.StatusCode = HttpStatusBadRequest;
            await WriteJson(response, new { error = ErrorInvalidRequest });
        }

        return parsed;
    }

    // Request DTOs
    private record ConnectRequest
    {
        public string? port { get; init; }
        public int? baud { get; init; }
    }

    private record ZeroRequest
    {
        public string[]? axes { get; init; }
    }

    private record LoadFileRequest
    {
        public string? path { get; init; }
    }

    private record ProbeSetupRequest
    {
        public double? margin { get; init; }
        public double? gridSize { get; init; }
    }

    private record ProbeSaveRequest
    {
        public string? path { get; init; }
    }

    private record ProbeLoadRequest
    {
        public string? path { get; init; }
    }

    private record SettingsUpdateRequest
    {
        public string? machineProfile { get; init; }
        public double? probeFeed { get; init; }
        public double? probeMaxDepth { get; init; }
        public double? probeSafeHeight { get; init; }
        public double? probeMinimumHeight { get; init; }
        public double? outlineTraceHeight { get; init; }
        public double? outlineTraceFeed { get; init; }
        public double? toolSetterX { get; init; }
        public double? toolSetterY { get; init; }
    }

    private record SessionRestoreAnswerRequest
    {
        public string? topic { get; init; }
        public bool? yes { get; init; }
    }

    private record DepthAdjustmentRequest
    {
        public double? depth { get; init; }
        public string? action { get; init; }  // "increase", "decrease", "reset"
    }

    private record ToolChangeUserInputRequest
    {
        /// <summary>The prompt being answered, as broadcast with it.</summary>
        public string? id { get; init; }

        public string? response { get; init; }  // e.g., "Continue" or "Abort"
    }

    private static async Task HandleProbeSave(HttpListenerResponse response, ProbeSaveRequest req)
    {
        // The same map the status announced: saving moves the autosave, so it needs nothing
        // in memory.
        var probePoints = AppState.CurrentProbeGrid;

        if (probePoints == null || !probePoints.HasCompleteData)
        {
            response.StatusCode = HttpStatusBadRequest;
            await WriteJson(response, new { error = ErrorNoCompleteProbeData });
            return;
        }

        if (string.IsNullOrEmpty(req.path))
        {
            response.StatusCode = HttpStatusBadRequest;
            await WriteJson(response, new { error = ErrorNoPathSpecified });
            return;
        }

        try
        {
            var path = PathHelpers.ExpandTilde(req.path);

            // Ensure .pgrid extension
            if (!path.EndsWith(".pgrid", StringComparison.OrdinalIgnoreCase))
            {
                path += ".pgrid";
            }

            // Convert to absolute path
            if (!Path.IsPathRooted(path))
            {
                var baseDir = AppState.Session.LastProbeBrowseDirectory;
                if (string.IsNullOrEmpty(baseDir))
                {
                    baseDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                }
                path = Path.Combine(baseDir, path);
            }

            // Move autosave to user's chosen location
            if (!Persistence.SaveProbeToFile(path))
            {
                response.StatusCode = HttpStatusServerError;
                await WriteJson(response, new { error = ErrorProbeSaveFailed });
                return;
            }

            // Update probe browse directory (separate from G-code browse directory)
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                AppState.Session.LastProbeBrowseDirectory = dir;
                Persistence.SaveSession();
            }

            await WriteJson(response, new { success = true, path = Path.GetFullPath(path) });
        }
        catch (Exception ex)
        {
            await WriteFailure(response, "Probe save", ex);
        }
    }

    private static async Task HandleProbeLoad(HttpListenerResponse response, ProbeLoadRequest req)
    {
        if (string.IsNullOrEmpty(req.path))
        {
            response.StatusCode = HttpStatusBadRequest;
            await WriteJson(response, new { error = ErrorNoPathSpecified });
            return;
        }

        try
        {
            var path = PathHelpers.ExpandTilde(req.path);

            if (!File.Exists(path))
            {
                response.StatusCode = HttpStatusNotFound;
                await WriteJson(response, new { error = ErrorFileNotFound });
                return;
            }

            // Single source for the load ritual (reloads original G-code first if a grid was
            // already applied, so this grid is not applied on top of the old one).
            var grid = AppState.LoadProbeGridFromFile(path);

            // Don't copy to autosave - loaded data is already saved (came from a file).
            // Autosave is only for data from active probing that hasn't been saved yet.
            // Clear any stale autosave to prevent "unsaved probe data" prompts.
            Persistence.ClearProbeAutoSave();

            // Auto-apply if probe is complete
            bool complete = grid.HasCompleteData;
            if (complete)
            {
                AppState.ApplyProbeData();
            }

            // Update browse directory
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                AppState.Session.LastProbeBrowseDirectory = dir;
            }
            Persistence.SaveSession();

            await WriteJson(response, new
            {
                success = true,
                sizeX = grid.SizeX,
                sizeY = grid.SizeY,
                totalPoints = grid.TotalPoints,
                progress = grid.Progress,
                complete,
                applied = AppState.AreProbePointsApplied
            });
        }
        catch (Exception ex)
        {
            await WriteFailure(response, "Probe load", ex);
        }
    }

    private static object GetProbeFiles(string dirPath) =>
        GetFilesWithFilter(dirPath, ext => ext == ".pgrid");

    /// <summary>
    /// Forces disconnect of all connected WebSocket clients and releases the serial port.
    /// Used by TUI when it needs to take over from web clients.
    /// Returns the number of clients that were disconnected.
    /// </summary>
    public static int ForceDisconnectAllClients()
    {
        // Suppress auto-reconnect so TUI can take over
        _forceDisconnected = true;

        List<ClientConnection> clientsToClose;
        lock (_clientsLock)
        {
            clientsToClose = _clients.ToList();
            _pendingClients.Clear();
            _clients.Clear();
            _webClientAddress = null;
        }

        Logger.Log($"ForceDisconnectAllClients: closing {clientsToClose.Count} client(s), suppressing auto-reconnect");

        foreach (var client in clientsToClose)
        {
            try
            {
                if (client.Socket.State == WebSocketState.Open)
                {
                    client.Socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        WsCloseReasonForceDisconnect,
                        CancellationToken.None).Wait(ForceDisconnectCloseTimeoutMs);
                }
            }
            catch
            {
                // Already closed
            }
        }

        // Disconnect Machine to release serial port
        if (_machine != null && _machine.Connected)
        {
            Logger.Log("ForceDisconnectAllClients: disconnecting Machine to release serial port");
            _machine.Disconnect();
        }

        // Also kick any TUI client from the proxy (if callback is wired up)
        if (ForceDisconnectProxyClient?.Invoke() == true)
        {
            Logger.Log("ForceDisconnectAllClients: kicked TUI client from proxy");
        }

        return clientsToClose.Count;
    }

    private static async Task HandleForceDisconnect(HttpListenerResponse response)
    {
        int disconnected = ForceDisconnectAllClients();
        await WriteJson(response, new { success = true, disconnected });
    }

    /// <summary>
    /// Clears probe data from memory and deletes the autosave file.
    /// In the single-file model, Clear and Discard are the same operation.
    /// </summary>
    /// <returns>False if the autosave is still on disk, so the operator is not told the
    /// data is gone while it waits to be offered again.</returns>
    private static bool HandleProbeDiscard()
    {
        bool cleared = AppState.DiscardProbeDataAndAutosave();
        Logger.Log("HandleProbeDiscard: autosave cleared={0}", cleared);
        return cleared;
    }

    /// <summary>
    /// Auto-start tool change controller when M6 is detected.
    /// This is the FSM-driven approach: server controls the workflow, client just observes.
    /// The ToolChangeController.State and Phase are the single source of truth.
    /// </summary>
    /// <param name="cts">Created and assigned to <see cref="_toolChangeCts"/> by the
    /// caller before this task was scheduled - see the onToolChange callback in
    /// HandleMillStart. Used as this run's identity for the compare-and-clear in the
    /// finally below, and as the cancellation source for the tool change itself.</param>
    private static async Task StartToolChangeControllerAsync(ToolChangeInfo info, MillingController millingController, CancellationTokenSource cts)
    {
        if (!MachineConnected)
        {
            Logger.Log("StartToolChangeControllerAsync: machine not connected");

            // The caller already published cts/this task to the shared fields before
            // scheduling this run (see the onToolChange callback in HandleMillStart);
            // returning here without the compare-and-clear below would leave them
            // pointing at a run that never really started, forever.
            if (ReferenceEquals(_toolChangeCts, cts))
            {
                _toolChangeCts = null;
                _toolChangeRunTask = null;
            }
            return;
        }

        Logger.Log("StartToolChangeControllerAsync: starting for T{0}", info.ToolNumber);

        var toolChangeController = AppState.ToolChange;

        // Clear the last tool change off the controller, whatever state it left behind.
        await toolChangeController.ReleaseAsync();

        // Set options from user settings and file bounds
        var settings = AppState.Settings;
        var currentFile = AppState.CurrentFile;
        toolChangeController.Options = ToolChangeOptions.FromSettings(settings, currentFile);

        // Subscribe to tool change controller events
        Action<ControllerState> onStateChanged = state =>
        {
            Logger.Log("Tool change state: {0}", state);
            BroadcastMessage(WsMessageTypeToolChangeState, new { state = state.ToString() });
        };
        // Throttle progress broadcasts to avoid overwhelming WebSocket
        // But always broadcast phase changes immediately
        DateTime lastToolChangeProgressBroadcast = DateTime.MinValue;
        string? lastToolChangePhase = null;
        Action<ProgressInfo> onProgressChanged = progress =>
        {
            var now = DateTime.Now;
            bool phaseChanged = progress.Phase != lastToolChangePhase;
            if (!phaseChanged && (now - lastToolChangeProgressBroadcast).TotalMilliseconds < WebConstants.WebSocketBroadcastIntervalMs)
            {
                return;  // Skip this update, same phase and too soon since last broadcast
            }
            lastToolChangeProgressBroadcast = now;
            lastToolChangePhase = progress.Phase;
            BroadcastMessage(WsMessageTypeToolChangeProgress, new
            {
                phase = progress.Phase,
                percentage = progress.Percentage,
                message = progress.Message
            });
        };
        // As in HandleMillStart: this run takes down its own question, not one the milling
        // run has put up since.
        UserInputRequest? published = null;
        Action<UserInputRequest> onUserInputRequired = request =>
        {
            Logger.Log("Tool change user input required: {0}", request.Message);
            published = request;
            PendingPrompt.Set(request);
            BroadcastMessage(WsMessageTypeToolChangeInput, new
            {
                title = request.Title,
                message = request.Message,
                options = request.Options,
                id = request.Id
            });
        };
        Action<ControllerError> onError = error =>
        {
            Logger.Log("Tool change error: {0}", error.Message);
            BroadcastMessage(WsMessageTypeToolChangeError, new
            {
                message = error.Message,
                isFatal = error.IsFatal
            });
        };

        toolChangeController.StateChanged += onStateChanged;
        toolChangeController.ProgressChanged += onProgressChanged;
        toolChangeController.UserInputRequired += onUserInputRequired;
        toolChangeController.ErrorOccurred += onError;

        try
        {
            bool success = await toolChangeController.HandleToolChangeAsync(info, cts.Token);

            if (success)
            {
                Logger.Log("Tool change complete, resuming milling");
                BroadcastMessage(WsMessageTypeToolChangeComplete, new { success = true });

                // The operator may have pressed Stop while the tool change was still
                // running, which cancels the milling controller independently of this
                // workflow. Resume() throws on anything but Paused, so resume only a
                // milling controller still parked waiting for this tool change.
                if (millingController.IsPaused)
                {
                    millingController.Resume();
                }
            }
            else
            {
                // Distinguish between user abort and actual failure only in what the
                // operator is told - the milling run this tool change interrupted is
                // torn down identically either way. Only the milling side is torn down
                // here (StopMillingAsync), never the combined stop path
                // (HandleMillStopAsync): an operator-initiated Stop/Abort already
                // cancelled this run's token and is awaiting this exact task, so calling
                // back into that combined path from here would await this task from
                // inside its own execution (see HandleMillStopAsync's remarks).
                bool wasAborted = toolChangeController.State == ControllerState.Cancelled;
                Logger.Log("Tool change {0}", wasAborted ? "aborted" : "failed");
                BroadcastMessage(WsMessageTypeToolChangeComplete, new { success = false, aborted = wasAborted });
                await StopMillingAsync();
            }
        }
        finally
        {
            // However it ended - success, user abort, genuine failure, or an exception
            // out of Resume() above - the tool change is over, so return the controller to
            // Idle and DetectToolChange stops reporting one. This is the only place that
            // releases it; HandleMillStopAsync awaits this task rather than releasing it
            // itself, so a second release never lands here at the same time. Logged rather
            // than thrown: the unsubscribes below have to run.
            try
            {
                await toolChangeController.ReleaseAsync();
            }
            catch (Exception ex)
            {
                Logger.Log("Could not release the tool change controller: {0}", ex.Message);
            }

            toolChangeController.StateChanged -= onStateChanged;
            toolChangeController.ProgressChanged -= onProgressChanged;
            toolChangeController.UserInputRequired -= onUserInputRequired;
            toolChangeController.ErrorOccurred -= onError;
            PendingPrompt.ClearIfCurrent(published);

            // Compare-and-clear: only release the shared handles if they still belong
            // to this run (see the onToolChange callback in HandleMillStart).
            if (ReferenceEquals(_toolChangeCts, cts))
            {
                _toolChangeCts = null;
                _toolChangeRunTask = null;
            }
        }
    }


    /// <summary>
    /// Handle a user input response posted to the tool-change dialog endpoint. Shared by
    /// the tool-change controller's own prompts and the milling controller's M0/M1
    /// prompt - whichever one is pending. See <see cref="PendingPrompt"/> for why the
    /// answer has to name the question it answers.
    /// </summary>
    private static async Task HandleToolChangeUserInput(HttpListenerResponse response, ToolChangeUserInputRequest req)
    {
        Logger.Log("Prompt answer: id={0} response={1}", req.id ?? "none", req.response ?? "none");

        switch (PendingPrompt.Answer(req.id, req.response))
        {
            case PromptAnswerResult.Accepted:
                await WriteJson(response, new { success = true });
                return;

            case PromptAnswerResult.NothingPending:
                response.StatusCode = HttpStatusBadRequest;
                await WriteJson(response, new { error = ErrorNoPendingUserInput });
                return;

            case PromptAnswerResult.NotAnOption:
                response.StatusCode = HttpStatusBadRequest;
                await WriteJson(response, new { error = ErrorNotAnOption });
                return;

            default:
                response.StatusCode = HttpStatusConflict;
                await WriteJson(response, new { error = ErrorPromptAlreadyAnswered });
                return;
        }
    }

    /// <summary>
    /// Handle tool change abort. An operator-initiated stop that happens to arrive via
    /// the tool-change dialog's Abort button rather than the main Stop button - both need
    /// to tear down the same two controllers the same way, so this is a thin caller of
    /// the shared stop path. See <see cref="HandleMillStopAsync"/> for the lock order
    /// and timeout behavior this depends on.
    /// </summary>
    /// <returns>False if the shared stop path could not confirm both controllers
    /// finished tearing down in time.</returns>
    private static Task<bool> HandleToolChangeAbortAsync() => HandleMillStopAsync();

    /// <summary>
    /// Handle depth adjustment. Used before milling to adjust cut depth.
    /// </summary>
    private static void HandleDepthAdjustment(DepthAdjustmentRequest req)
    {
        if (req.depth.HasValue)
        {
            AppState.SetDepthAdjustment(req.depth.Value);
            Logger.Log("Depth adjustment set to {0:F2}mm", AppState.DepthAdjustment);
        }
        else if (!string.IsNullOrEmpty(req.action))
        {
            switch (req.action.ToLowerInvariant())
            {
                case DepthActionIncrease:
                    AppState.AdjustDepthShallower();
                    break;
                case DepthActionDecrease:
                    AppState.AdjustDepthDeeper();
                    break;
                case DepthActionReset:
                    AppState.ResetDepthAdjustment();
                    break;
            }
            Logger.Log("Depth adjustment {0}: now {1:F2}mm", req.action, AppState.DepthAdjustment);
        }
    }

    private static object GetSettings()
    {
        var settings = AppState.Settings;
        return new
        {
            // Machine profile
            machineProfile = settings.MachineProfile,
            // Probing
            probeFeed = settings.ProbeFeed,
            probeMaxDepth = settings.ProbeMaxDepth,
            probeSafeHeight = settings.ProbeSafeHeight,
            probeMinimumHeight = settings.ProbeMinimumHeight,
            // Outline trace
            outlineTraceHeight = settings.OutlineTraceHeight,
            outlineTraceFeed = settings.OutlineTraceFeed,
            // Tool setter
            toolSetterX = settings.ToolSetterX,
            toolSetterY = settings.ToolSetterY,
            // Serial
            serialPortName = settings.SerialPortName,
            serialPortBaud = settings.SerialPortBaud
        };
    }

    private static object GetMachineProfiles()
    {
        var profileIds = MachineProfiles.GetProfileIds();
        var profiles = profileIds.Select(id =>
        {
            var profile = MachineProfiles.GetProfile(id);
            return new
            {
                id,
                name = profile?.Name ?? id,
                description = profile?.Description,
                hasToolSetter = profile?.ToolSetter != null
            };
        }).ToList();

        return new { profiles };
    }

    private static async Task HandleSettingsUpdate(HttpListenerResponse response, SettingsUpdateRequest req)
    {
        var settings = AppState.Settings;

        // Update only provided values
        if (req.machineProfile != null)
        {
            settings.MachineProfile = req.machineProfile;
        }
        if (req.probeFeed.HasValue)
        {
            settings.ProbeFeed = req.probeFeed.Value;
        }
        if (req.probeMaxDepth.HasValue)
        {
            settings.ProbeMaxDepth = req.probeMaxDepth.Value;
        }
        if (req.probeSafeHeight.HasValue)
        {
            settings.ProbeSafeHeight = req.probeSafeHeight.Value;
        }
        if (req.probeMinimumHeight.HasValue)
        {
            settings.ProbeMinimumHeight = req.probeMinimumHeight.Value;
        }
        if (req.outlineTraceHeight.HasValue)
        {
            settings.OutlineTraceHeight = req.outlineTraceHeight.Value;
        }
        if (req.outlineTraceFeed.HasValue)
        {
            settings.OutlineTraceFeed = req.outlineTraceFeed.Value;
        }
        if (req.toolSetterX.HasValue)
        {
            settings.ToolSetterX = req.toolSetterX.Value;
        }
        if (req.toolSetterY.HasValue)
        {
            settings.ToolSetterY = req.toolSetterY.Value;
        }

        Persistence.SaveSettings();

        await WriteJson(response, new { success = true });
    }

    /// <summary>
    /// Handles request to trust work zero from previous session.
    /// Equivalent to TUI's "Trust work zero from previous session?" prompt.
    /// </summary>
    private static async Task HandleTrustWorkZero(HttpListenerResponse response)
    {
        if (!AppState.Session.HasStoredWorkZero)
        {
            await WriteJson(response, new { success = false, error = ErrorNoStoredWorkZero });
            return;
        }

        AppState.TrustWorkZero(true);
        Logger.Log("HandleTrustWorkZero: work zero trusted via the web API");
        await WriteJson(response, new { success = true });
    }

    /// <summary>
    /// Handles request to recover probe data from autosave.
    /// Forces reload from autosave file even if probe data is in memory.
    /// </summary>
    private static async Task HandleProbeRecoverAutosave(HttpListenerResponse response)
    {
        if (AppState.ReadUsableAutosave() == null)
        {
            await WriteJson(response, new { success = false, error = ErrorNoAutosavedProbeData });
            return;
        }

        try
        {
            var grid = AppState.ForceLoadProbeFromAutosave();

            await WriteJson(response, new
            {
                success = true,
                progress = grid.Progress,
                total = grid.TotalPoints,
                sizeX = grid.SizeX,
                sizeY = grid.SizeY,
                complete = grid.HasCompleteData,
                sourceGCodeLoaded = AppState.CurrentFile != null
            });
        }
        catch (InvalidOperationException ex)
        {
            // The workflow's own refusal, in its own words - the terminal shows the same
            // sentence. ErrorServerFailure would tell the operator to retry something that
            // can never work.
            Logger.Log("HandleProbeRecoverAutosave: refused - {0}", ex.Message);
            await WriteJson(response, new { success = false, error = ex.Message });
        }
        catch (Exception ex)
        {
            Logger.Log("HandleProbeRecoverAutosave: failed - {0}", ex);
            await WriteJson(response, new { success = false, error = ErrorServerFailure });
        }
    }
}
