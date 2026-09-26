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
using coppercli.Core.Settings;
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
/// Serves the embedded web UI, the /api/ JSON API, and the /ws WebSocket for status and
/// jog commands. HTTP handlers call workflows in coppercli.Core and return their results.
///
/// Stop paths acquire <see cref="_toolChangeAbortLock"/>, wait for the tool-change task,
/// then acquire <see cref="_millStopLock"/>. Keep this order to avoid deadlock.
/// </summary>
public static class CncWebServer
{
    private static HttpListener? _listener;
    private static CancellationTokenSource? _cts;

    private sealed class ClientConnection
    {
        public required WebSocket Socket { get; init; }

        /// <summary>The browser id from its cookie, or null if absent.</summary>
        public string? Id { get; init; }

        /// <summary>
        /// Last client activity on the monotonic clock, read and written under
        /// <see cref="_clientsLock"/>. Wall-clock changes could expire every client together.
        /// </summary>
        public long LastActivityMs { get; set; }

        /// <summary>
        /// A WebSocket takes one send at a time and several threads write to this one. Not
        /// disposed when the client goes: a send still queued behind it would throw.
        /// </summary>
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }

    private static readonly List<ClientConnection> _clients = new();
    // Served the page, no WebSocket yet.
    private static readonly Dictionary<string, long> _pendingClients = new();
    private static readonly object _clientsLock = new();
    internal const int WebSocketTimeoutMs = 30000;

    private static Machine? _machine;

    /// <summary>
    /// True when a machine object exists and is connected. The MemberNotNullWhen attribute
    /// lets callers write <c>if (!MachineConnected) return;</c> and still have the compiler
    /// treat <c>_machine</c> as non-null afterward.
    /// </summary>
    [MemberNotNullWhen(true, nameof(_machine))]
    private static bool MachineConnected => _machine != null && _machine.Connected;


    // Created by Run; null while no server is running.
    private static MachineHold? _hold;

    // HandleMillStart assigns a new instance before scheduling the run; the run clears
    // this field only if it still holds that instance.
    private static CancellationTokenSource? _millCts;

    // Track the StartAsync task so StopMillingAsync waits for its cleanup before resetting
    // the controller.
    private static Task? _millRunTask;

    // The combined stop path and a failed tool change can call StopMillingAsync together.
    // Serialize them so only one changes the controller state at a time.
    private static readonly SemaphoreSlim _millStopLock = new(1, 1);

    // HandleMillStart's onToolChange callback assigns a new token before scheduling
    // the tool-change run.
    private static CancellationTokenSource? _toolChangeCts;

    // Track the tool-change task so HandleMillStopAsync waits for its cleanup and reset
    // before attempting another reset.
    private static Task? _toolChangeRunTask;

    // Serialize Stop and Abort requests through HandleMillStopAsync so two callers
    // cannot stop both controllers concurrently.
    private static readonly SemaphoreSlim _toolChangeAbortLock = new(1, 1);

    // Grid probes and outline traces share this controller and cannot run together.
    // The run clears its token in finally only if it is still current, without
    // disposing it; a timed-out stop leaves the run using that token.
    private static CancellationTokenSource? _probeCts;
    private static Task? _probeTask;

    private static string? _webClientAddress;

    // The proxy sharing the serial port, or null when there is none.
    private static SerialProxy? _proxy;

    /// <summary>
    /// Whether the server should have the machine; false while a terminal has it or a
    /// takeover waits for one. See <see cref="MachineHold.IsHeld"/>.
    /// </summary>
    internal static bool HoldsMachine => _hold?.IsHeld ?? true;

    /// <summary>
    /// For <see cref="SerialProxy.TryClaimSerialPort"/>. Refused while no server is running:
    /// before Run the server is about to open the port itself, and after it the proxy is
    /// about to stop.
    /// </summary>
    public static bool TryClaimSerialPort() => _hold?.TryClaimSerialPort() ?? false;

    /// <summary>For <see cref="SerialProxy.ReleaseSerialPort"/>.</summary>
    public static void ReleaseSerialPort() => _hold?.ReleaseSerialPort();

    /// <summary>True while a browser holds the one WebSocket this server allows.</summary>
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

    /// <summary>Blocks until <see cref="Stop"/> cancels it or the listener fails.</summary>
    /// <param name="startedSignal">Set once the server is ready.</param>
    /// <param name="proxy">The proxy sharing the serial port, or null when there is none.</param>
    public static void Run(int port, ManualResetEvent startedSignal, SerialProxy? proxy = null)
    {
        Logger.Log("CncWebServer.Run: starting on port {0}", port);
        _machine = AppState.Machine;
        _proxy = proxy;
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
            MenuHelpers.ShowFailure(CliConstants.FailedListeningOnTheNetwork, ex);
            AnsiConsole.MarkupLine($"[{ColorDim}]Trying localhost only...[/]");

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
        }

        var machine = _machine;
        var hold = new MachineHold(
            () => machine.Connected,
            () => ConnectMachine(machine),
            machine.Disconnect);
        _hold = hold;
        _ = hold.KeepConnectedAsync(_cts.Token);

        // Started before the ready signal, so a client that connects at once has a loop to
        // broadcast to.
        Logger.Log("CncWebServer.Run: starting BroadcastStatusLoop");
        _ = BroadcastStatusLoop(_cts.Token);

        Logger.Log("CncWebServer.Run: signaling ready");
        startedSignal.Set();

        Logger.Log("CncWebServer.Run: entering main request loop");
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var contextTask = _listener.GetContextAsync();
                    // Polled rather than awaited, so cancellation is noticed while no request
                    // arrives.
                    while (!contextTask.IsCompleted && !_cts.Token.IsCancellationRequested)
                    {
                        contextTask.Wait(RequestPollTimeoutMs, _cts.Token);
                    }

