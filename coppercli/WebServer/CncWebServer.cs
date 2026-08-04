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
    private static readonly List<WebSocket> _clients = new();
    private static readonly Dictionary<WebSocket, DateTime> _clientLastActivity = new();
    private static readonly Dictionary<WebSocket, string> _clientIds = new();
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

    // Pending prompt awaiting a response via ApiMillToolChangeUserInput - shared by the
    // tool-change controller's own prompts (set in StartToolChangeControllerAsync) and
    // the milling controller's M0/M1 prompt (set in HandleMillStart's onUserInputRequired
    // below). The two can never be pending at once: an M6 tool change and an M0/M1 are
    // mutually exclusive states of the same stream position, so reusing this one field -
    // and the toolchange:input/toolchange:complete wire messages - needs no new plumbing.
    private static UserInputRequest? _pendingMillUserInput;

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

    // Probe controller cancellation
    private static CancellationTokenSource? _probeCts;
    private static Task? _probeTask;

    // Trace outline cancellation (separate from probing)
    private static CancellationTokenSource? _traceCts;
    private static Task? _traceTask;

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
                // Disconnect machine if connected
                Logger.Log("CncWebServer: checking machine connection");
                if (_machine?.Connected == true)
                {
                    Logger.Log("CncWebServer: disconnecting machine");
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
                    _clientLastActivity.Clear();
                    _clientIds.Clear();
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

                if (currentClients == 0 && _machine != null && _machine.Connected)
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

    private static async Task HandleApi(HttpListenerContext context, string path)
    {
        var request = context.Request;
        var response = context.Response;
        var method = request.HttpMethod;

        response.ContentType = ContentTypeJson;

        switch (path)
        {
            case ApiStatus:
                await WriteJson(response, GetStatus());
                break;

            case ApiConfig:
                await WriteJson(response, GetConfig());
                break;

            case ApiConstants:
                await WriteJson(response, GetSharedConstants());
                break;

            case ApiPorts:
                var ports = Menus.ConnectionMenu.GetAvailablePorts();
                await WriteJson(response, new { ports });
                break;

            case ApiConnect:
                if (method == MethodPost)
                {
                    var body = await ReadBody(request);
                    var connectReq = JsonSerializer.Deserialize<ConnectRequest>(body);
                    await HandleConnect(response, connectReq);
                }
                break;

            case ApiDisconnect:
                if (method == MethodPost)
                {
                    HandleDisconnect();
                    await WriteJson(response, new { success = true });
                }
                break;

            case ApiHome:
                if (method == MethodPost)
                {
                    if (_machine != null)
                    {
                        MachineCommands.HomeAndWait(_machine);
                    }
                    await WriteJson(response, new { success = true });
                }
                break;

            case ApiUnlock:
                if (method == MethodPost)
                {
                    if (_machine != null)
                    {
                        MachineCommands.Unlock(_machine);
                    }
                    await WriteJson(response, new { success = true });
                }
                break;

            case ApiReset:
                if (method == MethodPost)
                {
                    _machine?.SoftReset();
                    await WriteJson(response, new { success = true });
                }
                break;

            case ApiFeedhold:
                if (method == MethodPost)
                {
                    _machine?.FeedHold();
                    await WriteJson(response, new { success = true });
                }
                break;

            case ApiResume:
                if (method == MethodPost)
                {
                    _machine?.CycleStart();
                    await WriteJson(response, new { success = true });
                }
                break;

            case ApiZero:
                if (method == MethodPost)
                {
                    var body = await ReadBody(request);
                    var zeroReq = JsonSerializer.Deserialize<ZeroRequest>(body);
                    HandleZero(zeroReq);
                    await WriteJson(response, new { success = true });
                }
                break;

            case ApiGotoOrigin:
                if (method == MethodPost)
                {
                    HandleGotoOrigin();
                    await WriteJson(response, new { success = true });
                }
                break;

            case ApiGotoCenter:
                if (method == MethodPost)
                {
                    HandleGotoCenter();
                    await WriteJson(response, new { success = true });
                }
                break;

            case ApiGotoSafe:
                if (method == MethodPost)
                {
                    HandleGotoSafeHeight();
                    await WriteJson(response, new { success = true });
                }
                break;

            case ApiGotoRef:
                if (method == MethodPost)
                {
                    HandleGotoRefHeight();
                    await WriteJson(response, new { success = true });
                }
                break;

            case ApiGotoZ0:
                if (method == MethodPost)
                {
                    HandleGotoZ0();
                    await WriteJson(response, new { success = true });
                }
                break;

            case ApiProbeZ:
                if (method == MethodPost)
                {
                    HandleProbeZSingle();
                    await WriteJson(response, new { success = true });
                }
                break;

            // File browser
            case ApiFiles:
                var fileBrowseDir = request.QueryString[QueryParamPath];
                if (string.IsNullOrEmpty(fileBrowseDir))
                {
                    fileBrowseDir = AppState.Session.LastBrowseDirectory;
                }
                if (string.IsNullOrEmpty(fileBrowseDir))
                {
                    fileBrowseDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                }
                await WriteJson(response, GetFiles(fileBrowseDir));
                break;

            case ApiFileLoad:
                if (method == MethodPost)
                {
                    var body = await ReadBody(request);
                    var loadReq = JsonSerializer.Deserialize<LoadFileRequest>(body);
                    await HandleLoadFile(response, loadReq);
                }
                break;

            case ApiFileUpload:
                if (method == MethodPost)
                {
                    await HandleFileUpload(request, response);
                }
                break;

            case ApiFileInfo:
                await WriteJson(response, GetFileInfo() ?? new { error = ErrorNoFileLoaded });
                break;

            // Milling control
            case ApiMillPreflight:
                await WriteJson(response, HandleMillPreflight());
                break;

            case ApiMillStart:
                if (method == MethodPost)
                {
                    HandleMillStart();
                    await WriteJson(response, new { success = true });
                }
                break;

            case ApiMillPause:
                if (method == MethodPost)
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
                if (method == MethodPost)
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
                if (method == MethodPost)
                {
                    await WriteStopResult(response, await HandleMillStopAsync());
                }
                break;

            case ApiFeedIncrease:
                if (method == MethodPost)
                {
                    _machine?.FeedOverrideIncrease();
                    await WriteJson(response, new { success = true });
                }
                break;

            case ApiFeedDecrease:
                if (method == MethodPost)
                {
                    _machine?.FeedOverrideDecrease();
                    await WriteJson(response, new { success = true });
                }
                break;

            case ApiFeedReset:
                if (method == MethodPost)
                {
                    _machine?.FeedOverrideReset();
                    await WriteJson(response, new { success = true });
                }
                break;

            // Probing
            case ApiProbeSetup:
                if (method == MethodPost)
                {
                    var body = await ReadBody(request);
                    var probeReq = JsonSerializer.Deserialize<ProbeSetupRequest>(body);
                    await HandleProbeSetup(response, probeReq);
                }
                break;

            case ApiProbeTrace:
                if (method == MethodPost)
                {
                    StartProbeTraceOutline();
                    await WriteJson(response, new { success = true });
                }
                break;

            case ApiProbeStart:
                if (method == MethodPost)
                {
                    HandleProbeStart();
                    await WriteJson(response, new { success = true });
                }
                break;

            case ApiProbePause:
                if (method == MethodPost)
                {
                    var pauseProbeController = AppState.Probe;
                    if (pauseProbeController.State == ControllerState.Running)
                    {
                        pauseProbeController.Pause();
                        await WriteJson(response, new { success = true });
                    }
                    else
                    {
                        await WriteJson(response, new { success = false, error = ErrorProbingNotRunning });
                    }
                }
                break;

            case ApiProbeResume:
                if (method == MethodPost)
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
                if (method == MethodPost)
                {
                    // Awaited: the previous fire-and-forget told the operator the machine
                    // had stopped while it was still moving.
                    await HandleProbeStop();
                    await WriteJson(response, new { success = true });
                }
                break;

            case ApiProbeStatus:
                await WriteJson(response, GetProbeStatus());
                break;

            case ApiProbeApply:
                if (request.HttpMethod != MethodPost)
                {
                    response.StatusCode = HttpStatusMethodNotAllowed;
                    await WriteJson(response, new { error = ErrorMethodNotAllowed });
                }
                else
                {
                    bool success = AppState.ApplyProbeData();
                    await WriteJson(response, new { success, applied = AppState.AreProbePointsApplied });
                }
                break;

            case ApiProbeSave:
                if (method == MethodPost)
                {
                    var body = await ReadBody(request);
                    var saveReq = JsonSerializer.Deserialize<ProbeSaveRequest>(body);
                    await HandleProbeSave(response, saveReq);
                }
                break;

            case ApiProbeLoad:
                if (method == MethodPost)
                {
                    var body = await ReadBody(request);
                    var loadReq = JsonSerializer.Deserialize<ProbeLoadRequest>(body);
                    await HandleProbeLoad(response, loadReq);
                }
                break;

            case ApiProbeFiles:
                var probeDir = request.QueryString[QueryParamPath];
                if (string.IsNullOrEmpty(probeDir))
                {
                    probeDir = AppState.Session.LastProbeBrowseDirectory;
                }
                if (string.IsNullOrEmpty(probeDir))
                {
                    probeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                }
                await WriteJson(response, GetProbeFiles(probeDir));
                break;

            case ApiProbeRecover:
                await WriteJson(response, GetProbeState());
                break;

            case ApiProbeClear:
            case ApiProbeDiscard:
                // Both endpoints do the same thing in single-file model
                if (method == MethodPost)
                {
                    HandleProbeDiscard();
                    await WriteJson(response, new { success = true });
                }
                break;

            // Tool change

            case ApiMillToolChangeAbort:
                if (method == MethodPost)
                {
                    await WriteStopResult(response, await HandleToolChangeAbortAsync());
                }
                break;

            case ApiMillToolChangeUserInput:
                if (method == MethodPost)
                {
                    var body = await ReadBody(request);
                    var inputReq = JsonSerializer.Deserialize<ToolChangeUserInputRequest>(body);
                    await HandleToolChangeUserInput(response, inputReq);
                }
                break;

            // Depth adjustment
            case ApiMillDepth:
                if (method == MethodGet)
                {
                    await WriteJson(response, new { depth = AppState.DepthAdjustment });
                }
                else if (method == MethodPost)
                {
                    var body = await ReadBody(request);
                    var depthReq = JsonSerializer.Deserialize<DepthAdjustmentRequest>(body);
                    HandleDepthAdjustment(depthReq);
                    await WriteJson(response, new { success = true, depth = AppState.DepthAdjustment });
                }
                break;

            case ApiMillGrid:
                {
                    // Get grid dimensions from query params (client-specified based on screen size)
                    var widthParam = request.QueryString[QueryParamWidth];
                    var heightParam = request.QueryString[QueryParamHeight];
                    int width = WebMillGridDefaultWidth;
                    int height = WebMillGridDefaultHeight;
                    if (!string.IsNullOrEmpty(widthParam))
                    {
                        int.TryParse(widthParam, out width);
                    }
                    if (!string.IsNullOrEmpty(heightParam))
                    {
                        int.TryParse(heightParam, out height);
                    }
                    await WriteJson(response, new
                    {
                        cells = GetVisitedGridCells(AppState.Milling, width, height),
                        count = AppState.Milling.CuttingPath.Count
                    });
                }
                break;

            case ApiForceDisconnect:
                if (method == MethodPost)
                {
                    await HandleForceDisconnect(response);
                }
                break;

            case ApiSettings:
                if (method == MethodGet)
                {
                    await WriteJson(response, GetSettings());
                }
                else if (method == MethodPost)
                {
                    var body = await ReadBody(request);
                    var settingsReq = JsonSerializer.Deserialize<SettingsUpdateRequest>(body);
                    await HandleSettingsUpdate(response, settingsReq);
                }
                break;

            case ApiProfiles:
                await WriteJson(response, GetMachineProfiles());
                break;

            case ApiSessionRestore:
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
                else if (method == MethodPost)
                {
                    var answer = JsonSerializer.Deserialize<SessionRestoreAnswerRequest>(await ReadBody(request));

                    if (answer?.topic == null
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
                if (method == MethodPost)
                {
                    await HandleTrustWorkZero(response);
                }
                break;

            case ApiProbeRecoverAutosave:
                if (method == MethodPost)
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

        // Milling state from controller. IsActive alone misses WaitingForUserInput
        // (the M0/M1 prompt), and the job is not over just because the spindle is
        // parked waiting on the operator - reporting otherwise sends the browser
        // to the dashboard mid-cut with the tool still down.
        var isMilling = controller.IsActive || controllerState == ControllerState.WaitingForUserInput;
        var isPaused = controllerState == ControllerState.Paused;

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
            spindleOverride = _machine.SpindleOverride,
            rapidOverride = _machine.RapidOverride,
            probePin = _machine.PinStateProbe,
            file = GetFileStatus(),
            probe = GetProbeStatusBrief(),
            probeApplied = AppState.AreProbePointsApplied,
            milling = isMilling,
            millingPaused = isPaused,
            millingPhase = controllerPhase.ToString(),
            controllerState = controllerState.ToString(),
            cuttingPathCount = controller.CuttingPath.Count,  // Client uses this to know when to fetch grid
            probing = AppState.IsProbing,
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
    /// UI behavior is 1:1 with phase:
    ///   - null (NotStarted/Complete) → no tool change UI
    ///   - WaitingForToolChange → mill screen shows overlay with Continue/Abort
    ///   - WaitingForZeroZ → jog screen shows "Continue Milling" button
    ///   - All other phases → spindle moving, no user action needed
    /// </summary>
    private static object? DetectToolChange()
    {
        var controller = AppState.ToolChange;
        var phase = controller.Phase;
        var state = controller.State;

        // Not in tool change
        if (phase == ToolChangePhase.NotStarted || phase == ToolChangePhase.Complete)
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

        return new
        {
            phase = phase.ToString(),
            toolNumber = info?.ToolNumber,
            toolName = info?.ToolName
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
        if (AppState.Milling.Phase != MillingPhase.WaitingForOperator || _pendingMillUserInput == null)
        {
            return null;
        }

        return new
        {
            phase = MillingPhase.WaitingForOperator.ToString(),
            title = _pendingMillUserInput.Title,
            message = _pendingMillUserInput.Message,
            options = _pendingMillUserInput.Options,
            id = _pendingMillUserInput.Id
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
            active = AppState.IsProbing,
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
                brief = 1,
                full = 3
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
                jogMode = WsCmdJogMode,
                home = WsCmdHome,
                unlock = WsCmdUnlock,
                reset = WsCmdReset,
                feedhold = WsCmdFeedhold,
                resume = WsCmdResume,
                zero = WsCmdZero,
                gotoOrigin = WsCmdGotoOrigin,
                gotoCenter = WsCmdGotoCenter,
                gotoSafe = WsCmdGotoSafe,
                gotoRef = WsCmdGotoRef,
                gotoZ0 = WsCmdGotoZ0,
                probeZ = WsCmdProbeZ
            },
            // API paths
            api = new
            {
                status = ApiStatus,
                config = ApiConfig,
                constants = ApiConstants,
                files = ApiFiles,
                fileLoad = ApiFileLoad,
                fileInfo = ApiFileInfo,
                millStart = ApiMillStart,
                millPause = ApiMillPause,
                millResume = ApiMillResume,
                millStop = ApiMillStop,
                feedIncrease = ApiFeedIncrease,
                feedDecrease = ApiFeedDecrease,
                feedReset = ApiFeedReset,
                probeSetup = ApiProbeSetup,
                probeStart = ApiProbeStart,
                probeStop = ApiProbeStop,
                probeStatus = ApiProbeStatus,
                probeApply = ApiProbeApply,
                probeSave = ApiProbeSave,
                probeLoad = ApiProbeLoad,
                probeFiles = ApiProbeFiles,
                probeRecover = ApiProbeRecover,
                probeClear = ApiProbeClear,
                settings = ApiSettings
            }
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

    private static async Task HandleConnect(HttpListenerResponse response, ConnectRequest? req)
    {
        if (_machine == null || req == null)
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
            response.StatusCode = HttpStatusServerError;
            await WriteJson(response, new { error = ex.Message });
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

    private static void HandleZero(ZeroRequest? req)
    {
        Logger.Log($"HandleZero called: axes={string.Join(",", req?.axes ?? Array.Empty<string>())}, retract={req?.retract}");

        if (!MachineConnected)
        {
            Logger.Log("HandleZero: machine null or not connected, returning");
            return;
        }

        var requested = req?.axes ?? new[] { "X", "Y", "Z" };

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
            Logger.Log("HandleZero: refused, no valid axis in request");
            return;
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
    }

    private static void HandleGotoOrigin()
    {
        if (!MachineConnected)
        {
            return;
        }

        // Move to X0 Y0 (does NOT change Z - matches TUI behavior)
        MachineCommands.GotoWorkOriginXY(_machine);
    }

    private static void HandleGotoCenter()
    {
        if (!MachineConnected)
        {
            return;
        }

        MachineCommands.GotoFileCenterXY(_machine, AppState.CurrentFile);
    }

    private static void HandleGotoSafeHeight()
    {
        if (!MachineConnected)
        {
            return;
        }

        // Move Z to safe height (T key in TUI: Z+6mm)
        MachineCommands.MoveToSafeHeight(_machine, Constants.RetractZMm);
    }

    private static void HandleGotoRefHeight()
    {
        if (!MachineConnected)
        {
            return;
        }

        // Move Z to reference height (B key in TUI: Z+1mm)
        MachineCommands.MoveToSafeHeight(_machine, ReferenceZHeightMm);
    }

    private static void HandleGotoZ0()
    {
        if (!MachineConnected)
        {
            return;
        }

        // Move Z to work zero (G key in TUI: Z0)
        MachineCommands.MoveToSafeHeight(_machine, 0);
    }

    private static void HandleProbeZSingle()
    {
        if (!MachineConnected)
        {
            return;
        }

        var settings = AppState.Settings;
        var controller = AppState.Probe;

        // Configure probe options
        controller.Options = ProbeOptions.FromSettings(settings);

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
            return new { error = ex.Message, currentPath = dirPath, entries = Array.Empty<object>() };
        }
    }

    private static async Task HandleFileUpload(HttpListenerRequest request, HttpListenerResponse response)
    {
        try
        {
            // Read the request body
            using var memoryStream = new MemoryStream();
            await request.InputStream.CopyToAsync(memoryStream);
            var body = memoryStream.ToArray();

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
            response.StatusCode = HttpStatusServerError;
            await WriteJson(response, new { error = ex.Message });
        }
    }

    private static async Task HandleLoadFile(HttpListenerResponse response, LoadFileRequest? req)
    {
        if (req?.path == null)
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
            response.StatusCode = HttpStatusServerError;
            await WriteJson(response, new { error = ex.Message });
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
    /// caller's existing critical section.
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

        return new { canStart = result.CanStart, errors, warnings };
    }

    private static void HandleMillStart()
    {
        if (!MachineConnected || AppState.CurrentFile == null)
        {
            return;
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
            BroadcastMessage(WsMessageTypeMillError, new
            {
                message = DescribePreflightError(preflight),
                isFatal = true
            });
            return;
        }

        // === ENSURE MACHINE READY ===
        // Clear Door state, wait for Idle
        if (!MachineCommands.EnsureMachineReady(_machine))
        {
            Logger.Log("Mill start aborted: machine did not reach a settled, alarm-free state");
            BroadcastMessage(WsMessageTypeMillError, new
            {
                message = CliConstants.ErrorMachineNotReady,
                isFatal = true
            });
            return;
        }

        var controller = AppState.Milling;

        // Reset controller if needed (from previous run). Reset() throws outside
        // Completed/Failed/Cancelled/Idle, so only call it once HasFinished says
        // that is safe - otherwise a teardown that left the controller Running/Paused
        // would make this call throw instead of starting the new run.
        if (controller.State != ControllerState.Idle && controller.HasFinished)
        {
            controller.Reset();
        }

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
            _toolChangeRunTask = Task.Run(() => StartToolChangeControllerAsync(info, controller, toolChangeCts));
        };
        // The mill controller's own prompt (M0/M1) - distinct from a tool change, which
        // runs on the separate ToolChangeController instance handled by onToolChange
        // above. Reuses the tool-change dialog's WS message and pending-input field (see
        // _pendingMillUserInput) since the two prompts can never be pending at once.
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
            _pendingMillUserInput = new UserInputRequest
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
            (float)AppState.DepthAdjustment, _machine!.IsHomed);

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
            finally
            {
                // Unsubscribe from events
                controller.StateChanged -= onStateChanged;
                controller.ProgressChanged -= onProgressChanged;
                controller.ToolChangeDetected -= onToolChange;
                controller.UserInputRequired -= onUserInputRequired;
                controller.ErrorOccurred -= onError;
                _pendingMillUserInput = null;

                // Compare-and-clear: only release the shared handles if they still
                // belong to this run.
                if (ReferenceEquals(_millCts, millCts))
                {
                    _millCts = null;
                    _millRunTask = null;
                }

                // Stop sleep prevention
                SleepPrevention.Stop();
                Logger.Log("Milling controller finished");

                // Re-enable auto state clear now that milling is done
                if (_machine != null)
                {
                    _machine.EnableAutoStateClear = true;
                }

                // Start idle disconnect timer if no clients connected
                StartIdleDisconnectTimer();
            }
        });

        Logger.Log("Milling started (controller-based)");
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
    /// CRITICAL: nothing reachable from the tool-change run task itself may call back
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
        bool acquiredAbortLock = await _toolChangeAbortLock.WaitAsync(ControllerCancelTimeoutMs);
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
                || await AwaitRunTeardownAsync(toolChangeRunTask, "Tool change");

            bool millStopped = await StopMillingAsync();

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
    private static async Task<bool> StopMillingAsync()
    {
        if (_machine == null)
        {
            return true;
        }

        bool acquiredStopLock = await _millStopLock.WaitAsync(ControllerCancelTimeoutMs);
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
                stopped = await AwaitRunTeardownAsync(runTask, "Mill");
            }
            else if (controller.State != ControllerState.Idle)
            {
                // No run is in flight - safe to drive cleanup directly.
                await controller.StopAsync();
            }

            // A timed-out unwind can leave the controller in a non-terminal state (still
            // Running/Paused/etc). Reset() throws on anything but
            // Completed/Failed/Cancelled/Idle, so only call it once the state says that
            // is safe.
            var state = controller.State;
            if (state != ControllerState.Idle && ControllerBase.IsFinishedState(state))
            {
                controller.Reset();
            }

            Logger.Log("Mill stop complete");
            return stopped;
        }
        finally
        {
            _millStopLock.Release();
        }
    }

    /// <summary>States from which ControllerBase.Reset() is legal to call.</summary>

    /// <summary>
    /// Waits for a controller's run task to unwind after cancellation, bounded so a
    /// stalled run cannot hang the caller forever. Any fault is swallowed here rather
    /// than rethrown: the teardown that follows this call is the entire reason for
    /// waiting, and letting the run's own exception propagate out of the await would
    /// skip it.
    /// </summary>
    /// <returns>True if the run task unwound within <see cref="ControllerCancelTimeoutMs"/>.</returns>
    private static async Task<bool> AwaitRunTeardownAsync(Task runTask, string label)
    {
        var completed = await Task.WhenAny(runTask, Task.Delay(ControllerCancelTimeoutMs));
        if (completed != runTask)
        {
            Logger.Log("{0}: run task did not unwind within {1}ms", label, ControllerCancelTimeoutMs);
            return false;
        }

        if (runTask.IsFaulted)
        {
            Logger.Log("{0}: run task faulted during teardown: {1}", label, runTask.Exception);
        }

        return true;
    }


    // Probe parameter limits are in WebConstants

    private static async Task HandleProbeSetup(HttpListenerResponse response, ProbeSetupRequest? req)
    {
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
            var margin = Math.Clamp(req?.margin ?? DefaultProbeMargin, MinProbeMargin, MaxProbeMargin);
            var gridSize = Math.Clamp(req?.gridSize ?? DefaultProbeGridSize, MinProbeGridSize, MaxProbeGridSize);

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
            response.StatusCode = HttpStatusServerError;
            await WriteJson(response, new { error = ex.Message });
        }
    }

    private static void StartProbeTraceOutline()
    {
        // Cancel any existing trace operation
        _traceCts?.Cancel();
        _traceCts?.Dispose();
        _traceCts = new CancellationTokenSource();

        _traceTask = HandleProbeTraceOutlineAsync(_traceCts.Token);
    }

    private static async Task HandleProbeTraceOutlineAsync(CancellationToken ct)
    {
        if (!MachineConnected)
        {
            Logger.Log($"HandleProbeTraceOutline: skipping (machine={_machine != null}, connected={_machine?.Connected})");
            return;
        }

        // Auto-load from autosave if probe data not in memory but exists on disk
        AppState.EnsureProbeDataLoaded();

        if (AppState.ProbePoints == null)
        {
            Logger.Log("HandleProbeTraceOutline: no probe data (run Setup first)");
            return;
        }

        var settings = AppState.Settings;
        var grid = AppState.ProbePoints;
        var controller = AppState.Probe;

        Logger.Log($"HandleProbeTraceOutline: tracing outline for {grid.SizeX}x{grid.SizeY} grid, " +
            $"traceHeight={settings.OutlineTraceHeight:F3}, traceFeed={settings.OutlineTraceFeed:F0}");

        // Configure controller with grid and trace options
        controller.LoadGrid(grid);
        controller.Options = ProbeOptions.FromSettings(settings, traceOutline: true);

        void OnTraceError(ControllerError error)
        {
            Logger.Log($"Trace outline error: {error.Message}");
            BroadcastMessage(WsMessageTypeProbeError, new { message = error.Message });
        }

        controller.ErrorOccurred += OnTraceError;
        try
        {
            await controller.TraceOutlineAsync(ct);
            Logger.Log("HandleProbeTraceOutline: complete");
        }
        catch (OperationCanceledException)
        {
            Logger.Log("HandleProbeTraceOutline: cancelled");
        }
        finally
        {
            controller.ErrorOccurred -= OnTraceError;
        }
    }

    private static void HandleProbeStart()
    {
        if (!MachineConnected)
        {
            Logger.Log($"HandleProbeStart: skipping (machine={_machine != null}, connected={_machine?.Connected})");
            return;
        }

        // Auto-load from autosave if probe data not in memory but exists on disk
        AppState.EnsureProbeDataLoaded();

        if (AppState.ProbePoints == null)
        {
            Logger.Log("HandleProbeStart: no probe data (run Setup first)");
            return;
        }

        var settings = AppState.Settings;
        var grid = AppState.ProbePoints;
        var controller = AppState.Probe;

        Logger.Log($"HandleProbeStart: starting grid probe {grid.SizeX}x{grid.SizeY} = {grid.TotalPoints} points");

        // Configure controller options
        // Web grid probe uses the same full settings mapping as the TUI.
        controller.Options = ProbeOptions.FromSettings(settings, traceOutline: false);

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

        // Start controller async
        _probeCts = new CancellationTokenSource();
        _probeTask = Task.Run(async () =>
        {
            try
            {
                await controller.StartAsync(_probeCts.Token);

                // Complete - autosave already contains the data, no action needed
                if (controller.State == ControllerState.Completed)
                {
                    Logger.Log("HandleProbeStart: probing complete, data in autosave");
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Log("HandleProbeStart: probing cancelled");
            }
            catch (Exception ex)
            {
                Logger.Log($"HandleProbeStart: error - {ex.Message}");
            }
            finally
            {
                controller.PointCompleted -= OnProbePointCompleted;
                controller.ErrorOccurred -= OnProbeError;
                controller.Reset();

                // Re-enable auto state clear now that probing is done
                if (_machine != null)
                {
                    _machine.EnableAutoStateClear = true;
                }

                // Start idle disconnect timer if no clients connected
                StartIdleDisconnectTimer();
            }
        });
    }

    private static void OnProbePointCompleted(int index, Vector2 coords, double z)
    {
        Persistence.SaveProbeProgress();
        Logger.Log($"Probe point {index + 1} complete: ({coords.X:F3}, {coords.Y:F3}) Z={z:F3}");
    }

    private static void OnProbeError(ControllerError error)
    {
        Logger.Log($"Probe error: {error.Message}");
    }

    /// <summary>
    /// Stops probing. Returns a Task rather than being async void: an exception from an
    /// async void method is rethrown on the thread pool and terminates the process -
    /// while the machine is still moving.
    /// </summary>
    private static async Task HandleProbeStop()
    {
        // Cancel both trace and probe operations first
        _traceCts?.Cancel();
        _probeCts?.Cancel();

        // Wait for tasks to complete
        try
        {
            _traceTask?.Wait(TimeSpan.FromMilliseconds(Constants.ProbeStopTimeoutMs));
        }
        catch
        {
            // Ignore cancellation exceptions
        }
        try
        {
            _probeTask?.Wait(TimeSpan.FromMilliseconds(Constants.ProbeStopTimeoutMs));
        }
        catch
        {
            // Ignore cancellation exceptions
        }

        // Cleanup
        _traceCts?.Dispose();
        _traceCts = null;
        _traceTask = null;
        _probeCts?.Dispose();
        _probeCts = null;
        _probeTask = null;

        // Stop motion and clear GRBL's command buffer
        if (_machine != null)
        {
            await MachineWait.StopAndResetAsync(_machine);
        }
    }

    private static object GetProbeStatus()
    {
        // Auto-load from autosave if grid not in memory but autosave exists
        AppState.EnsureProbeDataLoaded();

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

        // Check if controller is running (include Paused so UI stays in probe progress mode)
        var controller = AppState.Probe;
        var controllerState = controller.State;
        bool isActive = AppState.Probe.IsActive;
        bool isPaused = controllerState == ControllerState.Paused;

        return new
        {
            active = isActive,
            hasUnsavedData,
            paused = isPaused,
            progress = grid.Progress,
            total = grid.TotalPoints,
            sizeX = grid.SizeX,
            sizeY = grid.SizeY,
            minHeight = grid.MinHeight == double.MaxValue ? 0 : grid.MinHeight,
            maxHeight = grid.MaxHeight == double.MinValue ? 0 : grid.MaxHeight,
            points = GetProbePointsArray(grid),
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
        var grid = AppState.ProbePoints;
        bool hasUnsavedData = Persistence.GetProbeState() != Persistence.ProbeState.None;
        return (grid, ComputeProbeState(grid), hasUnsavedData);
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

    private static async Task HandleWebSocket(HttpListenerContext context)
    {
        WebSocket? webSocket = null;
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
            int otherClients = _clientIds.Count(kvp => kvp.Value != clientId);
            int otherPending = _pendingClients.Count(kvp => kvp.Key != clientId);
            int anonymousClients = _clients.Count - _clientIds.Count;

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
            webSocket = wsContext.WebSocket;

            var clientAddress = context.Request.RemoteEndPoint?.Address?.ToString();

            lock (_clientsLock)
            {
                _clients.Add(webSocket);
                _clientLastActivity[webSocket] = DateTime.Now;
                _webClientAddress = clientAddress;
                _forceDisconnected = false;  // Reset: new client means normal reconnect behavior
                if (clientId != null)
                {
                    // Remove any existing WebSocket with this client ID (stale connection from same browser)
                    var staleSocket = _clientIds.FirstOrDefault(kvp => kvp.Value == clientId).Key;
                    if (staleSocket != null)
                    {
                        _clients.Remove(staleSocket);
                        _clientLastActivity.Remove(staleSocket);
                        _clientIds.Remove(staleSocket);
                        Logger.Log("Removed stale WebSocket for client {0}", clientId);
                    }
                    _clientIds[webSocket] = clientId;
                    // Remove from pending - now fully connected
                    _pendingClients.Remove(clientId);
                }
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
                var errorBytes = Encoding.UTF8.GetBytes(errorJson);
                await webSocket.SendAsync(
                    new ArraySegment<byte>(errorBytes),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None);
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
                    Logger.Log($"Failed to connect Machine: {ex.Message}");
                    BroadcastMessage(WsMessageTypeConnectionError, new { error = ex.Message });
                }
                finally
                {
                    _machine.LineReceived -= OnLineReceived;
                }
            }

            var buffer = new byte[WebSocketBufferSize];
            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    _cts?.Token ?? CancellationToken.None);

                // Update activity timestamp
                lock (_clientsLock)
                {
                    _clientLastActivity[webSocket] = DateTime.Now;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await HandleWebSocketMessage(webSocket, message);
                }
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
            if (webSocket != null)
            {
                int remainingClients;
                lock (_clientsLock)
                {
                    _clients.Remove(webSocket);
                    _clientLastActivity.Remove(webSocket);
                    _clientIds.Remove(webSocket);
                    remainingClients = _clients.Count;
                    if (remainingClients == 0)
                    {
                        _webClientAddress = null;
                    }
                }
                Logger.Log("WebSocket client disconnected");

                // Disconnect Machine when last web client disconnects (frees proxy slot for TUI)
                // BUT only if no operation is in progress
                bool operationInProgress = _probeTask != null && !_probeTask.IsCompleted
                    || _millCts != null
                    || _toolChangeCts != null
                    || (_machine?.IsHoming ?? false);

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

    private static Task HandleWebSocketMessage(WebSocket socket, string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeElem))
            {
                return Task.CompletedTask;
            }

            var type = typeElem.GetString();

            switch (type)
            {
                case WsCmdJogMode:
                    // Mode-based jog - server determines values from mode index
                    var modeAxis = root.TryGetProperty("axis", out var modeAxisElem) ? modeAxisElem.GetString() : null;
                    var direction = root.TryGetProperty("direction", out var dirElem) ? dirElem.GetInt32() : 0;
                    var modeIndex = root.TryGetProperty("modeIndex", out var modeIdxElem) ? modeIdxElem.GetInt32() : 1;
                    HandleJogWithMode(modeAxis, direction, modeIndex);
                    break;

                case WsCmdHome:
                    if (_machine != null)
                    {
                        MachineCommands.HomeAndWait(_machine);
                    }
                    break;

                case WsCmdUnlock:
                    if (_machine != null)
                    {
                        MachineCommands.Unlock(_machine);
                    }
                    break;

                case WsCmdReset:
                    _machine?.SoftReset();
                    break;

                case WsCmdFeedhold:
                    _machine?.FeedHold();
                    break;

                case WsCmdResume:
                    _machine?.CycleStart();
                    break;

                case WsCmdZero:
                    Logger.Log($"WebSocket: received zero command");
                    if (root.TryGetProperty("axes", out var axesElem))
                    {
                        var axes = new List<string>();
                        foreach (var a in axesElem.EnumerateArray())
                        {
                            axes.Add(a.GetString() ?? "");
                        }
                        var retract = root.TryGetProperty("retract", out var retractElem) && retractElem.GetBoolean();
                        Logger.Log($"WebSocket zero: axes=[{string.Join(",", axes)}], retract={retract}");
                        HandleZero(new ZeroRequest { axes = axes.ToArray(), retract = retract });
                    }
                    else
                    {
                        Logger.Log("WebSocket zero: no axes specified, using defaults");
                        HandleZero(null);
                    }
                    break;

                case WsCmdGotoOrigin:
                    HandleGotoOrigin();
                    break;

                case WsCmdGotoCenter:
                    HandleGotoCenter();
                    break;

                case WsCmdGotoSafe:
                    HandleGotoSafeHeight();
                    break;

                case WsCmdGotoRef:
                    HandleGotoRefHeight();
                    break;

                case WsCmdGotoZ0:
                    HandleGotoZ0();
                    break;

                case WsCmdProbeZ:
                    HandleProbeZSingle();
                    break;
            }
        }
        catch (JsonException)
        {
            // Invalid JSON, ignore
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Broadcast a message to all connected WebSocket clients.
    /// </summary>
    private static void BroadcastMessage(string type, object data)
    {
        var json = JsonSerializer.Serialize(new { type, data });
        var bytes = Encoding.UTF8.GetBytes(json);

        List<WebSocket> clientsCopy;
        lock (_clientsLock)
        {
            clientsCopy = _clients.ToList();
        }

        foreach (var client in clientsCopy)
        {
            if (client.State == WebSocketState.Open)
            {
                try
                {
                    // Fire and forget - don't wait for send to complete
                    _ = client.SendAsync(
                        new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text,
                        true,
                        CancellationToken.None);
                }
                catch
                {
                    // Client disconnected, will be cleaned up
                }
            }
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

                var status = GetStatus();
                var json = JsonSerializer.Serialize(new { type = "status", data = status });
                var bytes = Encoding.UTF8.GetBytes(json);

                List<WebSocket> clientsCopy;
                List<WebSocket> staleClients = new();
                lock (_clientsLock)
                {
                    clientsCopy = _clients.ToList();

                    // Detect stale clients (no activity for WebSocketTimeoutMs)
                    var now = DateTime.Now;
                    foreach (var client in clientsCopy)
                    {
                        if (_clientLastActivity.TryGetValue(client, out var lastActivity))
                        {
                            if ((now - lastActivity).TotalMilliseconds > WebSocketTimeoutMs)
                            {
                                staleClients.Add(client);
                            }
                        }
                    }
                }

                // Close stale clients
                foreach (var stale in staleClients)
                {
                    Logger.Log("Closing stale WebSocket client (no activity for 30s)");
                    try
                    {
                        await stale.CloseAsync(WebSocketCloseStatus.NormalClosure, "Timeout", CancellationToken.None);
                    }
                    catch
                    {
                        // Already closed
                    }
                    lock (_clientsLock)
                    {
                        _clients.Remove(stale);
                        _clientLastActivity.Remove(stale);
                        _clientIds.Remove(stale);
                    }
                }

                foreach (var client in clientsCopy)
                {
                    if (client.State == WebSocketState.Open && !staleClients.Contains(client))
                    {
                        try
                        {
                            await client.SendAsync(
                                new ArraySegment<byte>(bytes),
                                WebSocketMessageType.Text,
                                true,
                                ct);
                        }
                        catch
                        {
                            // Client disconnected, will be cleaned up
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
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
                    path, requestClientId ?? "null", _clients.Count, _clientIds.Count, _pendingClients.Count);
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
            await WriteJson(response, new { error = ErrorStopTimedOut });
        }
    }

    /// <summary>Writes a bare sentence, for a response a person reads rather than the UI.</summary>
    private static async Task WriteText(HttpListenerResponse response, string text)
    {
        response.ContentType = ContentTypeText;
        var bytes = Encoding.UTF8.GetBytes(text);
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private static async Task<string> ReadBody(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        return await reader.ReadToEndAsync();
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
        public bool? retract { get; init; }
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
        public string? response { get; init; }  // e.g., "Continue" or "Abort"
    }

    private static async Task HandleProbeSave(HttpListenerResponse response, ProbeSaveRequest? req)
    {
        var probePoints = AppState.ProbePoints;

        if (probePoints == null || !probePoints.HasCompleteData)
        {
            response.StatusCode = HttpStatusBadRequest;
            await WriteJson(response, new { error = ErrorNoCompleteProbeData });
            return;
        }

        if (string.IsNullOrEmpty(req?.path))
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
                await WriteJson(response, new { error = "Failed to save probe data" });
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
            response.StatusCode = HttpStatusServerError;
            await WriteJson(response, new { error = ex.Message });
        }
    }

    private static async Task HandleProbeLoad(HttpListenerResponse response, ProbeLoadRequest? req)
    {
        if (string.IsNullOrEmpty(req?.path))
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
            response.StatusCode = HttpStatusServerError;
            await WriteJson(response, new { error = ex.Message });
        }
    }

    private static object GetProbeFiles(string dirPath) =>
        GetFilesWithFilter(dirPath, ext => ext == ".pgrid");

    /// <summary>
    /// Gets probe state using the simplified single-file model.
    /// Returns state as string: "none", "partial", or "complete".
    /// </summary>
    private static object GetProbeState()
    {
        var state = Persistence.GetProbeState();
        return new
        {
            state = state.ToString().ToLowerInvariant()
        };
    }

    /// <summary>
    /// Forces disconnect of all connected WebSocket clients and releases the serial port.
    /// Used by TUI when it needs to take over from web clients.
    /// Returns the number of clients that were disconnected.
    /// </summary>
    public static int ForceDisconnectAllClients()
    {
        // Suppress auto-reconnect so TUI can take over
        _forceDisconnected = true;

        List<WebSocket> clientsToClose;
        lock (_clientsLock)
        {
            clientsToClose = _clients.ToList();
            _pendingClients.Clear();
            _clients.Clear();
            _clientLastActivity.Clear();
            _clientIds.Clear();
            _webClientAddress = null;
        }

        Logger.Log($"ForceDisconnectAllClients: closing {clientsToClose.Count} client(s), suppressing auto-reconnect");

        foreach (var client in clientsToClose)
        {
            try
            {
                if (client.State == WebSocketState.Open)
                {
                    client.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        WsCloseReasonForceDisconnect,
                        CancellationToken.None).Wait(1000);
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
    private static void HandleProbeDiscard()
    {
        Logger.Log("HandleProbeDiscard: ProbePoints was {0}", AppState.ProbePoints != null ? "set" : "null");
        AppState.DiscardProbeData();
        Persistence.ClearProbeAutoSave();
        Logger.Log("HandleProbeDiscard: ProbePoints is now {0}", AppState.ProbePoints != null ? "set" : "null");
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

        // Reset controller if needed (shouldn't happen, but defensive)
        if (toolChangeController.State != ControllerState.Idle)
        {
            Logger.Log("StartToolChangeControllerAsync: resetting controller from state {0}", toolChangeController.State);
            toolChangeController.Reset();
        }

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
        Action<UserInputRequest> onUserInputRequired = request =>
        {
            Logger.Log("Tool change user input required: {0}", request.Message);
            _pendingMillUserInput = request;
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
                // workflow. Resume() throws on anything but Paused, and an unguarded
                // throw here would skip the toolChangeController.Reset() below,
                // stranding the tool-change controller non-Idle for the rest of the
                // session - so only resume if the milling controller is actually
                // still parked waiting for this tool change.
                if (millingController.State == ControllerState.Paused)
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
            // However it ended - success, user abort, genuine failure, or an
            // exception out of Resume() above - the tool change is over: return the
            // controller to Idle so DetectToolChange stops reporting one and Phase
            // does not stay stuck at whatever step it reached. This is the single
            // place that resets the tool change controller; HandleMillStopAsync
            // awaits this task instead of resetting the controller itself, so a
            // second Reset() never lands here concurrently. Placed in the finally so
            // it always runs, even when Resume() throws.
            if (toolChangeController.State != ControllerState.Idle)
            {
                toolChangeController.Reset();
            }

            toolChangeController.StateChanged -= onStateChanged;
            toolChangeController.ProgressChanged -= onProgressChanged;
            toolChangeController.UserInputRequired -= onUserInputRequired;
            toolChangeController.ErrorOccurred -= onError;
            _pendingMillUserInput = null;

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
    /// prompt (see <see cref="_pendingMillUserInput"/>) - whichever one is pending.
    /// Called when the user clicks Continue or Abort.
    /// </summary>
    private static async Task HandleToolChangeUserInput(HttpListenerResponse response, ToolChangeUserInputRequest? req)
    {
        // Taken once: two Continue taps can arrive together, and re-reading the field
        // between the check and the call lets the second find it already cleared.
        var pending = _pendingMillUserInput;
        if (pending == null)
        {
            response.StatusCode = HttpStatusBadRequest;
            await WriteJson(response, new { error = ErrorNoPendingUserInput });
            return;
        }

        if (string.IsNullOrEmpty(req?.response))
        {
            response.StatusCode = HttpStatusBadRequest;
            await WriteJson(response, new { error = ErrorNoResponseProvided });
            return;
        }

        Logger.Log("Tool change user input response: {0}", req.response);

        // Call the callback to unblock the controller
        _pendingMillUserInput = null;
        pending.OnResponse?.Invoke(req.response);

        await WriteJson(response, new { success = true });
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
    private static void HandleDepthAdjustment(DepthAdjustmentRequest? req)
    {
        if (req == null)
        {
            return;
        }

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

    private static async Task HandleSettingsUpdate(HttpListenerResponse response, SettingsUpdateRequest? req)
    {
        if (req == null)
        {
            response.StatusCode = HttpStatusBadRequest;
            await WriteJson(response, new { error = ErrorInvalidRequest });
            return;
        }

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

        AppState.IsWorkZeroSet = true;
        Logger.Log("HandleTrustWorkZero: IsWorkZeroSet = true (trusted via web API)");
        await WriteJson(response, new { success = true });
    }

    /// <summary>
    /// Handles request to recover probe data from autosave.
    /// Forces reload from autosave file even if probe data is in memory.
    /// </summary>
    private static async Task HandleProbeRecoverAutosave(HttpListenerResponse response)
    {
        var autosaveState = Persistence.GetProbeState();
        if (autosaveState == Persistence.ProbeState.None)
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
        catch (Exception ex)
        {
            Logger.Log("HandleProbeRecoverAutosave: failed - {0}", ex.Message);
            await WriteJson(response, new { success = false, error = ex.Message });
        }
    }
}
