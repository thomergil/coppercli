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
    private static string _serialPort = "";
    private static int _baudRate = Constants.DefaultBaudRate;
    private static bool _isReconnecting = false;
    private static bool _forceDisconnected = false;  // Suppress auto-reconnect after force disconnect
    private static readonly object _reconnectLock = new();

    // Milling controller cancellation (for stopping operations)
    private static CancellationTokenSource? _millCts;

    // Tool change controller cancellation and pending user input
    private static CancellationTokenSource? _toolChangeCts;
    private static UserInputRequest? _pendingToolChangeInput;

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

        if (clientCount > 0 || _machine == null || !_machine.Connected)
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

            // WebSocket upgrade
            if (request.IsWebSocketRequest && path == "/ws")
            {
                await HandleWebSocket(context);
                return;
            }

            // API endpoints
            if (path.StartsWith("/api/"))
            {
                await HandleApi(context, path);
                return;
            }

            // Static files
            await ServeStaticFile(context, path);
        }
        catch (Exception ex)
        {
            Logger.Log($"Request error: {ex.Message}");
            response.StatusCode = HttpStatusServerError;
            await WriteJson(response, new { error = ex.Message });
        }
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
                var fileBrowseDir = request.QueryString["path"];
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
                    if (resumeController.State == ControllerState.Paused)
                    {
                        resumeController.Resume();
                        await WriteJson(response, new { success = true });
                    }
                    else
                    {
                        response.StatusCode = HttpStatusBadRequest;
                        await WriteJson(response, new { error = ErrorCannotResumeNotPaused });
                    }
                }
                break;

            case ApiMillStop:
                if (method == MethodPost)
                {
                    await HandleMillStopAsync();
                    await WriteJson(response, new { success = true });
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
                    if (resumeProbeController.State == ControllerState.Paused)
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
                    HandleProbeStop();
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
                var probeDir = request.QueryString["path"];
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
            case ApiMillToolChangeContinue:
                if (method == MethodPost)
                {
                    await HandleToolChangeContinue(response);
                }
                break;

            case ApiMillToolChangeAbort:
                if (method == MethodPost)
                {
                    await HandleToolChangeAbortAsync();
                    await WriteJson(response, new { success = true });
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
                    var widthParam = request.QueryString["width"];
                    var heightParam = request.QueryString["height"];
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

        // Milling state from controller
        var isMilling = controllerState == ControllerState.Running ||
                        controllerState == ControllerState.Initializing ||
                        controllerState == ControllerState.Paused;
        var isPaused = controllerState == ControllerState.Paused;

        // Tool change state from AppState (set by controller event)
        var toolChange = DetectToolChange();

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
            probing = AppState.Probing,
            toolChange = toolChange,
            depthAdjustment = AppState.DepthAdjustment,
            buttons = GetButtonStates(_machine.Connected),
            hasStoredWorkZero = AppState.Session.HasStoredWorkZero,
            isWorkZeroSet = AppState.IsWorkZeroSet
        };
    }

    /// <summary>
    /// Get tool change status from the ToolChangeController.
    /// The controller's Phase is the single source of truth.
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
        var grid = AppState.ProbePoints;
        var autosaveState = Persistence.GetProbeState();
        string state = ComputeProbeState(grid);
        bool hasUnsavedData = autosaveState != Persistence.ProbeState.None;

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
            active = AppState.Probing,
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
        if (_machine == null || !_machine.Connected)
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

        if (_machine == null || !_machine.Connected)
        {
            Logger.Log("HandleZero: machine null or not connected, returning");
            return;
        }

        var axes = req?.axes ?? new[] { "X", "Y", "Z" };
        var axesUpper = axes.Select(a => a.ToUpperInvariant()).ToArray();
        var axesStr = string.Join(" ", axesUpper.Select(a => $"{a}0"));
        Logger.Log($"HandleZero: sending ZeroWorkOffset with axes: {axesStr}");

        // SetWorkZeroAndWait handles probe grid state (re-applies if Z-only, discards if XY)
        MachineCommands.SetWorkZeroAndWait(_machine, axesStr);

        // If zeroing all axes, save to session
        if (axes.Length == 3 || (axes.Contains("X") && axes.Contains("Y") && axes.Contains("Z")))
        {
            var session = AppState.Session;
            var pos = _machine.WorkPosition;
            session.WorkZeroX = pos.X;
            session.WorkZeroY = pos.Y;
            session.WorkZeroZ = pos.Z;
            session.HasStoredWorkZero = true;
            Persistence.SaveSession();
            Logger.Log($"HandleZero: saved session with work zero at {pos.X}, {pos.Y}, {pos.Z}");
        }

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
        if (_machine == null || !_machine.Connected)
        {
            return;
        }

        // Block X/Y movement when probe is in contact
        if (_machine.PinStateProbe)
        {
            Logger.Log("Blocked goto origin: probe in contact");
            return;
        }

        // Move to X0 Y0 (does NOT change Z - matches TUI behavior)
        MachineCommands.SetAbsoluteMode(_machine);
        MachineCommands.RapidMoveXY(_machine, 0, 0);
    }

    private static void HandleGotoCenter()
    {
        if (_machine == null || !_machine.Connected)
        {
            return;
        }

        // Block X/Y movement when probe is in contact
        if (_machine.PinStateProbe)
        {
            Logger.Log("Blocked goto center: probe in contact");
            return;
        }

        var currentFile = AppState.CurrentFile;
        if (currentFile != null)
        {
            double centerX = (currentFile.Min.X + currentFile.Max.X) / 2;
            double centerY = (currentFile.Min.Y + currentFile.Max.Y) / 2;
            MachineCommands.SetAbsoluteMode(_machine);
            MachineCommands.RapidMoveXY(_machine, centerX, centerY);
        }
    }

    private static void HandleGotoSafeHeight()
    {
        if (_machine == null || !_machine.Connected)
        {
            return;
        }

        // Move Z to safe height (T key in TUI: Z+6mm)
        MachineCommands.MoveToSafeHeight(_machine, Constants.RetractZMm);
    }

    private static void HandleGotoRefHeight()
    {
        if (_machine == null || !_machine.Connected)
        {
            return;
        }

        // Move Z to reference height (B key in TUI: Z+1mm)
        MachineCommands.MoveToSafeHeight(_machine, ReferenceZHeightMm);
    }

    private static void HandleGotoZ0()
    {
        if (_machine == null || !_machine.Connected)
        {
            return;
        }

        // Move Z to work zero (G key in TUI: Z0)
        MachineCommands.MoveToSafeHeight(_machine, 0);
    }

    private static void HandleProbeZSingle()
    {
        if (_machine == null || !_machine.Connected)
        {
            return;
        }

        var settings = AppState.Settings;
        var controller = AppState.Probe;

        // Configure probe options
        controller.Options = new ProbeOptions
        {
            MaxDepth = settings.ProbeMaxDepth,
            ProbeFeed = settings.ProbeFeed
        };

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

            var savePath = Path.Combine(uploadsDir, fileName);

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

            await WriteJson(response, new
            {
                success = true,
                name = Path.GetFileName(savePath),
                path = savePath,
                lines = file.Toolpath.Count,
                bounds = new
                {
                    minX = file.Min.X,
                    minY = file.Min.Y,
                    minZ = file.Min.Z,
                    maxX = file.Max.X,
                    maxY = file.Max.Y,
                    maxZ = file.Max.Z
                }
            });
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

            await WriteJson(response, new
            {
                success = true,
                name = Path.GetFileName(req.path),
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
            });
        }
        catch (Exception ex)
        {
            response.StatusCode = HttpStatusServerError;
            await WriteJson(response, new { error = ex.Message });
        }
    }

    private static object? GetFileInfo()
    {
        var file = AppState.CurrentFile;
        if (file == null)
        {
            return null;
        }

        return new
        {
            name = Path.GetFileName(file.FileName),
            path = file.FileName,
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
    }

    private static object HandleMillPreflight()
    {
        var result = MenuHelpers.ValidateMillPreflight();
        var warnings = new List<string>();
        var errors = new List<string>();

        // Map error code to API error message
        if (result.Error != MillPreflightError.None)
        {
            string errorMsg = result.Error switch
            {
                MillPreflightError.NotConnected => PreflightErrorNotConnected,
                MillPreflightError.NoFile => PreflightErrorNoFile,
                MillPreflightError.ProbeNotApplied => PreflightErrorProbeNotApplied,
                MillPreflightError.ProbeIncomplete => string.Format(PreflightErrorProbeIncomplete, result.ProbeProgress),
                MillPreflightError.AlarmState => PreflightErrorAlarm,
                _ => PreflightErrorUnknown
            };
            errors.Add(errorMsg);
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
        if (_machine == null || !_machine.Connected || AppState.CurrentFile == null)
        {
            return;
        }

        // === ENSURE MACHINE READY ===
        // Clear Door state, wait for Idle
        if (!MachineCommands.EnsureMachineReady(_machine))
        {
            Logger.Log("Mill start aborted: machine not ready (Alarm state)");
            return;
        }

        var controller = AppState.Milling;

        // Reset controller if needed (from previous run)
        if (controller.State != ControllerState.Idle)
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

            // Auto-start the tool change controller (no client API call needed)
            // This is the FSM-driven approach: server controls the workflow
            _ = Task.Run(() => StartToolChangeControllerAsync(info, controller));
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
        controller.ErrorOccurred += onError;

        // Configure controller
        controller.Options = new MillingOptions
        {
            FilePath = AppState.CurrentFile?.FileName,
            DepthAdjustment = (float)AppState.DepthAdjustment,
            RequireHoming = !_machine!.IsHomed,
        };

        Logger.Log("Starting milling controller: RequireHoming={0}, DepthAdjustment={1:F3}",
            controller.Options.RequireHoming, controller.Options.DepthAdjustment);

        // Start sleep prevention
        SleepPrevention.Start();
        Logger.Log("Sleep prevention started: {0}", SleepPrevention.IsActive);

        // Start controller (fire and forget - events broadcast updates)
        _ = Task.Run(async () =>
        {
            try
            {
                await controller.StartAsync(_millCts.Token);
            }
            finally
            {
                // Unsubscribe from events
                controller.StateChanged -= onStateChanged;
                controller.ProgressChanged -= onProgressChanged;
                controller.ToolChangeDetected -= onToolChange;
                controller.ErrorOccurred -= onError;

                // Clear milling CTS to indicate operation is complete
                _millCts = null;

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

    private static async Task HandleMillStopAsync()
    {
        if (_machine == null)
        {
            return;
        }

        var controller = AppState.Milling;
        int position = _machine.FilePosition;
        Logger.Log("Mill stop requested at line {0}", position);

        // Cancel the milling operation
        _millCts?.Cancel();

        // Stop controller (handles cleanup: spindle off, Z retract)
        await controller.StopAsync();

        // Reset controller for next use
        if (controller.State != ControllerState.Idle)
        {
            controller.Reset();
        }

        Logger.Log("Mill stop complete");
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
        if (_machine == null || !_machine.Connected)
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

        Logger.Log($"HandleProbeTraceOutline: tracing outline for {grid.SizeX}x{grid.SizeY} grid");

        // Configure controller with grid and trace options
        controller.LoadGrid(grid);
        controller.Options = new ProbeOptions
        {
            TraceHeight = settings.OutlineTraceHeight,
            TraceFeed = settings.OutlineTraceFeed
        };

        try
        {
            await controller.TraceOutlineAsync(ct);
            Logger.Log("HandleProbeTraceOutline: complete");
        }
        catch (OperationCanceledException)
        {
            Logger.Log("HandleProbeTraceOutline: cancelled");
        }
    }

    private static void HandleProbeStart()
    {
        if (_machine == null || !_machine.Connected)
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

        // Set probing flag so WebSocket status reflects active state
        AppState.Probing = true;

        // Configure controller options
        controller.Options = new ProbeOptions
        {
            SafeHeight = settings.ProbeSafeHeight,
            MaxDepth = settings.ProbeMaxDepth,
            ProbeFeed = settings.ProbeFeed,
            MinimumHeight = settings.ProbeMinimumHeight,
            AbortOnFail = settings.AbortOnProbeFail,
            XAxisWeight = settings.ProbeXAxisWeight,
            TraceOutline = false  // Web UI doesn't support outline tracing yet
        };

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
                AppState.Probing = false;
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

    private static async void HandleProbeStop()
    {
        // Clear probing flag immediately so status reflects stopped state
        AppState.Probing = false;

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

        var grid = AppState.ProbePoints;
        var autosaveState = Persistence.GetProbeState();

        // 4-state model based on in-memory grid progress:
        //   none: no grid
        //   ready: grid exists, progress=0
        //   partial: 0 < progress < total
        //   complete: progress = total
        string state = ComputeProbeState(grid);

        // hasUnsavedData = autosave exists (determines Save vs Clear button)
        bool hasUnsavedData = autosaveState != Persistence.ProbeState.None;

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
        bool isActive = controllerState == ControllerState.Initializing ||
                        controllerState == ControllerState.Running ||
                        controllerState == ControllerState.Paused;
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

    private static string ComputeProbeState(ProbeGrid? grid)
    {
        if (grid == null)
        {
            return ProbeStateNone;
        }
        if (grid.NotProbed.Count == 0)
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
        clientId = query["clientId"];

        // Check if another client is already connected (web or TUI via proxy)
        bool hasOtherClient = false;
        lock (_clientsLock)
        {
            // Clean up expired pending clients first
            var expiredPending = _pendingClients
                .Where(kvp => (DateTime.Now - kvp.Value).TotalMilliseconds > PendingClientTimeoutMs)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var expired in expiredPending)
            {
                _pendingClients.Remove(expired);
            }

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

            bool isOnlyClient = false;
            int otherClientCount = 0;

            lock (_clientsLock)
            {
                // Clean up expired pending clients
                var expiredPending = _pendingClients
                    .Where(kvp => (DateTime.Now - kvp.Value).TotalMilliseconds > PendingClientTimeoutMs)
                    .Select(kvp => kvp.Key)
                    .ToList();
                foreach (var expired in expiredPending)
                {
                    _pendingClients.Remove(expired);
                }

                int totalClients = _clients.Count;
                int totalClientIds = _clientIds.Count;
                int pendingCount = _pendingClients.Count;

                // Check if this client ID already has an active connection or is pending
                bool isSameClient = false;
                if (requestClientId != null)
                {
                    isSameClient = _clientIds.ContainsValue(requestClientId) ||
                                   _pendingClients.ContainsKey(requestClientId);
                }

                // Count other connected clients (WebSocket) and pending clients
                otherClientCount = _clientIds.Count(kvp => kvp.Value != requestClientId);
                otherClientCount += _pendingClients.Count(kvp => kvp.Key != requestClientId);

                // Also count anonymous clients (WebSocket without clientId)
                int anonymousClients = totalClients - totalClientIds;
                otherClientCount += anonymousClients;

                // This client is the "only client" if they're connected/pending AND no others exist
                isOnlyClient = isSameClient && otherClientCount == 0;

                Logger.Log("ServeStaticFile: path={0}, requestClientId={1}, totalClients={2}, totalClientIds={3}, pendingCount={4}, otherClientCount={5}, isSameClient={6}, isOnlyClient={7}",
                    path, requestClientId ?? "null", totalClients, totalClientIds, pendingCount, otherClientCount, isSameClient, isOnlyClient);
            }

            // Note: We always serve index.html even if other clients exist.
            // The WebSocket handler will detect the conflict and send an error,
            // allowing the web UI to show a unified force-disconnect modal.

            // Generate a new client ID if this browser doesn't have one
            if (requestClientId == null)
            {
                requestClientId = Guid.NewGuid().ToString("N");
            }

            // Mark this client as pending (served page, waiting for WebSocket)
            lock (_clientsLock)
            {
                _pendingClients[requestClientId] = DateTime.Now;
            }

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
                    response.ContentType = "text/html";
                    await indexStream.CopyToAsync(response.OutputStream);
                    response.Close();
                    return;
                }
            }

            response.StatusCode = HttpStatusNotFound;
            response.ContentType = "text/plain";
            var notFound = Encoding.UTF8.GetBytes("Not Found");
            await response.OutputStream.WriteAsync(notFound);
            response.Close();
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
            ".html" => "text/html",
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".json" => "application/json",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            _ => "application/octet-stream"
        };
    }

    private static async Task WriteJson(HttpListenerResponse response, object data)
    {
        response.ContentType = "application/json";
        var json = JsonSerializer.Serialize(data);
        var bytes = Encoding.UTF8.GetBytes(json);
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

        if (probePoints == null || probePoints.NotProbed.Count > 0)
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

            // If probe data was already applied, reload original G-code first
            if (AppState.AreProbePointsApplied && !string.IsNullOrEmpty(AppState.Session.LastLoadedGCodeFile) &&
                File.Exists(AppState.Session.LastLoadedGCodeFile))
            {
                var originalFile = GCodeFile.Load(AppState.Session.LastLoadedGCodeFile);
                AppState.LoadGCodeIntoMachine(originalFile);
                Logger.Log("HandleProbeLoad: Reloaded G-code before loading new probe");
            }

            AppState.ProbePoints = ProbeGrid.Load(path);
            AppState.ResetProbeApplicationState();

            // Don't copy to autosave - loaded data is already saved (came from a file).
            // Autosave is only for data from active probing that hasn't been saved yet.
            // Clear any stale autosave to prevent "unsaved probe data" prompts.
            Persistence.ClearProbeAutoSave();

            // Auto-apply if probe is complete
            var complete = AppState.ProbePoints.NotProbed.Count == 0;
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

            var grid = AppState.ProbePoints;
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
    private static async Task StartToolChangeControllerAsync(ToolChangeInfo info, MillingController millingController)
    {
        if (_machine == null || !_machine.Connected)
        {
            Logger.Log("StartToolChangeControllerAsync: machine not connected");
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
        toolChangeController.Options = new ToolChangeOptions
        {
            ProbeMaxDepth = settings.ProbeMaxDepth,
            ProbeFeed = settings.ProbeFeed,
            RetractHeight = Constants.RetractZMm,
            WorkAreaCenter = currentFile != null && currentFile.ContainsMotion
                ? new Vector3(
                    (currentFile.Min.X + currentFile.Max.X) / 2,
                    (currentFile.Min.Y + currentFile.Max.Y) / 2,
                    0)
                : null
        };

        // Start tool change controller workflow
        _toolChangeCts = new CancellationTokenSource();

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
            _pendingToolChangeInput = request;
            BroadcastMessage(WsMessageTypeToolChangeInput, new
            {
                title = request.Title,
                message = request.Message,
                options = request.Options
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
            bool success = await toolChangeController.HandleToolChangeAsync(info, _toolChangeCts.Token);

            if (success)
            {
                Logger.Log("Tool change complete, resuming milling");
                BroadcastMessage(WsMessageTypeToolChangeComplete, new { success = true });
                millingController.Resume();
            }
            else
            {
                // Distinguish between user abort and actual failure
                bool wasAborted = toolChangeController.State == ControllerState.Cancelled;
                Logger.Log("Tool change {0}", wasAborted ? "aborted" : "failed");
                BroadcastMessage(WsMessageTypeToolChangeComplete, new { success = false, aborted = wasAborted });
            }
        }
        finally
        {
            toolChangeController.StateChanged -= onStateChanged;
            toolChangeController.ProgressChanged -= onProgressChanged;
            toolChangeController.UserInputRequired -= onUserInputRequired;
            toolChangeController.ErrorOccurred -= onError;
            _pendingToolChangeInput = null;
            _toolChangeCts = null;
        }
    }

    /// <summary>
    /// Handle tool change continue (DEPRECATED - tool change now auto-starts).
    /// Kept for backward compatibility - returns status of current tool change.
    /// </summary>
    private static async Task HandleToolChangeContinue(HttpListenerResponse response)
    {
        var toolChangeController = AppState.ToolChange;

        // Check if tool change is in progress
        bool isActive = toolChangeController.State != ControllerState.Idle &&
                        toolChangeController.State != ControllerState.Completed &&
                        toolChangeController.State != ControllerState.Failed &&
                        toolChangeController.State != ControllerState.Cancelled;

        if (!isActive)
        {
            response.StatusCode = HttpStatusBadRequest;
            await WriteJson(response, new { error = ErrorNoToolChangeInProgress });
            return;
        }

        // Tool change is already running (auto-started) - return status
        await WriteJson(response, new
        {
            success = true,
            message = "Tool change already in progress (auto-started)",
            usingToolSetter = toolChangeController.HasToolSetter,
            phase = toolChangeController.Phase.ToString()
        });
    }

    /// <summary>
    /// Handle user input response during tool change workflow.
    /// Called when user clicks Continue or Abort in the tool change dialog.
    /// </summary>
    private static async Task HandleToolChangeUserInput(HttpListenerResponse response, ToolChangeUserInputRequest? req)
    {
        if (_pendingToolChangeInput == null)
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
        _pendingToolChangeInput.OnResponse?.Invoke(req.response);
        _pendingToolChangeInput = null;

        await WriteJson(response, new { success = true });
    }

    /// <summary>
    /// Handle tool change abort. Stops milling and raises Z.
    /// Controller transitions to Cancelled state when CTS is cancelled.
    /// </summary>
    private static async Task HandleToolChangeAbortAsync()
    {
        Logger.Log("Tool change abort requested");

        // Cancel any running tool change controller
        // Controller will transition to Cancelled state
        _toolChangeCts?.Cancel();

        // Stop milling (this handles spindle off, Z raise, etc.)
        await HandleMillStopAsync();

        // Reset tool change controller so DetectToolChange returns null
        var toolChangeController = AppState.ToolChange;
        Logger.Log("Tool change abort: State={0}, Phase={1}", toolChangeController.State, toolChangeController.Phase);
        if (toolChangeController.State != ControllerState.Idle)
        {
            toolChangeController.Reset();
            Logger.Log("Tool change controller reset after abort");
        }
        else
        {
            Logger.Log("Tool change controller already Idle, skipping Reset");
        }
    }

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
                complete = grid.NotProbed.Count == 0,
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