                    if (contextTask.IsCompletedSuccessfully)
                    {
                        _ = HandleRequest(contextTask.Result);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One connection that fails while it is being accepted - a client that
                    // resets mid-handshake, a malformed request - is not a reason to stop
                    // serving. Without this the whole web UI went down with it.
                    Logger.Log("CncWebServer: accepting a request failed - {0}", ex.Message);

                    // Unless the listener itself is gone, in which case every further call
                    // throws at once and this would spin.
                    if (!_listener.IsListening)
                    {
                        Logger.Log("CncWebServer: listener is no longer bound");
                        break;
                    }
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

            // Run also ends without Stop when the listener fails, so cancel here as well. First,
            // so the hold does not reconnect while the runs below unwind.
            _cts.Cancel();

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
                // Cancel first so the runs unwind while the machine is still reachable:
                // their teardown stops it and retracts. A run left going would send moves to
                // the next connection.
                var (probeCts, probeTask) = GetCurrentProbeRun();

                var running = new[] { probeTask, _millRunTask, _toolChangeRunTask }
                    .Where(task => task != null)
                    .ToArray();

                probeCts?.Cancel();
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

                Logger.Log("CncWebServer: stopping the hold and disconnecting");
                hold.Stop();
                _hold = null;
                _proxy = null;

                Logger.Log("CncWebServer: stopping listener");
                _listener.Stop();
                Logger.Log("CncWebServer: listener stopped");

                // Cleared for a clean restart in the same process.
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

    public static void Stop()
    {
        _cts?.Cancel();
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

            if (!RequestPolicy.IsAllowed(request))
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
                // The upgrade takes over the connection; nothing here may touch it after.
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
    /// The web UI drives a machine from large on-screen buttons, so a page that framed it
    /// could put an invisible copy over those buttons. Inside the frame the UI runs at its own
    /// origin and every request it makes is same-origin, so only refusing to be framed stops
    /// it.
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
    /// A command that sends one instruction to the machine and needs nothing else from the
    /// request. The HTTP endpoint and the WebSocket command run the same entry.
    /// </summary>
    /// <param name="Path">The HTTP endpoint that runs it.</param>
    /// <param name="WsCommand">The WebSocket command that runs it, or null for none.</param>
    /// <param name="Run">What it asks of the machine: null once the command has been sent, or
    /// the reason it was refused. Without a return value a refusal would be dropped and the
    /// browser told it succeeded.</param>
    /// <param name="DuringRun">
    /// Whether it may be sent while a workflow is driving the machine. True only for the
    /// controls used during a job: stop, hold, resume, unlock, feed override. Anything that
    /// starts a move of its own is false.
    /// </param>
    internal sealed record DirectCommand(
        string Path, string? WsCommand, Func<Machine, string?> Run, bool DuringRun = false);

    private static readonly DirectCommand[] DirectCommands =
    {
        new(ApiHome, null, machine => MachineCommands.HomeAndWait(machine).FailureMessage),
        new(ApiUnlock, null, MachineCommands.Unlock, DuringRun: true),
        new(ApiReset, WsCmdReset, machine => { machine.SoftReset(); return null; }, DuringRun: true),
        // Unguarded on purpose: CanPause decides whether the Pause control is enabled, but
        // a feed hold is harmless in every state, so it is always sent.
        new(ApiFeedhold, WsCmdFeedhold, machine => { machine.FeedHold(); return null; },
            DuringRun: true),
        // Resume releases a feed hold. A door hold restarts the spindle, so it goes through
        // the workflow's prompt and this command does nothing at the door.
        new(ApiResume, null, machine =>
            {
                var activity = MachineWait.GetActivity(machine);
                if (!MachineWait.CanResume(activity)) { return GetResumeRefusalMessage(machine, activity); }
                machine.CycleStart();
                return null;
            }, DuringRun: true),

        // X0 Y0, leaving Z where it is - the same as the TUI's key for it.
        new(ApiGotoOrigin, WsCmdGotoOrigin, machine => { MachineCommands.GotoWorkOriginXY(machine); return null; }),
        new(ApiGotoCenter, WsCmdGotoCenter,
            machine => { MachineCommands.GotoFileCenterXY(machine, AppState.CurrentFile); return null; }),
        new(ApiGotoSafe, WsCmdGotoSafe,
            machine => { MachineCommands.MoveToSafeHeight(machine, Constants.RetractZMm); return null; }),
        new(ApiGotoRef, WsCmdGotoRef,
            machine => { MachineCommands.MoveToSafeHeight(machine, ReferenceZHeightMm); return null; }),
        new(ApiGotoZ0, WsCmdGotoZ0, machine => { MachineCommands.MoveToSafeHeight(machine, 0); return null; }),
        new(ApiProbeZ, WsCmdProbeZ, _ => { ProbeZSingle(); return null; }),

        // Feed override has no WebSocket command: the mill screen adjusts it over HTTP.
        new(ApiFeedIncrease, null, machine => { machine.FeedOverrideIncrease(); return null; }, DuringRun: true),
        new(ApiFeedDecrease, null, machine => { machine.FeedOverrideDecrease(); return null; }, DuringRun: true),
        new(ApiFeedReset, null, machine => { machine.FeedOverrideReset(); return null; }, DuringRun: true),
    };

    /// <summary>
    /// Why a cycle start would do nothing in this state. Only the door needs the operator
    /// to act; the other cases report what the machine is doing.
    /// </summary>
    private static string GetResumeRefusalMessage(Machine machine, MachineActivity activity) =>
        MachineWait.GetDoorRefusal(machine) ?? activity switch
        {
            MachineActivity.Alarm => ControllerConstants.ErrorAlarmBeforeStart,
            MachineActivity.Disconnected => ErrorMachineNotConnected,
            _ => ErrorNothingToResume
        };

    /// <summary>
    /// Why a release straight from a browser will not be taken, or null once it will. The
    /// endpoint and the button both read this, so the browser cannot offer a Continue the
    /// server would refuse.
    /// </summary>
    private static string? WhyTheDoorCannotBeReleasedHere()
    {
        var machine = _machine;
        if (machine == null)
        {
            return ErrorMachineNotConnected;
        }

        // A run holds the machine until it ends, parked at a prompt or not. It may have moves
        // queued for the moment the hold lifts, and it releases the hold itself on a Continue
        // the operator has already given.
        if (AnyOperationRunning())
        {
            return PendingPrompt.Current?.IsDoorPrompt == true
                ? ControllerConstants.ErrorDoorAnswerThePrompt
                : ErrorMachineBusy;
        }

        // Only a closed door with the park move ended can be released. For any other door
        // state ReleaseDoorHoldAsync waits for a fresh reading, and a switch that flipped
        // closed inside that wait would get the cycle start.
        if (!MachineWait.CanReleaseDoorHold(machine))
        {
            return MachineWait.GetDoorRefusal(machine) ?? ErrorNoDoorToRelease;
        }

        return null;
    }

    /// <summary>
    /// Releases a door hold from the browser's door overlay, where the click is the
    /// confirmation. MachineWait.ReleaseDoorHoldAsync is the only place that sends the cycle
    /// start, and it checks GRBL's reading of the switch first.
    /// </summary>
    /// <returns>Null once the hold is released, or the reason it was not.</returns>
    private static async Task<string?> ReleaseDoorHoldAsync()
    {
        string? refused = WhyTheDoorCannotBeReleasedHere();
        if (refused != null)
        {
            return refused;
        }

        var machine = _machine!;

        var left = await MachineWait.ReleaseDoorHoldAsync(
            machine, ControllerConstants.DoorResumeTimeoutMs);

        // Still restoring means the cycle start was accepted, so it is not a failure.
        return left is DoorState.None or DoorState.Resuming
            ? null
            : MachineWait.GetDoorRefusal(left);
    }

    private static DirectCommand? FindDirectCommand(Func<DirectCommand, bool> match) =>
        DirectCommands.FirstOrDefault(match);

    /// <summary>The command an HTTP endpoint runs, or null for none.</summary>
    internal static DirectCommand? FindHttpCommand(string path) =>
        FindDirectCommand(command => command.Path == path);

    /// <summary>
    /// Whether a stored connection is one this browser left behind, so a new connection
    /// replaces it. A socket still open belongs to a second tab, and dropping that would
    /// leave the tab sending commands with no status.
    /// </summary>
    internal static bool IsSupersededClient(
        string? storedId, WebSocketState storedState, string newClientId) =>
        storedId == newClientId && storedState != WebSocketState.Open;

    /// <summary>
    /// The command a WebSocket message of this type runs, or null for none. A message with
    /// no type matches nothing: matching null would select the entries that have no command.
    /// </summary>
    internal static DirectCommand? FindWsCommand(string? type) =>
        type == null ? null : FindDirectCommand(command => command.WsCommand == type);

    /// <summary>
    /// Run a direct command, or return why it could not. <paramref name="offTheCallingThread"/>
    /// is for the WebSocket: a command that waits for the machine must not hold up the socket,
    /// which must remain available to receive Stop.
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
            return command.Run(machine);
        }

        // A command run by Task.Run has no caller to return a refusal to, so the refusal is
        // logged. A control whose refusal the operator must see is sent over HTTP instead.
        _ = Task.Run(() =>
        {
            try
            {
                string? refused = command.Run(machine);
                if (refused != null) { Logger.Log("Command {0} refused: {1}", command.Path, refused); }
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
    /// Answer a request to start something: success, or the reason it was refused. The
    /// browser opens its screen on this answer.
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
    /// The directory the caller asked for, else the one that browser last used, else home.
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

        // Commands that send one instruction and need nothing else from the request are
        // handled from the table shared with the WebSocket, rather than a case each.
        var direct = FindHttpCommand(path);
        if (direct != null)
        {
            if (await RequireMethod(response, method, MethodPost))
            {
                // Homing blocks until it finishes, and run here it would hold the accept loop
                // and every request behind it.
                await WriteStartResult(response, await Task.Run(() => RunDirectCommand(direct)));
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

            case ApiZero:
                if (await RequireMethod(response, method, MethodPost))
                {
                    var zeroReq = await ReadBody<ZeroRequest>(request, response);
                    if (zeroReq != null)
                    {
                        var (zeroRefused, mapOutcome) = HandleZero(zeroReq);
                        if (zeroRefused != null)
                        {
                            // Only a malformed axes list is the caller's mistake. A busy or
                            // disconnected machine is a conflict, as it is everywhere else.
                            response.StatusCode = zeroRefused == ErrorInvalidRequest
                                ? HttpStatusBadRequest
                                : HttpStatusConflict;
                            await WriteJson(response, new { error = zeroRefused });
                        }
                        else
                        {
                            // The enum name goes over the wire; the browser holds the wording,
                            // as it does for every other activity name.
                            await WriteJson(response, new
                            {
                                success = true,
                                heightMap = mapOutcome.ToString(),

                                // Core decides which outcomes need the operator to act, so
                                // the browser does not list them again.
                                reloadTheFile = mapOutcome.LeftTheGCodeWrong()
                            });
                        }
                    }
                }
                break;

            // File browsers for G-code and saved probe grids: the same listing with a
            // different filter and its own browse directory.
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

            case ApiMillCanStart:
                if (await RequireMethod(response, method, MethodGet))
                {
                    await WriteJson(response, HandleMillCanStart());
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

                    // A tool change leaves the milling controller Paused, and resuming here
                    // while one is under way would restart file streaming mid tool-swap.
                    // Gated on the milling controller's own Phase rather than DetectToolChange
                    // (the tool-change controller's status): Phase flips to ToolChange before
                    // the Paused transition and before the event that starts the tool-change
                    // controller fires, while DetectToolChange lags up to a few seconds behind
                    // it (see DetectToolChange's own remarks).
                    bool toolChangeActive = resumeController.Phase == MillingPhase.ToolChange;

                    // The same check Resume() makes, made first so the refusal comes back
                    // with the response rather than only as an error event.
                    string? blocked = _machine == null
                        ? ErrorMachineNotConnected
                        : MachineWait.GetDoorRefusal(_machine);

                    if (resumeController.IsPaused && !toolChangeActive && blocked == null)
                    {
                        resumeController.Resume();
                        await WriteJson(response, new { success = true });
                    }
                    else
                    {
                        response.StatusCode = HttpStatusBadRequest;
                        await WriteJson(response, new
                        {
                            error = blocked
                                ?? (toolChangeActive ? ErrorCannotResumeToolChangeActive : ErrorCannotResumeNotPaused)
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

            case ApiDoorRelease:
                if (await RequireMethod(response, method, MethodPost))
                {
                    await WriteStartResult(response, await ReleaseDoorHoldAsync());
                }
                break;

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
                    {
                        // A refusal, not a stop that failed: nothing was sent.
                        string? refused = ProbeStopBlocker();
                        await WriteStopResult(
                            response, refused == null && await HandleProbeStop(), refused);
                    }
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
                    // Applying rewrites the loaded G-code, which rewinds the file that a
                    // paused run would resume from.
                    string? notApplied = AppState.ApplyProbeData();
                    if (notApplied != null)
                    {
                        response.StatusCode = HttpStatusConflict;
                    }

                    await WriteJson(response, new
                    {
                        success = notApplied == null,
                        error = notApplied,
                        applied = AppState.AreProbePointsApplied
                    });
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
                    // A refusal, not a server failure: the map is where it was, and the
                    // message gives the reason.
                    string? notDiscarded = HandleProbeDiscard();
                    if (notDiscarded != null)
                    {
                        response.StatusCode = HttpStatusConflict;
                    }

                    await WriteJson(response, new
                    {
                        success = notDiscarded == null,
                        error = notDiscarded
                    });
                }
                break;


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
                        string? refusedDepth = HandleDepthAdjustment(depthReq);
                        if (refusedDepth != null)
                        {
                            response.StatusCode = HttpStatusBadRequest;
                            await WriteJson(response, new { error = refusedDepth });
                            break;
                        }

                        await WriteJson(response, new { success = true, depth = AppState.DepthAdjustment });
                    }
                }
                break;

            case ApiMillGrid:
                if (await RequireMethod(response, method, MethodGet))
                {
                    // Read before the cells, so the count never describes a longer path than
                    // they were built from. The dimensions come from the client, which sizes
                    // them to its screen from the maxima this server published.
                    int pathCount = AppState.Milling.CuttingPath.Count;

                    await WriteJson(response, new
                    {
                        cells = GetVisitedGridCells(
                            AppState.Milling,
                            QueryInt(request, QueryParamWidth, WebMillGridDefaultWidth, MillGridMaxWidth),
                            QueryInt(request, QueryParamHeight, WebMillGridDefaultHeight, MillGridMaxHeight)),
                        count = pathCount
                    });
                }
                break;

            case ApiBrowserTakeover:
                if (await RequireMethod(response, method, MethodPost))
                {
                    await HandleBrowserTakeover(context);
                }
                break;

            case ApiTerminalTakeover:
                if (await RequireMethod(response, method, MethodPost))
                {
                    await HandleTerminalTakeover(context);
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
                            detail = step.Detail
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

                    // A missing answer is refused, not read as no, which deletes an unsaved or
                    // unfinished map. The topic must match a name exactly: Enum.TryParse also
                    // takes numbers and comma lists.
                    if (answer.topic == null || answer.detail == null || answer.yes == null
                        || !Enum.TryParse<SessionRestoreTopic>(answer.topic, out var topic)
                        || topic.ToString() != answer.topic)
                    {
                        response.StatusCode = HttpStatusBadRequest;
                        await WriteJson(response, new { error = ErrorInvalidRequest });
                        break;
                    }

                    string? failed = SessionRestore.Answer(topic, answer.detail, answer.yes.Value);
                    if (failed != null)
                    {
                        response.StatusCode = HttpStatusConflict;
                    }

                    await WriteJson(response, new { success = failed == null, error = failed });
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
    /// Machine.Connect reports a failure through its NonFatalException event and returns, so
    /// this throws an exception carrying that message, for <see cref="MachineHold"/> to retry. Once connected, it turns automatic
    /// state clearing back on unless <see cref="AnyOperationRunning"/> is true.
    /// </summary>
    internal static void ConnectMachine(Machine machine)
    {
        string? failure = null;
        void OnError(string message) => failure = message;

        machine.NonFatalException += OnError;
        try
        {
            machine.Connect();
        }
        finally
        {
            machine.NonFatalException -= OnError;
        }

        if (!machine.Connected)
        {
            throw new IOException(failure ?? "Machine.Connect returned without connecting");
        }

        if (!AnyOperationRunning())
        {
            machine.EnableAutoStateClear = true;
        }
    }

    /// <summary>
    /// Whether anything is using the machine. AppState covers the runs, so a run parked at a
    /// prompt counts; homing and an open probe cycle are added here because only the server's
    /// machine handle reports them.
    /// </summary>
    private static bool AnyOperationRunning() =>
        AppState.IsRunInProgress
        || (_machine?.IsHoming ?? false)
        || (_machine != null && MachineWait.IsProbeCycleOpen(_machine));

    /// <summary>
    /// Whether a workflow controls machine motion and a separate command would interfere.
    /// Narrower than <see cref="AnyOperationRunning"/>.
    /// </summary>
    private static bool MachineIsBeingDriven()
    {
        // IsRunInProgress rather than IsActive: a probe parked at its enclosure prompt still
        // holds the machine, and homing there destroys the frame the rest of the run measures in.
        if (AppState.Probe.IsRunInProgress || (_machine?.IsHoming ?? false)
            || (_machine != null && MachineWait.IsProbeCycleOpen(_machine)))
        {
            return true;
        }

        // A tool change is the one pause the operator is meant to jog through, because it
        // asks them to set Z0; IsActive excludes it. Every other hold leaves the tool where
        // the job put it, so a move would offset the rest of the pass.
        if (AppState.ToolChange.IsActive)
        {
            return true;
        }

        var milling = AppState.Milling;
        return milling.IsRunInProgress && milling.Phase != MillingPhase.ToolChange;
    }

    private static object GetStatus()
    {
        // AppState.Machine exists before the server starts. Use it when _machine is unset
        // so every status response contains the same fields.
        var machine = _machine ?? AppState.Machine;

        var controller = AppState.Milling;
        var controllerState = controller.State;
        var controllerPhase = controller.Phase;

        // One read of the map for the whole payload. Read again for the buttons, the probe
        // panel and the Mill button could describe different data, and the autosave would be
        // parsed twice every broadcast.
        var (probeGrid, probeState, hasUnsavedProbeData) = ReadProbeStateSnapshot();

        // Still running while the run waits on the operator or retracts.
        var isMilling = ControllerBase.IsRunInProgressState(controllerState);

        // Use AppState's state set by the tool-change event, or the current prompt,
        // for the shared dialog.
        // Status must include it because a reconnecting client missed the one-time
        // toolchange:input broadcast.
        var toolChange = DetectToolChange() ?? DetectPendingPrompt();

        var settings = AppState.Settings;
        var profile = !string.IsNullOrEmpty(settings.MachineProfile)
            ? MachineProfiles.GetProfile(settings.MachineProfile)
            : null;

        // One read each of what the receive thread rewrites, so the payload describes one
        // instant. Read per field, machineActivity and doorMessage could disagree.
        var activity = MachineWait.GetActivity(machine);
        var doorState = MachineWait.GetDoorState(machine);
        var status = machine.Status;
        var workPos = machine.WorkPosition;
        var machinePos = machine.MachinePosition;

        return new
        {
            connected = MachineWait.IsResponding(activity),

            // GRBL's own word, for display only. See rule browser-uses-core-status-values.
            status,

            machineActivity = activity.ToString(),
            needsAttention = MachineWait.NeedsAttention(activity),

            // Wider than needsAttention: it also covers a machine that is not there. The
            // jog controls read this, because a disconnected machine takes no move either.
            machineUnavailable = MachineWait.IsUnavailable(activity),
            canPause = MachineWait.CanPause(activity),
            canResume = MachineWait.CanResume(activity),
            canReleaseDoor = MachineWait.CanReleaseDoorHold(doorState),

            // The sentence the door overlay shows. It is defined in Core, because it is the
            // same prompt a run raises; the header's short label is the browser's own wording.
            doorMessage = MachineWait.IsDoorActivity(activity)
                ? MachineWait.GetDoorMessage(doorState)
                : null,
            machineProfile = profile?.Name,
            workPos = new { x = workPos.X, y = workPos.Y, z = workPos.Z },
            machinePos = new { x = machinePos.X, y = machinePos.Y, z = machinePos.Z },
            feedOverride = machine.FeedOverride,
            probePin = machine.PinStateProbe,
            file = GetFileStatus(),
            probe = GetProbeStatusBrief(probeGrid, probeState, hasUnsavedProbeData),
            probeApplied = AppState.AreProbePointsApplied,
            milling = isMilling,
            millingPhase = controllerPhase.ToString(),
            controllerState = controllerState.ToString(),
            cuttingPathCount = controller.CuttingPath.Count,  // The browser fetches the grid when this grows
            probing = AppState.IsMeasuringGrid,
            tracingOutline = AppState.IsTracingOutline,
            toolChange = toolChange,
            depthAdjustment = AppState.DepthAdjustment,
            buttons = GetButtonStates(probeGrid)
        };
    }

    /// <summary>
    /// Tool-change status for display, from ToolChangeController. It lags the milling
    /// controller's MillingPhase.ToolChange by up to a few seconds, because
    /// StartToolChangeControllerAsync only reaches it once the run task is picked up by the
    /// thread pool, so a caller that needs the answer synchronously (gating /api/mill/resume,
    /// for one) reads AppState.Milling.Phase instead; which phase puts what on screen is
    /// documented on <see cref="ToolChangePhase"/>.
    /// </summary>
    private static object? DetectToolChange()
    {
        var controller = AppState.ToolChange;
        var phase = controller.Phase;
        var state = controller.State;

        // Whether a run is under way comes from ControllerState. Reading it off the phase
        // made one enum answer two questions, which could disagree.
        if (!controller.IsActive && !ControllerBase.IsWaitingForOperatorState(state))
        {
            return null;
        }

        var info = controller.CurrentToolChange;

        if (info == null)
        {
            Logger.Log($"DetectToolChange: phase={phase}, state={state}, info=null (BUG!)");
        }

        // The prompt now waiting, if any: a client that reloaded mid-tool-change missed the
        // toolchange:input broadcast, and an answer has to name its prompt. Title and message
        // are sent because a tool change raises two kinds of prompt, and one rebuilt from the
        // phase alone would show "change the tool and press Continue" over a door prompt whose
        // Continue restarts the spindle.
        var pending = PendingPrompt.Current;

        return new
        {
            phase = phase.ToString(),
            toolNumber = info?.ToolNumber,
            toolName = info?.ToolName,
            title = pending?.Title,
            message = pending?.Message,
            id = pending?.Id,
            options = pending?.Options,
            isDoorPrompt = pending?.IsDoorPrompt ?? false
        };
    }

    /// <summary>
    /// Return the current prompt for any run, including a probe or outline trace after
    /// the client reloads. Use the same payload as the tool-change overlay.
    /// </summary>
    private static object? DetectPendingPrompt()
    {
        var pending = PendingPrompt.Current;
        return pending == null ? null : GetOperatorPausePayload(pending);
    }

    /// <summary>
    /// A paused run's prompt on the wire. The text is the workflow's, because a door prompt
    /// drawn under the tool-change heading would ask about the tool instead.
    /// </summary>
    internal static object GetOperatorPausePayload(UserInputRequest pending) => new
    {
        phase = PromptKindOperatorPause,
        title = pending.Title,
        message = pending.Message,
        options = pending.Options,
        id = pending.Id,
        isDoorPrompt = pending.IsDoorPrompt
    };

    /// <summary>
    /// Store the prompt for later answers and send it to connected browsers.
    /// </summary>
    /// <returns>
    /// The stored prompt. The run clears it only if it is still current.
    /// </returns>
    private static UserInputRequest PublishPrompt(UserInputRequest request)
    {
        // Keep the request id so a client can match the status payload to this broadcast.
        var published = new UserInputRequest
        {
            Id = request.Id,
            Title = request.Title,
            Message = request.Message,
            Options = request.Options,
            IsDoorPrompt = request.IsDoorPrompt,
            OnResponse = response =>
            {
                request.OnResponse(response);

                // Answer clears the pending prompt before calling this, but the callback
                // may publish another. Close the dialog only if none is pending.
                if (PendingPrompt.Current == null)
                {
                    BroadcastMessage(WsMessageTypeToolChangeComplete, new { success = true });
                }
            }
        };

        PendingPrompt.Set(published);
        BroadcastMessage(WsMessageTypeToolChangeInput, new
        {
            title = request.Title,
            message = request.Message,
            options = request.Options,
            id = request.Id,

            // The enclosure prompt is drawn in the page-level door overlay, because the
            // tool-change overlay lives inside the mill screen and is not on the page at all
            // while the operator is on the probe or jog screen.
            isDoorPrompt = request.IsDoorPrompt
        });

        return published;
    }

    /// <param name="probeGrid">
    /// The map the rest of this payload was built from, so the buttons and the probe panel
    /// cannot describe different data - and the autosave is read once rather than twice.
    /// </param>
    private static object GetButtonStates(ProbeGrid? probeGrid)
    {
        string? jogReason = MenuHelpers.GetMachineDisabledReason();

        string? probeReason = MenuHelpers.GetProbeDisabledReason();

        string? millReason = MenuHelpers.GetMillDisabledReason(probeGrid);

        string? doorReleaseReason = WhyTheDoorCannotBeReleasedHere();

        return new
        {
            jog = new { enabled = jogReason == null, reason = jogReason },
            probe = new { enabled = probeReason == null, reason = probeReason },
            mill = new { enabled = millReason == null, reason = millReason },

            // The door overlay's own Continue, for a hold with no run behind it. A run at
            // the door asks its own question, or releases the hold on a Continue already
            // given, and the browser must not offer a second one over the top.
            doorRelease = new { enabled = doorReleaseReason == null, reason = doorReleaseReason }
        };
    }

    private static object? GetProbeStatusBrief(
        ProbeGrid? grid, string state, bool hasUnsavedData)
    {
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

            // What a summary counts. Progress is how far through the queue the run is, and
            // a skipped point comes off the queue without being measured.
            measured = grid.MeasuredCount,
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

    /// <summary>The values the browser must hold the same; validateConstants compares them
    /// against these at startup.</summary>
    private static object GetSharedConstants()
    {
        return new
        {
            // What a saved height map is called on disk.
            probeGridExtension = CliConstants.ProbeGridExtension,

            // Asked before every mill run, by the terminal and the browser.
            probeRemovedQuestion = CliConstants.ProbeRemovedQuestion,

            // The warning before an X or Y zero, so both front ends use the same wording.
            zeroWarning = new
            {
                discardsMap = CliConstants.ZeroDiscardsMap,
                unmeasured = CliConstants.UnmeasuredMap,
                partlyMeasured = CliConstants.PartlyMeasuredMap,
                complete = CliConstants.CompleteMap
            },
            // What became of the height map after a zero. The browser has its own words for
            // each, so only the names have to agree.
            heightMapOutcomes = new
            {
                reapplied = nameof(WorkZeroOutcome.MapReapplied),
                notReapplied = nameof(WorkZeroOutcome.MapNotReapplied),
                notDiscarded = nameof(WorkZeroOutcome.MapNotDiscarded),
                discarded = nameof(WorkZeroOutcome.MapDiscarded),
                fileLeftAlone = nameof(WorkZeroOutcome.FileLeftAlone)
            },
            // The activity names the browser branches on: the door states it has its own
            // text for. It shows GRBL's status word for the rest, so those are not published.
            machineActivities = new
            {
                doorOpen = nameof(MachineActivity.DoorOpen),
                doorRetracting = nameof(MachineActivity.DoorRetracting),
                doorHolding = nameof(MachineActivity.DoorHolding),
                doorResuming = nameof(MachineActivity.DoorResuming)
            },
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
            // The options a prompt offers. The client draws a button per option and sends
            // the option back, so a rename on either side has to be caught.
            promptOptions = new
            {
                carryOn = ControllerConstants.OptionContinue,
                abandon = ControllerConstants.OptionAbort
            },
            // The phases the client changes its display for. The rest are shown as sent,
            // so only these have to match.
            phases = new
            {
                milling = nameof(MillingPhase.Milling),
                tracingOutline = nameof(ProbePhase.TracingOutline),
                waitingForZeroZ = nameof(ToolChangePhase.WaitingForZeroZ)
            },
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
            wsCloseReasons = new
            {
                forceDisconnect = WsCloseReasonForceDisconnect
            },
            decimals = new
            {
                brief = PositionDecimalsBrief,
                full = PositionDecimalsFull
            },
            probe = new
            {
                minMargin = MinProbeMargin,
                maxMargin = MaxProbeMargin,
                minGridSize = MinProbeGridSize,
                maxGridSize = MaxProbeGridSize
            },
            // How the browser addresses the socket and names itself on it. Spelled out in
            // the browser instead, a rename here connects it anonymously and its own reload
            // counts as a second client.
            socket = new
            {
                path = WsPath,
                clientIdParam = QueryParamClientId,
                clientIdCookie = ClientIdCookieName,

                // The browser pings well inside this, or every client is reaped mid-job.
                timeoutMs = WebSocketTimeoutMs
            },
            // What the depth buttons send. Named on one side only, a rename answers 200
            // with the depth unchanged.
            depthActions = new
            {
                increase = DepthActionIncrease,
                decrease = DepthActionDecrease,
                reset = DepthActionReset
            },
            probeStates = new
            {
                none = ProbeStateNone,
                ready = ProbeStateReady,
                partial = ProbeStatePartial,
                complete = ProbeStateComplete
            },
            millGrid = new
            {
                maxWidth = MillGridMaxWidth,
                maxHeight = MillGridMaxHeight,
                cuttingDepthThreshold = MillCuttingDepthThreshold,
                minRangeThreshold = MillMinRangeThreshold
            },
            depthAdjustment = new
            {
                increment = DepthAdjustmentIncrement,
                max = DepthAdjustmentMax
            },
            thresholds = new
            {
                heightRangeEpsilon = HeightRangeEpsilon,
                millMinRange = MillMinRangeThreshold
            },
            commands = new
            {
                ping = WsCmdPing,
                jogMode = WsCmdJogMode,
                reset = WsCmdReset,
                feedhold = WsCmdFeedhold,
                gotoOrigin = WsCmdGotoOrigin,
                gotoCenter = WsCmdGotoCenter,
                gotoSafe = WsCmdGotoSafe,
                gotoRef = WsCmdGotoRef,
                gotoZ0 = WsCmdGotoZ0,
                probeZ = WsCmdProbeZ
            }

            // API paths stay in each side's constants. A wrong client path returns 404;
            // publishing another unused copy would add values that are never checked.
        };
    }

    private static object? GetFileStatus()
    {
        var file = AppState.CurrentFile;
        if (file == null)
        {
            return null;
        }

        // The machine's own count, which includes the lines probe adjustment added.
        int totalLines = _machine?.File.Count ?? file.Toolpath.Count;
        int currentLine = _machine?.FilePosition ?? 0;
        var bounds = GetCuttingBounds(file);

        return new
        {
            name = Path.GetFileName(file.FileName),
            path = file.FileName,
            totalLines,
            currentLine,
            progress = totalLines > 0 ? (double)currentLine / totalLines : 0,
            // The same bounds the cells are indexed on - see GetCuttingBounds.
            minX = bounds.MinX,
            maxX = bounds.MaxX,
            minY = bounds.MinY,
            maxY = bounds.MaxY
        };
    }

    /// <summary>
    /// The area the job cuts: the feed bounds when both axes have them, and the whole
    /// toolpath otherwise. The browser sizes its grid from these bounds and then draws cells
    /// the server indexed on them, so both must use one rule.
    /// </summary>
    private static (double MinX, double MaxX, double MinY, double MaxY) GetCuttingBounds(GCodeFile file)
    {
        bool useFeedBounds = file.SizeFeed.X > MillMinRangeThreshold
            && file.SizeFeed.Y > MillMinRangeThreshold;

        return useFeedBounds
            ? (file.MinFeed.X, file.MaxFeed.X, file.MinFeed.Y, file.MaxFeed.Y)
            : (file.Min.X, file.Max.X, file.Min.Y, file.Max.Y);
    }

    /// <summary>The cells the cutting path has reached, as "x,y" keys.</summary>
    /// <param name="maxWidth">Largest grid width, sized by the client to its screen.</param>
    /// <param name="maxHeight">Largest grid height, sized by the client to its screen.</param>
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

        var (minX, maxX, minY, maxY) = GetCuttingBounds(file);

        double rangeX = Math.Max(maxX - minX, MillMinRangeThreshold);
        double rangeY = Math.Max(maxY - minY, MillMinRangeThreshold);
        double aspectRatio = rangeX / rangeY;

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

        var cells = new HashSet<string>();
        foreach (var point in path)
        {
            int gridX = MapToGrid(point.X, minX, rangeX, gridWidth);
            int gridY = MapToGrid(point.Y, minY, rangeY, gridHeight);
            cells.Add($"{gridX},{gridY}");
        }

        return cells.ToArray();
    }

    private static int MapToGrid(double value, double min, double range, int gridSize)
    {
        if (range < MillMinRangeThreshold)
        {
            return 0;
        }
        int index = (int)Math.Floor((value - min) / range * (gridSize - 1));
        return Math.Max(0, Math.Min(gridSize - 1, index));
    }

    /// <summary>
    /// The browser sends a mode index and a direction; the distances and feeds come from this
    /// server's own JogModes. A browser that sent the numbers could put values of its choosing
    /// into the G-code.
    /// </summary>
    private static void HandleJogWithMode(string? axisStr, int direction, int modeIndex)
    {
        if (!MachineConnected)
        {
            return;
        }

        var axis = axisStr?.ToUpperInvariant();
        if (axis != "X" && axis != "Y" && axis != "Z")
        {
            Logger.Log($"Invalid jog axis: {axisStr}");
            return;
        }

        // A probe in contact would be dragged across the workpiece.
        if ((axis == "X" || axis == "Y") && _machine.PinStateProbe)
        {
            Logger.Log($"Blocked {axis} jog: probe in contact");
            return;
        }

        if (direction != -1 && direction != 1)
        {
            Logger.Log($"Invalid jog direction: {direction}");
            return;
        }

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

    /// <returns>The outcome, whose Refused is null once the work zero is set.</returns>
    private static WorkZeroResult HandleZero(ZeroRequest req)
    {
        Logger.Log($"HandleZero called: axes={string.Join(",", req.axes ?? Array.Empty<string>())}");

        if (!MachineConnected)
        {
            return new WorkZeroResult(ErrorMachineNotConnected, WorkZeroOutcome.NothingToDo);
        }

        // Re-datuming under a run would move the rest of the job relative to the part.
        if (MachineIsBeingDriven())
        {
            return new WorkZeroResult(ErrorMachineBusy, WorkZeroOutcome.NothingToDo);
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
            return new WorkZeroResult(ErrorInvalidRequest, WorkZeroOutcome.NothingToDo);
        }

        var axesStr = string.Join(" ", axesUpper.Select(a => $"{a}0"));
        Logger.Log($"HandleZero: axes={axesStr} workPos=({_machine.WorkPosition.X:F3},{_machine.WorkPosition.Y:F3},{_machine.WorkPosition.Z:F3}) machPos=({_machine.MachinePosition.X:F3},{_machine.MachinePosition.Y:F3},{_machine.MachinePosition.Z:F3})");

        // SetWorkZeroAndWait sets the offset, decides what it means for the height map, and
        // refuses an X or Y zero during a run.
        var zeroed = MachineCommands.SetWorkZeroAndWait(_machine, axesStr);
        if (zeroed.Refused != null)
        {
            return zeroed;
        }


        Logger.Log($"HandleZero: after zero workPos=({_machine.WorkPosition.X:F3},{_machine.WorkPosition.Y:F3},{_machine.WorkPosition.Z:F3})");

        // Retracted after a Z zero, as the terminal does.
        bool includesZ = axesUpper.Contains("Z");
        Logger.Log($"HandleZero: axesUpper={string.Join(",", axesUpper)}, includesZ={includesZ}");
        if (includesZ)
        {
            // Not awaited: the browser sees Z moving in the status broadcast.
            Logger.Log($"HandleZero: sending retract to Z={Constants.RetractZMm}");
            MachineCommands.MoveToSafeHeight(_machine, Constants.RetractZMm);
        }
        else
        {
            Logger.Log("HandleZero: no Z axis, skipping retract");
        }

        Logger.Log("HandleZero: done");
        return zeroed;
    }

    private static void ProbeZSingle()
    {
        // Register this probe as the current run so /api/probe/stop can stop it.
        var probeCts = new CancellationTokenSource();
        if (!TrySetCurrentProbeRun(probeCts))
        {
            probeCts.Dispose();
            BroadcastMessage(WsMessageTypeProbeError, new { message = ErrorMachineBusy });
            return;
        }

        // The current run is set before its task exists. AppState.Probe can throw while
        // constructing the controller, so construction belongs in this try block.
        try
        {
            var controller = AppState.Probe;

            controller.Options = ProbeOptions.FromSettings(AppState.Settings);

            PublishProbeTask(probeCts, Task.Run(async () =>
            {
                try
                {
                    var (success, _) = await controller.ProbeZSingleAsync(probeCts.Token);
                    if (!success)
                    {
                        BroadcastMessage(
                            WsMessageTypeProbeError,
                            new { message = ControllerConstants.ErrorProbeNoContact });
                    }
                }
                catch (OperationCanceledException)
                {
                    Logger.Log("ProbeZSingle: stopped");
                }
                catch (Exception ex)
                {
                    // A probe that never triggers and one the enclosure interrupts both
                    // time out. Reported as no contact, both said the tool reached full
                    // depth when it had not.
                    Logger.Log("ProbeZSingle: failed - {0}", ex);
                    BroadcastMessage(
                        WsMessageTypeProbeError,
                        new { message = ControllerConstants.ShowableMessage(ex) });
                }
                finally
                {
                    // This probe started no sleep prevention. Clear only
                    // its current-run entry.
                    ClearCurrentProbeRun(probeCts);
                }
            }));
        }
        catch (Exception ex)
        {
            Logger.Log("ProbeZSingle: could not start - {0}", ex);
            ClearCurrentProbeRun(probeCts);
            BroadcastMessage(
                WsMessageTypeProbeError,
                new { message = ControllerConstants.ShowableMessage(ex) });
        }
    }

    /// <summary>
    /// Refuses a path starting with two separators ("\\" or "/", in any mix) or a separator
    /// and "?", which Windows reads as a host or device path. On Unix this also refuses
    /// "//tmp", the same directory as "/tmp".
    /// </summary>
    internal static bool IsLocalPath(string path) =>
        !string.IsNullOrEmpty(path)
        && !(path.Length > 1
            && PathSeparators.Contains(path[0])
            && (PathSeparators.Contains(path[1]) || path[1] == DevicePathMarker));

    /// <summary>
    /// The absolute local path a request asked for, or null with the reason it was refused.
    /// The sequence every endpoint that loads or saves a path follows: expand the tilde,
    /// check it names this computer, then root it against <paramref name="baseDir"/>.
    /// </summary>
    internal static string? ResolveRequestPath(
        string? requested, string? baseDir, out string? refused)
    {
        refused = null;

        if (string.IsNullOrEmpty(requested))
        {
            refused = ErrorNoPathSpecified;
            return null;
        }

        string path = PathHelpers.ExpandTilde(requested);

        // Checked before rooting. A Windows UNC path is not rooted on Unix, so combining it
        // with the base directory first would bury the prefix and let it through.
        if (!IsLocalPath(path))
        {
            refused = ErrorPathNotOnThisComputer;
            return null;
        }

        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(
                string.IsNullOrEmpty(baseDir)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    : baseDir,
                path);
        }

        return path;
    }

    private static object GetFiles(string dirPath) =>
        GetFilesWithFilter(dirPath, ext => GCodeExtensions.Contains(ext));

    private static object GetFilesWithFilter(string dirPath, Func<string, bool> extensionFilter)
    {
        try
        {
            // A path on another host sends this thread to that host's file server. On
            // Windows Directory.Exists opens an outbound session and blocks for the mount
            // timeout.
            if (!IsLocalPath(dirPath) || !Directory.Exists(dirPath))
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

            var contentType = request.ContentType ?? "";
            if (!contentType.StartsWith("multipart/form-data"))
            {
                response.StatusCode = HttpStatusBadRequest;
                await WriteJson(response, new { error = ErrorExpectedMultipart });
                return;
            }

            var boundaryMatch = System.Text.RegularExpressions.Regex.Match(contentType, @"boundary=(.+)");
            if (!boundaryMatch.Success)
            {
                response.StatusCode = HttpStatusBadRequest;
                await WriteJson(response, new { error = ErrorMissingBoundary });
                return;
            }

            var boundary = "--" + boundaryMatch.Groups[1].Value.Trim('"');
            var content = Encoding.UTF8.GetString(body);

            var parts = content.Split(new[] { boundary }, StringSplitOptions.RemoveEmptyEntries);
            string? fileName = null;
            string? fileContent = null;

            foreach (var part in parts)
            {
                if (part.Trim() == "--") continue; // End boundary

                var filenameMatch = System.Text.RegularExpressions.Regex.Match(
                    part, @"filename=""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (filenameMatch.Success)
                {
                    fileName = filenameMatch.Groups[1].Value;

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

            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (!GCodeExtensions.Contains(ext))
            {
                response.StatusCode = HttpStatusBadRequest;
                await WriteJson(response, new { error = string.Format(ErrorInvalidFileType, ext) });
                return;
            }

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

            var baseName = Path.GetFileNameWithoutExtension(fileName);
            int counter = 1;
            while (File.Exists(savePath))
            {
                savePath = Path.Combine(uploadsDir, $"{baseName}_{counter}{ext}");
                counter++;
            }

            // Asked before the upload is written, so a refused upload leaves nothing behind.
            if (AppState.WhyTheFileCannotChange() is string uploadBlocked)
            {
                response.StatusCode = HttpStatusConflict;
                await WriteJson(response, new { error = uploadBlocked });
                return;
            }

            await File.WriteAllTextAsync(savePath, fileContent);

            var file = GCodeFile.Load(savePath);
            var loaded = AppState.LoadGCodeIntoMachine(file);
            if (loaded.Refused != null)
            {
                response.StatusCode = HttpStatusConflict;
                await WriteJson(response, new { error = loaded.Refused });
                return;
            }

            // LoadGCodeIntoMachine recorded which board is loaded; only the browse
            // directory is this caller's to set.
            AppState.Session.LastBrowseDirectory = uploadsDir;

            await WriteJson(response, FileSummary(file, loaded.MapDiscardedBecause));
        }
        catch (Exception ex)
        {
            await WriteFailure(response, "File upload", ex);
        }
    }

    private static async Task HandleLoadFile(HttpListenerResponse response, LoadFileRequest req)
    {
        // Asked before the path is resolved, so a refused load reads no directory.
        if (AppState.WhyTheFileCannotChange() is string loadBlocked)
        {
            response.StatusCode = HttpStatusConflict;
            await WriteJson(response, new { success = false, error = loadBlocked });
            return;
        }

        // Resolved first, so every later step works on the one path that was checked.
        string? path = ResolveRequestPath(req.path, AppState.Session.LastBrowseDirectory, out string? refusedPath);
        if (path == null)
        {
            response.StatusCode = HttpStatusBadRequest;
            await WriteJson(response, new { error = refusedPath });
            return;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!GCodeExtensions.Contains(ext))
        {
            response.StatusCode = HttpStatusBadRequest;
            await WriteJson(response, new { error = string.Format(ErrorInvalidFileType, ext) });
            return;
        }

        // File.Exists is false for a directory, so this rejects one too. A symlink to a
        // file passes.
        if (!File.Exists(path))
        {
            response.StatusCode = HttpStatusNotFound;
            await WriteJson(response, new { error = ErrorFileNotFound });
            return;
        }

        try
        {
            var file = GCodeFile.Load(path);
            var loaded = AppState.LoadGCodeIntoMachine(file);
            if (loaded.Refused != null)
            {
                response.StatusCode = HttpStatusConflict;
                await WriteJson(response, new { error = loaded.Refused });
                return;
            }

            AppState.Session.LastBrowseDirectory = Path.GetDirectoryName(path);

            await WriteJson(response, FileSummary(file, loaded.MapDiscardedBecause));
        }
        catch (Exception ex)
        {
            await WriteFailure(response, "File load", ex);
        }
    }

    /// <summary>
    /// Builds the loaded G-code summary for upload, load, and status responses so they
    /// return the same fields.
    /// </summary>
    private static object FileSummary(GCodeFile file, string? droppedMap = null) => new
    {
        success = true,
        droppedMap,
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
    /// has elapsed. Caller MUST hold <see cref="_clientsLock"/>.
    /// </summary>
    private static void PurgeExpiredPendingClients()
    {
        var expired = _pendingClients
            .Where(kvp => Environment.TickCount64 - kvp.Value > PendingClientTimeoutMs)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in expired)
        {
            _pendingClients.Remove(key);
        }
    }

    internal static string GetMillBlockerMessage(MillStartCheck result) => result.Error switch
    {
        MillBlocker.NotConnected => MillBlockedNotConnected,
        MillBlocker.NoFile => MillBlockedNoFile,
        MillBlocker.ProbeNotApplied => MillBlockedProbeNotApplied,
        MillBlocker.ProbeSetupChanged => MillBlockedProbeSetupChanged,
        MillBlocker.ProbeIncomplete => string.Format(MillBlockedProbeIncomplete, result.ProbeProgress),
        MillBlocker.AlarmState => MillBlockedAlarm,
        MillBlocker.Asleep => MillBlockedAsleep,
        _ => MillBlockedUnknown
    };

    private static object HandleMillCanStart()
    {
        var result = MenuHelpers.CheckMillCanStart();
        var warnings = new List<string>();
        var errors = new List<string>();

        if (result.Error != MillBlocker.None)
        {
            errors.Add(GetMillBlockerMessage(result));
        }

        foreach (var warning in result.Warnings)
        {
            switch (warning)
            {
                case MillWarning.NotHomed:
                    warnings.Add(MillWarningNotHomed);
                    break;
                case MillWarning.DangerousCommands:
                    if (result.DangerousWarnings != null)
                    {
                        warnings.AddRange(result.DangerousWarnings);
                    }
                    break;
                case MillWarning.NoMachineProfile:
                    warnings.Add(MillWarningNoProfile);
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

    /// <returns>Null once the run is under way, or the reason it was refused.</returns>
    private static async Task<string?> StartMilling()
    {
        var controller = AppState.Milling;

        // A second start would cancel the first run's token and clear the prompt the
        // operator is looking at.
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

        // The same check the TUI runs (MillMenu). /api/mill/can-start only reports it to
        // the browser, so enforcing it here stops a direct POST starting a job with an
        // incomplete or unapplied height map.
        var canStart = MenuHelpers.CheckMillCanStart();
        if (!canStart.CanStart)
        {
            Logger.Log("Mill start blocked: {0}", canStart.Error);
            return GetMillBlockerMessage(canStart);
        }

        // Clear the last run off the controller, whatever state it left behind.
        await controller.ReleaseAsync();

        _millCts?.Cancel();
        _millCts = new CancellationTokenSource();

        // A door must pause the run rather than be cleared automatically.
        if (_machine != null)
        {
            _machine.EnableAutoStateClear = false;
        }

        Action<ControllerState> onStateChanged = state =>
        {
            Logger.Log("Mill controller state: {0}", state);
            BroadcastMessage(WsMessageTypeMillState, new { state = state.ToString() });
        };
        // The controller reports progress at 10 Hz. Send phase changes immediately and
        // limit other socket updates to the interval.
        long lastProgressBroadcast = 0;
        string? lastProgressPhase = null;
        Action<ProgressInfo> onProgressChanged = progress =>
        {
            long now = Environment.TickCount64;
            bool phaseChanged = progress.Phase != lastProgressPhase;
            if (!phaseChanged && now - lastProgressBroadcast < WebConstants.WebSocketBroadcastIntervalMs)
            {
                return;
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

            // Informational: the browser can show that a tool change is starting.
            BroadcastMessage(WsMessageTypeMillToolChange, new
            {
                toolNumber = info.ToolNumber,
                toolName = info.ToolName,
                lineNumber = info.LineNumber
            });

            // Assign toolChangeCts before scheduling the task, then compare it before
            // clearing the shared field so cleanup cannot clear a later run's handle.
            // The server starts this run without another browser request and tracks it in
            // _toolChangeRunTask for HandleMillStopAsync to await.
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
        // Milling prompts (M0/M1) and tool-change prompts use the same PendingPrompt entry.
        // Keep this request so teardown clears it only if it still belongs to this run.
        UserInputRequest? published = null;
        Action<UserInputRequest> onUserInputRequired = request =>
        {
            Logger.Log("Mill controller user input required: {0}", request.Message);

            // Wrap OnResponse so the dialog closes once answered, unless the run publishes
            // a new prompt from inside that call, which a milling run does when the
            // enclosure was opened.
            published = PublishPrompt(request);
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

        // The web UI has no per-start depth confirmation, as the terminal does; the check
        // above is what protects a start from a browser. Its pre-mill modal does ask the
        // enclosure question, but it asks it in the browser and this endpoint never receives
        // the answer, so a door hold goes to the operator as a prompt here rather than being
        // released on an answer the server is only assuming.
        controller.Options = MillingOptions.Create(AppState.CurrentFile?.FileName,
            AppState.DepthAdjustment, _machine!.IsHomed, enclosureConfirmed: false);

        Logger.Log("Starting milling controller: RequireHoming={0}, DepthAdjustment={1:F3}",
            controller.Options.RequireHoming, controller.Options.DepthAdjustment);

        SleepPrevention.Start();
        Logger.Log("Sleep prevention started: {0}", SleepPrevention.IsActive);

        // Capture millCts before scheduling the task, then compare it in finally so an
        // older run cannot clear a newer run's fields. _millRunTask lets Stop await this
        // run's teardown before using the controller again (see HandleMillStopAsync).
        var millCts = _millCts;
        _millRunTask = Task.Run(async () =>
        {
            try
            {
                await controller.StartAsync(millCts.Token);
            }
            catch (Exception ex)
            {
                // Only a stop awaits this task, and it swallows faults so its own teardown
                // runs. Without this the run would fail with nothing logged.
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

                // These fields may now belong to a newer run. Clearing its prompt would
                // hide the operator's question; enabling automatic door release during a
                // cut would let software resume the machine.
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
                }
                else
                {
                    Logger.Log("Milling controller finished; a newer run is active");
                }
            }
        });

        Logger.Log("Milling started (controller-based)");
        return null;
    }

    /// <summary>
    /// Stop tool change and milling under <see cref="_toolChangeAbortLock"/> so a milling
    /// reset cannot clear a tool-change alarm. A tool-change task must call
    /// <see cref="StopMillingAsync"/> instead, because this method would await that task.
    /// </summary>
    /// <returns>False if a lock, or a run's cancellation-driven unwind, did not complete
    /// within <see cref="ControllerCancelTimeoutMs"/> - the caller must not tell the
    /// operator the machine has stopped.</returns>
    private static async Task<bool> HandleMillStopAsync()
    {
        var stopElapsed = Stopwatch.StartNew();

        bool acquiredAbortLock = await _toolChangeAbortLock.WaitAsync(RemainingStopTimeoutMs(stopElapsed));
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
                || await AwaitRunTeardownAsync(toolChangeRunTask, "Tool change", stopElapsed);

            bool millStopped = await StopMillingAsync(stopElapsed);

            Logger.Log("Mill stop complete");
            return toolChangeStopped && millStopped;
        }
        finally
        {
            _toolChangeAbortLock.Release();
        }
    }

    /// <summary>
    /// Stop milling after a tool change fails or an operator requests Stop.
    /// <see cref="_millStopLock"/> prevents a second reset while StartAsync handles
    /// cancellation, cleanup, and its terminal-state transition.
    /// </summary>
    /// <returns>False if the lock wait or run cleanup exceeded
    /// <see cref="ControllerCancelTimeoutMs"/>.</returns>
    private static async Task<bool> StopMillingAsync(Stopwatch? stopElapsed = null)
    {
        stopElapsed ??= Stopwatch.StartNew();

        if (_machine == null)
        {
            return true;
        }

        bool acquiredStopLock = await _millStopLock.WaitAsync(RemainingStopTimeoutMs(stopElapsed));
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
                // Wait for StartAsync to clean up and change state after cancellation.
                stopped = await AwaitRunTeardownAsync(runTask, "Mill", stopElapsed);
            }

            // A timed-out run may still be stopping the machine and retracting.
            // Reset only after that work finishes.
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
    /// Return the remaining stop timeout after waiting for locks and run cleanup.
    /// </summary>
    private static int RemainingStopTimeoutMs(Stopwatch elapsed)
    {
        long left = ControllerCancelTimeoutMs - elapsed.ElapsedMilliseconds;
        return left > 0 ? (int)left : 0;
    }

    /// <summary>
    /// Wait for a controller's run task after cancellation, up to the remaining stop
    /// timeout. Log task failures while allowing the stop sequence to continue.
    /// </summary>
    /// <returns>True if the run task finished within <see cref="ControllerCancelTimeoutMs"/>.</returns>
    private static async Task<bool> AwaitRunTeardownAsync(Task runTask, string label, Stopwatch? stopElapsed = null)
    {
        stopElapsed ??= Stopwatch.StartNew();
        int remaining = RemainingStopTimeoutMs(stopElapsed);

        var completed = await Task.WhenAny(runTask, Task.Delay(remaining));
        if (completed != runTask)
        {
            Logger.Log("{0}: run task did not finish within the remaining {1}ms stop timeout", label, remaining);
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
        // Setup replaces the grid and autosave. SetupProbeGrid rejects active runs;
        // check homing here because SetupProbeGrid does not check it.
        if (_machine?.IsHoming ?? false)
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
            // Clamped: the browser cannot set arbitrary values.
            var margin = Math.Clamp(req.margin ?? DefaultProbeMargin, MinProbeMargin, MaxProbeMargin);
            var gridSize = Math.Clamp(req.gridSize ?? DefaultProbeGridSize, MinProbeGridSize, MaxProbeGridSize);

            var (grid, refused) = AppState.SetupProbeGrid(
                new Vector2(file.Min.X, file.Min.Y),
                new Vector2(file.Max.X, file.Max.Y),
                margin,
                gridSize);

            if (grid == null)
            {
                response.StatusCode = HttpStatusConflict;
                await WriteJson(response, new { success = false, error = refused });
                return;
            }

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

    /// <summary>Starts an outline trace, or returns why it will not. See <see cref="StartMilling"/>.</summary>
    private static string? StartProbeTraceOutline()
    {
        if (!TryGetProbeGridForRun(out var grid, out string? refusal))
        {
            return refusal;
        }

        // Set the current run before scheduling its task so cleanup sees the same token.
        var traceCts = new CancellationTokenSource();
        if (!TrySetCurrentProbeRun(traceCts))
        {
            traceCts.Dispose();
            return ErrorMachineBusy;
        }

        PublishProbeTask(traceCts, Task.Run(() => TraceOutlineAsync(grid, traceCts)));
        return null;
    }

    /// <summary>Protects the current probe token and task from concurrent requests.</summary>
    private static readonly object ProbeRunLock = new();

    /// <summary>
    /// The probe run's task, read under the lock that publishes and clears it.
    /// </summary>
    private static Task? GetCurrentProbeTask() => GetCurrentProbeRun().Task;

    /// <summary>
    /// The current probe run's token and task, read together. Read apart, a run that ends
    /// between the two leaves a caller cancelling one run and waiting on another.
    /// </summary>
    private static (CancellationTokenSource? Cts, Task? Task) GetCurrentProbeRun()
    {
        lock (ProbeRunLock)
        {
            return (_probeCts, _probeTask);
        }
    }

    /// <summary>Clears the current probe token and task between concurrency tests.</summary>
    internal static void ClearCurrentProbeRunForTest()
    {
        lock (ProbeRunLock)
        {
            _probeCts = null;
            _probeTask = null;
        }
    }

    /// <summary>
    /// Store the probe token only when no probe token is set. The check and assignment share
    /// a lock so concurrent requests cannot both start runs on the same controller.
    /// </summary>
    internal static bool TrySetCurrentProbeRun(CancellationTokenSource cts)
    {
        lock (ProbeRunLock)
        {
            if (_probeCts != null)
            {
                Logger.Log("Probe start refused: another probe run is active");
                return false;
            }

            _probeCts = cts;
            return true;
        }
    }

    /// <summary>
    /// Store the task only if its token is still current. The run may finish before Task.Run
    /// returns, so an unconditional assignment could restore a task after cleanup cleared it.
    /// </summary>
    private static void PublishProbeTask(CancellationTokenSource cts, Task task)
    {
        lock (ProbeRunLock)
        {
            if (ReferenceEquals(_probeCts, cts))
            {
                _probeTask = task;
            }
        }
    }

    /// <summary>
    /// Clear the current probe token and task only if the token matches. A completed run
    /// must not clear a newer run's task, which the stop endpoint needs.
    /// </summary>
    /// <returns>False when the token does not match and nothing was cleared.</returns>
    internal static bool ClearCurrentProbeRun(CancellationTokenSource cts)
    {
        lock (ProbeRunLock)
        {
            if (!ReferenceEquals(_probeCts, cts))
            {
                Logger.Log("Probe run finished; a newer probe run is active");
                return false;
            }

            _probeCts = null;
            _probeTask = null;
            return true;
        }
    }

    /// <summary>Clears the current probe run and releases its controller.</summary>
    private static async Task ReleaseProbeRunAsync(CancellationTokenSource cts)
    {
        if (!ClearCurrentProbeRun(cts))
        {
            return;
        }

        // Release the controller after this run ends so its state returns to Idle.
        // Log failures here because this runs from the task's finally block.
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
    }

    /// <summary>
    /// Get the grid for a probe run or return the reason the run cannot start.
    /// A grid probe and outline trace share one controller.
    /// </summary>
    private static bool TryGetProbeGridForRun(
        [NotNullWhen(true)] out ProbeGrid? grid, out string? refusal)
    {
        grid = null;

        // The same check the terminal runs: grid coordinates are work coordinates, so
        // probing from an unset origin drives the tool to arbitrary XY.
        refusal = MenuHelpers.GetProbeDisabledReason();
        if (refusal != null)
        {
            return false;
        }

        if (AppState.Probe.IsRunInProgress)
        {
            Logger.Log("Probe start refused: controller is {0}, probe task {1}",
                AppState.Probe.State, GetCurrentProbeTask() == null ? "absent" : "running");
            refusal = CliConstants.ProbeErrorAlreadyRunning;
            return false;
        }

        // Preserve the reason for failing to load a saved map; it differs from no map.
        refusal = AppState.EnsureProbeDataLoaded();
        if (refusal != null)
        {
            return false;
        }

        grid = AppState.ProbePoints;
        refusal = grid == null ? ErrorNoProbeGrid : null;
        return grid != null;
    }

    private static async Task TraceOutlineAsync(ProbeGrid grid, CancellationTokenSource cts)
    {
        var settings = AppState.Settings;
        var controller = AppState.Probe;

        // The trace prompts about the enclosure like any other run, and a prompt with no
        // subscriber throws rather than waiting (ControllerBase.RequestUserInputAsync).
        UserInputRequest? published = null;
        Action<UserInputRequest> onUserInputRequired = request => published = PublishPrompt(request);

        // Setup is inside the try so the finally clears the current run if setup throws.
        // ReleaseAsync may throw while stopping an unfinished run.
        try
        {
            // Start from Idle, whatever the last run left behind.
            await controller.ReleaseAsync();

            Logger.Log($"TraceOutline: tracing outline for {grid.SizeX}x{grid.SizeY} grid, " +
                $"traceHeight={settings.OutlineTraceHeight:F3}, traceFeed={settings.OutlineTraceFeed:F0}");

            controller.LoadGrid(grid);
            controller.Options = ProbeOptions.FromSettings(settings, traceOutline: true);

            controller.ErrorOccurred += OnProbeError;
            controller.UserInputRequired += onUserInputRequired;

            // The tool moves for the whole trace, so the door must pause it rather than be
            // cleared automatically, and the computer must stay awake.
            if (_machine != null)
            {
                _machine.EnableAutoStateClear = false;
            }
            SleepPrevention.Start();

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
            controller.UserInputRequired -= onUserInputRequired;
            PendingPrompt.ClearIfCurrent(published);
            await ReleaseProbeRunAsync(cts);
        }
    }

    /// <summary>Starts a grid probe, or returns why it will not. See <see cref="StartMilling"/>.</summary>
    private static async Task<string?> StartProbing()
    {
        if (!TryGetProbeGridForRun(out var grid, out string? refusal))
        {
            return refusal;
        }

        // Claimed before the first await, so a second request cannot pass the check above
        // while this one is still setting up.
        var probeCts = new CancellationTokenSource();
        if (!TrySetCurrentProbeRun(probeCts))
        {
            probeCts.Dispose();
            return ErrorMachineBusy;
        }

        var controller = AppState.Probe;

        // Probe, mill, and tool-change runs share one pending prompt. Only one can wait
        // for an answer at a time.
        UserInputRequest? published = null;
        Action<UserInputRequest> onUserInputRequired = request =>
        {
            Logger.Log("Probe controller user input required: {0}", request.Message);

            published = PublishPrompt(request);
        };

        void Unsubscribe()
        {
            controller.PointCompleted -= OnProbePointCompleted;
            controller.ErrorOccurred -= OnProbeError;
            controller.UserInputRequired -= onUserInputRequired;
        }

        try
        {
            await controller.ReleaseAsync();

            Logger.Log($"StartProbing: starting grid probe {grid.SizeX}x{grid.SizeY} = {grid.TotalPoints} points");

            // The web grid probe uses the same settings mapping as the terminal.
            controller.Options = ProbeOptions.FromSettings(AppState.Settings, traceOutline: false);

            // The same object AppState holds, updated in place.
            controller.LoadGrid(grid);

            controller.PointCompleted += OnProbePointCompleted;
            controller.ErrorOccurred += OnProbeError;
            controller.UserInputRequired += onUserInputRequired;

            if (_machine != null)
            {
                _machine.EnableAutoStateClear = false;
            }

            // A grid probe runs for tens of minutes with the probe down, and a suspend would
            // drop the link. The TUI does the same.
            SleepPrevention.Start();

            PublishProbeTask(probeCts, Task.Run(async () =>
            {
                try
                {
                    await controller.StartAsync(probeCts.Token);

                        // The autosave already holds the data, so completion needs nothing here.
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
                    Unsubscribe();

                    // Keep a prompt published by a newer run.
                    bool wasOurs = published != null && ReferenceEquals(PendingPrompt.Current, published);
                    PendingPrompt.ClearIfCurrent(published);
                    if (wasOurs)
                    {
                        BroadcastMessage(WsMessageTypeToolChangeComplete, new { success = false });
                    }

                    await ReleaseProbeRunAsync(probeCts);
                }
            }));
        }
        catch
        {
            // The run task's finally is the only other release, and setup failed before there
            // was a run task to reach it.
            Unsubscribe();
            await ReleaseProbeRunAsync(probeCts);
            throw;
        }

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
    /// Refuse a probe stop when no probe task exists and another run is active.
    /// Resetting GRBL here would abort milling and clear an alarm before milling handles it.
    /// </summary>
    private static string? ProbeStopBlocker() =>
        GetCurrentProbeTask() == null
            && (AppState.Milling.IsRunInProgress || AppState.ToolChange.IsRunInProgress)
            ? ErrorMachineBusy
            : null;

    /// <summary>
    /// Cancel the probe and wait for ProbeController.CleanupAsync to stop and retract.
    /// Return a Task so exceptions can be observed by the caller.
    /// </summary>
    /// <returns>False if cleanup timed out and the machine may still be moving.</returns>
    private static async Task<bool> HandleProbeStop()
    {
        var (probeCts, runTask) = GetCurrentProbeRun();
        probeCts?.Cancel();

        if (runTask == null)
        {
            // Clear a token whose run task never started.
            if (probeCts != null)
            {
                ClearCurrentProbeRun(probeCts);
            }

            // No run in progress, so nothing else will stop the machine.
            if (_machine != null)
            {
                await MachineWait.StopAndResetAsync(_machine);
            }

            // Release a controller run even if the server has no task for it.
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

        // Do not reset while timed-out cleanup may still be retracting the tool.
        return await AwaitRunTeardownAsync(runTask, "Probe");
    }

    private static object GetProbeStatus()
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
            colors = GetProbeColorsArray(grid),
            phase = controller.Phase.ToString(),
            suggestedFileName = Persistence.SuggestedProbeFileName(),
            state
        };
    }

    /// <summary>
    /// The values every probe-status response derives from the current grid, computed once
    /// so the brief and full status builders can never disagree on state or unsaved-data.
    /// </summary>
    private static (ProbeGrid? grid, string state, bool hasUnsavedData) ReadProbeStateSnapshot()
    {
        // The autosave is used when nothing is loaded, and is reported without being adopted -
        // see rule no-side-effect-on-get.
        var (grid, usableAutosave) = AppState.ReadProbeGridAndAutosave();
        return (grid, ComputeProbeState(grid), usableAutosave != null);
    }

    /// <summary>The wire name for the state Core computed. The words are this layer's.</summary>
    private static string ComputeProbeState(ProbeGrid? grid) => ProbeGrid.StateOf(grid) switch
    {
        ProbeDataState.Complete => ProbeStateComplete,
        ProbeDataState.Partial => ProbeStatePartial,
        ProbeDataState.Ready => ProbeStateReady,
        _ => ProbeStateNone
    };

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
    /// The color for each measured node, as CSS. Computed here rather than in the browser
    /// so both views use one calculation.
    ///
    /// Null where a node has no height, and null throughout until something is measured,
    /// because the range needs at least one reading.
    /// </summary>
    private static string?[][] GetProbeColorsArray(ProbeGrid grid)
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
                var (r, g, b) = HeightGradient.Color(fraction);
                result[x][y] = $"rgb({r}, {g}, {b})";
            }
        }
        return result;
    }

    private static async Task HandleWebSocket(HttpListenerContext context)
    {
        ClientConnection? client = null;
        string? clientId = null;

        var query = context.Request.QueryString;
        clientId = query[QueryParamClientId];

        bool hasOtherClient = false;
        lock (_clientsLock)
        {
            PurgeExpiredPendingClients();

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
                // Reserved here, so a second WebSocket request does not pass the same check.
                _pendingClients[clientId] = Environment.TickCount64;
            }
        }

        // A terminal has the machine, or a takeover is waiting for one. Not the proxy still
        // closing the port after a browser took the machine back, or the page that did so
        // would be offered it again when it reloads.
        if ((_hold?.IsYieldedToTerminal ?? false) || (_proxy?.HasClient ?? false))
        {
            hasOtherClient = true;
            Logger.Log("WebSocket: a terminal has the machine");
        }

        try
        {
            var wsContext = await context.AcceptWebSocketAsync(null);
            var webSocket = wsContext.WebSocket;
            client = new ClientConnection
            {
                Socket = webSocket,
                Id = clientId,
                LastActivityMs = Environment.TickCount64
            };

            var clientAddress = context.Request.RemoteEndPoint?.Address?.ToString();

            lock (_clientsLock)
            {
                if (clientId != null)
                {
                    int stale = _clients.RemoveAll(
                        c => IsSupersededClient(c.Id, c.Socket.State, clientId));
                    if (stale > 0)
                    {
                        Logger.Log("Removed stale WebSocket for client {0}", clientId);
                    }
                    _pendingClients.Remove(clientId);
                }
                _clients.Add(client);
                _webClientAddress = clientAddress;
            }

            Logger.Log("WebSocket client connected (clientId={0}, address={1})", clientId ?? "none", clientAddress ?? "unknown");

            if (hasOtherClient)
            {
                Logger.Log("WebSocket: another client connected, sending connection error");
                var errorJson = JsonSerializer.Serialize(new
                {
                    type = WsMessageTypeConnectionError,
                    data = new { error = ProxyConnectionRejected, otherClientConnected = true }
                });
                await SendToClientAsync(client, Encoding.UTF8.GetBytes(errorJson));
                // Left open, so the browser can show the take-over modal and reload after it.
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
                    client.LastActivityMs = Environment.TickCount64;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // Answered, because a client that completes the close handshake waits for
                    // the reply.
                    await CloseClientAsync(client, null);
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
                lock (_clientsLock)
                {
                    _clients.Remove(client);
                    if (_clients.Count == 0)
                    {
                        _webClientAddress = null;
                    }
                }
                Logger.Log("WebSocket client disconnected");
            }
            else
            {
                // The upgrade never completed, and HandleRequest has already skipped
                // closing the connection. Left open, each retry leaks one.
                context.Response.Abort();
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

            // Use the same direct-command table as the HTTP endpoints.
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

            // Jog uses a payload and needs no reply. Zeroing can be refused, so the jog
            // screen sends it over HTTP to receive the reason.
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
    /// absent or not a number. A malformed field must not throw out of the receive loop,
    /// which would drop the connection.
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
    /// An array of strings, or null when the field is absent or not an array. Non-string
    /// elements are dropped, and every caller checks what it accepts anyway.
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
    /// Closes the socket, or answers the client's own close, taking the one send slot a
    /// close needs. A client that does not answer within <see cref="WebSocketCloseTimeoutMs"/>,
    /// or a socket that cannot be closed, is aborted.
    /// </summary>
    private static async Task CloseClientAsync(ClientConnection client, string? reason)
    {
        using var timeout = new CancellationTokenSource(WebSocketCloseTimeoutMs);
        try
        {
            await client.SendLock.WaitAsync(timeout.Token);
            try
            {
                if (client.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await client.Socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure, reason, timeout.Token);
                }
            }
            finally
            {
                client.SendLock.Release();
            }
        }
        catch (Exception ex)
        {
            Logger.Log("Closing a WebSocket failed, aborting it: {0}", ex.Message);
            try
            {
                client.Socket.Abort();
            }
            catch (Exception abortEx)
            {
                Logger.Log("Aborting a WebSocket failed: {0}", abortEx.Message);
            }
        }
    }

    /// <summary>
    /// Send one frame to one client. A WebSocket accepts one send at a time, so each
    /// client's sends queue behind its own lock. <paramref name="dropIfBusy"/> drops a
    /// message that will be superseded shortly rather than queueing it.
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
    /// Send a message to every connected client. The only place a WebSocket message leaves
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
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(WebConstants.WebSocketBroadcastIntervalMs, ct);

                List<ClientConnection> staleClients;
                lock (_clientsLock)
                {
                    long now = Environment.TickCount64;
                    staleClients = _clients
                        .Where(c => now - c.LastActivityMs > WebSocketTimeoutMs)
                        .ToList();
                    _clients.RemoveAll(staleClients.Contains);
                }

                foreach (var stale in staleClients)
                {
                    Logger.Log("Closing stale WebSocket client (silent for {0}ms)", WebSocketTimeoutMs);
                    await CloseClientAsync(stale, WsCloseReasonTimeout);
                }

                // Dropped rather than queued for a client that has stopped reading: the
                // next snapshot is along shortly and is more current.
                BroadcastMessage(WsMessageTypeStatus, GetStatus(), dropIfBusy: true);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // The browser takes its screen lock from this stream, so keep it running
                // and skip a snapshot that failed to build.
                Logger.Log("Status broadcast failed: {0}", ex);
            }
        }
    }

    private static async Task ServeStaticFile(HttpListenerContext context, string path)
    {
        var response = context.Response;

        if (path == "/")
        {
            path = "/index.html";
        }

        bool willServeIndexHtml = path == "/index.html";

        var resourcePath = "coppercli.WebServer.wwwroot" + path.Replace('/', '.');

        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourcePath);

        // A path with no extension and no resource of its own falls back to index.html.
        if (stream == null && path.IndexOf('.') < 0)
        {
            willServeIndexHtml = true;
        }

        if (willServeIndexHtml)
        {
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

            if (requestClientId == null)
            {
                requestClientId = Guid.NewGuid().ToString("N");
            }

            // Deliberately no pending-client reservation here: serving a page is a GET, and a
            // GET arrives from anywhere a browser can be pointed - an <img> on another site
            // reaches this line with no Origin to check. Reserving a slot per page fetch let
            // such a page fill the single client slot from a distance, so the operator's own UI
            // found the machine "already connected" and offered a force-disconnect mid-job;
            // HandleWebSocket reserves instead, because the upgrade always carries an Origin.

            response.SetCookie(new Cookie(ClientIdCookieName, requestClientId)
            {
                Path = "/",
                HttpOnly = false,  // JavaScript needs to read it for WebSocket
            });
        }

        if (stream == null)
        {
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

    /// <summary>Answers a Stop or Abort request.</summary>
    /// <param name="stopped">False when the stop could not confirm the controllers finished
    /// tearing down in time, so the operator must not be told the machine has stopped.</param>
    /// <param name="refusal">Set when the stop was not attempted and the machine is as it
    /// was.</param>
    private static async Task WriteStopResult(
        HttpListenerResponse response, bool stopped, string? refusal = null)
    {
        if (stopped)
        {
            await WriteJson(response, new { success = true });
            return;
        }

        response.StatusCode = refusal == null ? HttpStatusServerError : HttpStatusConflict;
        await WriteJson(response, new { error = refusal ?? CliConstants.StopTimedOutWarning });
    }

    /// <summary>
    /// Answer a request that failed on something internal. The exception goes to the log
    /// and a plain message to the screen.
    /// </summary>
    private static async Task WriteFailure(HttpListenerResponse response, string what, Exception ex)
    {
        Logger.Log("{0} failed: {1}", what, ex);
        response.StatusCode = HttpStatusServerError;
        await WriteJson(response, new { error = ErrorServerFailure });
    }

    /// <summary>Writes plain text, for a response read directly rather than by the UI.</summary>
    private static async Task WriteText(HttpListenerResponse response, string text)
    {
        response.ContentType = ContentTypeText;
        var bytes = Encoding.UTF8.GetBytes(text);
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    /// <summary>
    /// Read a request body into memory, up to <paramref name="maxBytes"/>. Content-Length
    /// is not trusted, so the read is capped as it goes.
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
    /// Read and parse a JSON request body, answering the request itself when the body is
    /// too large, invalid or missing. A null result means the request was already answered.
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

        /// <summary>The operator agreed to replace the file at <see cref="path"/>.</summary>
        public bool overwrite { get; init; }
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

    /// <summary>An answer names the question it answers by its topic and the detail shown.</summary>
    private record SessionRestoreAnswerRequest
    {
        public string? topic { get; init; }
        public string? detail { get; init; }
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
        // The same map the status reported. Saving moves the autosave, so nothing has to be
        // in memory.
        var probePoints = AppState.CurrentProbeGrid;

        if (probePoints == null || !probePoints.HasCompleteData)
        {
            response.StatusCode = HttpStatusBadRequest;
            await WriteJson(response, new { error = ErrorNoCompleteProbeData });
            return;
        }

        try
        {
            // The terminal's rule, so one name is not saved as two different files.
            // ResolveRequestPath refuses an empty path, and EnsureExtension leaves one alone.
            string named = PathHelpers.EnsureExtension(req.path ?? string.Empty, ProbeGridExtensions);

            string? path = ResolveRequestPath(
                named, AppState.Session.LastProbeBrowseDirectory, out string? refusedPath);
            if (path == null)
            {
                response.StatusCode = HttpStatusBadRequest;
                await WriteJson(response, new { error = refusedPath });
                return;
            }

            // Refused so the operator is asked first, as the terminal does: the error text is the
            // question, and the browser sends the save again with overwrite set once the
            // operator agrees.
            if (File.Exists(path) && !req.overwrite)
            {
                response.StatusCode = HttpStatusConflict;
                await WriteJson(response, new
                {
                    error = string.Format(ProbeFormatOverwrite, Path.GetFileName(path)),
                    fileExists = true
                });
                return;
            }

            if (!Persistence.SaveProbeToFile(path))
            {
                response.StatusCode = HttpStatusServerError;
                await WriteJson(response, new { error = ErrorProbeSaveFailed });
                return;
            }

            // The probe browser keeps its own directory, apart from the G-code one.
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
        try
        {
            string? path = ResolveRequestPath(
                req.path, AppState.Session.LastProbeBrowseDirectory, out string? refusedPath);
            if (path == null)
            {
                response.StatusCode = HttpStatusBadRequest;
                await WriteJson(response, new { error = refusedPath });
                return;
            }

            if (!File.Exists(path))
            {
                response.StatusCode = HttpStatusNotFound;
                await WriteJson(response, new { error = ErrorFileNotFound });
                return;
            }

            // The one place a probe grid is loaded (it reloads the original G-code first if
            // a grid was already applied, so this grid is not applied on top of the old one).
            var (grid, refused) = AppState.LoadProbeGridFromFile(path);
            if (grid == null)
            {
                response.StatusCode = HttpStatusConflict;
                await WriteJson(response, new { success = false, error = refused });
                return;
            }

            bool complete = grid.HasCompleteData;
            string? notApplied = complete ? AppState.ApplyProbeData() : null;

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
                applied = AppState.AreProbePointsApplied,

                // Loaded either way; this reports whether it also went into the G-code.
                error = notApplied
            });
        }
        catch (Exception ex)
        {
            await WriteFailure(response, "Probe load", ex);
        }
    }

    private static object GetProbeFiles(string dirPath) =>
        GetFilesWithFilter(dirPath, ext => ProbeGridExtensions.Contains(ext));

    /// <summary>
    /// Closes every WebSocket, so the pages behind them offer the takeover again when they
    /// reconnect. Returns how many were closed.
    /// </summary>
    private static async Task<int> CloseAllClientsAsync()
    {
        List<ClientConnection> clientsToClose;
        lock (_clientsLock)
        {
            clientsToClose = _clients.ToList();
            _pendingClients.Clear();
            _clients.Clear();
            _webClientAddress = null;
        }

        await Task.WhenAll(clientsToClose.Select(
            client => CloseClientAsync(client, WsCloseReasonForceDisconnect)));
        return clientsToClose.Count;
    }

    /// <summary>
    /// A browser takes the machine over: every other page and any terminal on the proxy is
    /// disconnected. The machine stays connected, or reconnects if the server had let go of it
    /// for a terminal.
    /// </summary>
    private static async Task HandleBrowserTakeover(HttpListenerContext context)
    {
        // Reclaimed first, so no terminal can claim the port between the two.
        _hold?.Reclaim();
        int closed = await CloseAllClientsAsync();
        if (_proxy?.ForceDisconnectClient() == true)
        {
            Logger.Log("Browser takeover: disconnected the terminal on the proxy");
        }
        Logger.Log("Browser takeover: closed {0} page(s)", closed);

        await WriteJson(context.Response, new { success = true, disconnected = closed });
    }

    /// <summary>
    /// A terminal on another computer takes the machine over: the server disconnects so the
    /// proxy can open the serial port. Refused while <see cref="AnyOperationRunning"/>,
    /// because the disconnect would stop it partway.
    /// </summary>
    private static async Task HandleTerminalTakeover(HttpListenerContext context)
    {
        if (_hold == null || !_hold.TryYieldToTerminal(AnyOperationRunning))
        {
            Logger.Log("Terminal takeover refused: the machine is in use");
            context.Response.StatusCode = HttpStatusConflict;
            await WriteJson(context.Response, new { error = ErrorTakeoverWhileBusy });
            return;
        }

        int closed = await CloseAllClientsAsync();
        Logger.Log("Terminal takeover: closed {0} page(s)", closed);

        await WriteJson(context.Response, new { success = true, disconnected = closed });
    }

    /// <summary>
    /// Deletes the saved height map, drops the map in memory, and puts the original G-code
    /// back.
    /// </summary>
    /// <returns>The reason nothing was cleared, or null once it was.</returns>
    private static string? HandleProbeDiscard()
    {
        string? notCleared = AppState.DiscardProbeDataAndAutosave();
        Logger.Log("HandleProbeDiscard: {0}", notCleared ?? "cleared");
        return notCleared;
    }

    /// <summary>
    /// Starts the tool-change controller when M6 is detected. The browser only watches; the
    /// controller's State and Phase are what drive it.
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

        var settings = AppState.Settings;
        var currentFile = AppState.CurrentFile;
        toolChangeController.Options = ToolChangeOptions.FromSettings(settings, currentFile);

        Action<ControllerState> onStateChanged = state =>
        {
            Logger.Log("Tool change state: {0}", state);
            BroadcastMessage(WsMessageTypeToolChangeState, new { state = state.ToString() });
        };
        // Send phase changes immediately and limit other socket updates to the interval.
        long lastToolChangeProgressBroadcast = 0;
        string? lastToolChangePhase = null;
        Action<ProgressInfo> onProgressChanged = progress =>
        {
            long now = Environment.TickCount64;
            bool phaseChanged = progress.Phase != lastToolChangePhase;
            if (!phaseChanged && now - lastToolChangeProgressBroadcast < WebConstants.WebSocketBroadcastIntervalMs)
            {
                return;
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
        // As in HandleMillStart: this run clears its own prompt, not one the milling run
        // published since.
        UserInputRequest? published = null;
        Action<UserInputRequest> onUserInputRequired = request =>
        {
            Logger.Log("Tool change user input required: {0}", request.Message);
            published = PublishPrompt(request);
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
                // Abort and failure differ only in what the operator is told; the milling run
                // this tool change interrupted is torn down the same way either way. Only the
                // milling side is torn down here, never the combined stop path: an
                // operator-initiated Stop already cancelled this run's token and is awaiting
                // this task, so calling back into that path would await this task from inside
                // its own execution (see HandleMillStopAsync's remarks).
                bool wasAborted = toolChangeController.State == ControllerState.Cancelled;
                Logger.Log("Tool change {0}", wasAborted ? "aborted" : "failed");
                BroadcastMessage(WsMessageTypeToolChangeComplete, new { success = false, aborted = wasAborted });
                await StopMillingAsync();
            }
        }
        finally
        {
            // However it ended - success, abort, failure, or an exception from Resume() above -
            // the tool change is over, so the controller goes back to Idle and DetectToolChange
            // stops reporting one. The only place that releases it, because HandleMillStopAsync
            // awaits this task instead; a failure is logged rather than thrown, so the
            // unsubscribes below still run.
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
    /// Answers whichever prompt is pending: the tool-change controller's own, or the milling
    /// controller's M0/M1, both posted to the tool-change dialog endpoint. See
    /// <see cref="PendingPrompt"/> for why the answer has to name the prompt it answers.
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
    /// An operator-initiated stop that arrives through the tool-change dialog's Abort button
    /// rather than the main Stop button; both tear down the same two controllers the same way,
    /// so this is a thin caller of the shared stop path. See
    /// <see cref="HandleMillStopAsync"/> for the lock order and timeout this depends on.
    /// </summary>
    /// <returns>False if the shared stop path could not confirm both controllers
    /// finished tearing down in time.</returns>
    private static Task<bool> HandleToolChangeAbortAsync() => HandleMillStopAsync();

    /// <returns>
    /// Why the request was refused, or null once it was carried out. An unrecognized action
    /// is refused rather than answered with the unchanged depth.
    /// </returns>
    private static string? HandleDepthAdjustment(DepthAdjustmentRequest req)
    {
        if (req.depth.HasValue)
        {
            AppState.SetDepthAdjustment(req.depth.Value);
            Logger.Log("Depth adjustment set to {0:F2}mm", AppState.DepthAdjustment);
            return null;
        }

        switch (req.action?.ToLowerInvariant())
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
            default:
                return ErrorInvalidRequest;
        }

        Logger.Log("Depth adjustment {0}: now {1:F2}mm", req.action, AppState.DepthAdjustment);
        return null;
    }

    private static object GetSettings()
    {
        var settings = AppState.Settings;
        return new
        {
            machineProfile = settings.MachineProfile,
            probeFeed = settings.ProbeFeed,
            probeMaxDepth = settings.ProbeMaxDepth,
            probeSafeHeight = settings.ProbeSafeHeight,
            probeMinimumHeight = settings.ProbeMinimumHeight,
            outlineTraceHeight = settings.OutlineTraceHeight,
            outlineTraceFeed = settings.OutlineTraceFeed,
            toolSetterX = settings.ToolSetterX,
            toolSetterY = settings.ToolSetterY,
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

        // Checked first, applied second: a half-applied update leaves the machine working to a
        // mix of old and new settings. SettingRanges pairs each range with its property, so
        // this table only maps each request field to its setting.
        var updates = new (double? Value, SettingBinding Setting)[]
        {
            (req.probeFeed, SettingRanges.ProbeFeed),
            (req.probeMaxDepth, SettingRanges.ProbeMaxDepth),
            (req.probeSafeHeight, SettingRanges.ProbeSafeHeight),
            (req.probeMinimumHeight, SettingRanges.ProbeMinimumHeight),
            (req.outlineTraceHeight, SettingRanges.OutlineTraceHeight),
            (req.outlineTraceFeed, SettingRanges.OutlineTraceFeed),
            (req.toolSetterX, SettingRanges.ToolSetterX),
            (req.toolSetterY, SettingRanges.ToolSetterY),
        };

        // An unknown profile turns the tool setter off silently, so it is checked too.
        if (req.machineProfile != null
            && !MachineProfiles.GetProfileIds().Contains(req.machineProfile))
        {
            response.StatusCode = HttpStatusBadRequest;
            await WriteJson(response, new { error = ErrorUnknownMachineProfile });
            return;
        }

        foreach (var (value, setting) in updates)
        {
            if (value is double given && setting.Range.Check(given) is string refused)
            {
                response.StatusCode = HttpStatusBadRequest;
                await WriteJson(response, new { error = refused });
                return;
            }
        }

        if (req.machineProfile != null)
        {
            settings.MachineProfile = req.machineProfile;
        }

        foreach (var (value, setting) in updates)
        {
            if (value is double given)
            {
                setting.Write(settings, given);
            }
        }

        Persistence.SaveSettings();

        await WriteJson(response, new { success = true });
    }

    /// <summary>Reloads the probe data from the autosave, even when a grid is already in
    /// memory.</summary>
    private static async Task HandleProbeRecoverAutosave(HttpListenerResponse response)
    {
        try
        {
            var (grid, refused) = AppState.ForceLoadProbeFromAutosave();
            if (grid == null)
            {
                response.StatusCode = HttpStatusConflict;
                await WriteJson(response, new { success = false, error = refused });
                return;
            }

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
            Logger.Log("HandleProbeRecoverAutosave: failed - {0}", ex);
            await WriteJson(response, new { success = false, error = ErrorServerFailure });
        }
    }
}
