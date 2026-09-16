namespace coppercli.WebServer;

/// <summary>
/// Constants for the web server.
/// </summary>
public static class WebConstants
{
    // --- WebSocket ---
    /// <summary>WebSocket receive buffer size (bytes).</summary>
    public const int WebSocketBufferSize = 4096;

    /// <summary>
    /// Largest WebSocket message accepted. Commands are a few hundred bytes; anything larger
    /// is refused rather than accumulated.
    /// </summary>
    public const int WebSocketMaxMessageBytes = 64 * 1024;

    /// <summary>
    /// Interval for broadcasting status updates to WebSocket clients.
    /// Throttles high-frequency controller events to avoid overwhelming the connection.
    /// </summary>
    public const int WebSocketBroadcastIntervalMs = 300;

    // --- Reconnection ---
    public const int ReconnectIntervalMs = 2000;
    public const int ReconnectMaxAttempts = 0;  // 0 = infinite
    public const int ProxyRejectionCheckDelayMs = 100;  // Wait for proxy rejection message after connect

    // --- Request handling ---
    /// <summary>
    /// Timeout for waiting on incoming HTTP requests. Prevents hanging forever.
    /// </summary>
    public const int RequestPollTimeoutMs = 5000;

    /// <summary>
    /// Timeout for waiting for web server to start during server mode initialization.
    /// </summary>
    public const int WebServerStartTimeoutMs = 5000;

    /// <summary>
    /// Timeout for web server shutdown. Forces exit if shutdown hangs. Covers a run
    /// unwinding (Constants.ControllerCancelTimeoutMs) and then stopping the machine.
    /// </summary>
    public const int ShutdownTimeoutMs = 20000;

    /// <summary>
    /// Time to wait for a client to establish WebSocket after being served the page.
    /// After this timeout, the pending slot is freed for other clients.
    /// </summary>
    public const int PendingClientTimeoutMs = 10000;

    /// <summary>How long a close frame is waited for before the client is dropped.</summary>
    public const int ForceDisconnectCloseTimeoutMs = 1000;

    // --- Idle disconnect ---
    /// <summary>
    /// Time to wait before disconnecting Machine when no browser clients are connected
    /// after an operation completes. Allows user to reconnect (e.g., phone screen went dark).
    /// </summary>
    public const int IdleDisconnectTimeoutMs = 5 * 60 * 1000;  // 5 minutes

    // --- Request body limits ---
    /// <summary>
    /// Largest JSON request body accepted. Each is a handful of fields, and the whole body is
    /// held in memory while it is parsed.
    /// </summary>
    public const int RequestBodyMaxBytes = 1024 * 1024;

    /// <summary>
    /// Largest file upload accepted. Parsing holds the bytes, a string of them and the split
    /// parts at once. G-code for a board runs to a few megabytes.
    /// </summary>
    public const int UploadMaxBytes = 16 * 1024 * 1024;

    /// <summary><see cref="UploadMaxBytes"/> in the megabytes an operator is told about.</summary>
    public const int UploadMaxMegabytes = UploadMaxBytes / (1024 * 1024);

    /// <summary>How much of a request body is read at a time.</summary>
    public const int BodyReadChunkBytes = 64 * 1024;

    // --- Content Types ---
    public const string ContentTypeJson = "application/json";
    public const string ContentTypeHtml = "text/html";
    public const string ContentTypeCss = "text/css";
    public const string ContentTypeJs = "application/javascript";
    public const string ContentTypeText = "text/plain";

    // --- WebSocket Message Types ---
    public const string WsMessageTypeStatus = "status";
    public const string WsMessageTypeMillState = "mill:state";
    public const string WsMessageTypeMillProgress = "mill:progress";
    public const string WsMessageTypeMillToolChange = "mill:toolchange";
    public const string WsMessageTypeMillError = "mill:error";
    public const string WsMessageTypeToolChangeState = "toolchange:state";
    public const string WsMessageTypeToolChangeProgress = "toolchange:progress";
    public const string WsMessageTypeToolChangeInput = "toolchange:input";
    public const string WsMessageTypeToolChangeComplete = "toolchange:complete";
    public const string WsMessageTypeToolChangeError = "toolchange:error";
    public const string WsMessageTypeConnectionError = "connection:error";
    public const string WsMessageTypeProbeError = "probe:error";

    /// <summary>
    /// Marks the prompt the browser shows for a plain program stop, so it can tell one
    /// from the tool-change prompt arriving in the same field.
    ///
    /// A kind of prompt, not a run state. PROMPT_KIND_OPERATOR_PAUSE in constants.js must
    /// match it; validateConstants compares the two at startup.
    /// </summary>
    public const string PromptKindOperatorPause = "WaitingForOperator";

    // --- WebSocket Close Reasons ---
    public const string WsCloseReasonForceDisconnect = "Disconnected by another client";

    /// <summary>Sent to a client that has gone silent. The browser reconnects on it.</summary>
    public const string WsCloseReasonTimeout = "Timeout";

    // --- Display formatting ---
    // Published to the browser through /api/constants and checked there by
    // validateConstants, so both sides read one definition.

    /// <summary>Decimal places for a position shown in a compact readout.</summary>
    public const int PositionDecimalsBrief = 1;

    /// <summary>Decimal places for a position shown in full.</summary>
    public const int PositionDecimalsFull = 3;

    // --- Probe Parameter Limits ---
    public const double MinProbeMargin = 0.0;
    public const double MaxProbeMargin = 10.0;
    public const double MinProbeGridSize = 1.0;
    public const double MaxProbeGridSize = 50.0;

    // --- Probe State Strings (API response values) ---
    // What each state means, and which buttons it allows, is defined once in the remarks
    // block at the top of coppercli.Core/Controllers/ProbeController.cs.
    public const string ProbeStateNone = "none";
    public const string ProbeStateReady = "ready";
    public const string ProbeStatePartial = "partial";
    public const string ProbeStateComplete = "complete";

    // --- API Paths ---
    public const string ApiStatus = "/api/status";
    public const string ApiConfig = "/api/config";
    public const string ApiConstants = "/api/constants";
    public const string ApiPorts = "/api/ports";
    public const string ApiConnect = "/api/connect";
    public const string ApiDisconnect = "/api/disconnect";
    public const string ApiHome = "/api/home";
    public const string ApiUnlock = "/api/unlock";
    public const string ApiReset = "/api/reset";
    public const string ApiFeedhold = "/api/feedhold";
    public const string ApiResume = "/api/resume";
    public const string ApiZero = "/api/zero";
    public const string ApiGotoOrigin = "/api/goto-origin";
    public const string ApiGotoCenter = "/api/goto-center";
    public const string ApiGotoSafe = "/api/goto-safe";
    public const string ApiGotoRef = "/api/goto-ref";
    public const string ApiGotoZ0 = "/api/goto-z0";
    public const string ApiProbeZ = "/api/probe-z";
    public const string ApiFiles = "/api/files";
    public const string ApiFileLoad = "/api/file/load";
    public const string ApiFileUpload = "/api/file/upload";
    public const string ApiFileInfo = "/api/file/info";
    public const string ApiMillPreflight = "/api/mill/preflight";
    public const string ApiMillStart = "/api/mill/start";
    public const string ApiMillPause = "/api/mill/pause";
    public const string ApiMillResume = "/api/mill/resume";
    public const string ApiMillStop = "/api/mill/stop";
    public const string ApiMillToolChangeAbort = "/api/mill/toolchange/abort";
    public const string ApiMillToolChangeUserInput = "/api/mill/toolchange/input";
    public const string ApiMillDepth = "/api/mill/depth";
    public const string ApiMillGrid = "/api/mill/grid";  // Get visited grid cells (pass width/height as query params)
    public const string ApiFeedIncrease = "/api/feed-override/increase";
    public const string ApiFeedDecrease = "/api/feed-override/decrease";
    public const string ApiFeedReset = "/api/feed-override/reset";
    public const string ApiProbeSetup = "/api/probe/setup";
    public const string ApiProbeTrace = "/api/probe/trace";
    public const string ApiProbeStart = "/api/probe/start";
    public const string ApiProbePause = "/api/probe/pause";
    public const string ApiProbeResume = "/api/probe/resume";
    public const string ApiProbeStop = "/api/probe/stop";
    public const string ApiProbeStatus = "/api/probe/status";
    public const string ApiProbeApply = "/api/probe/apply";
    public const string ApiProbeSave = "/api/probe/save";
    public const string ApiProbeLoad = "/api/probe/load";
    public const string ApiProbeFiles = "/api/probe/files";
    public const string ApiProbeClear = "/api/probe/clear";
    public const string ApiProbeDiscard = "/api/probe/discard";
    public const string ApiSettings = "/api/settings";
    public const string ApiProfiles = "/api/profiles";
    public const string ApiForceDisconnect = "/api/force-disconnect";
    public const string ApiTrustWorkZero = "/api/trust-work-zero";

    /// <summary>Questions carried over from the previous session, and answers to them.
    /// Same source as the terminal startup, so the two cannot drift apart.</summary>
    public const string ApiSessionRestore = "/api/session/restore";
    public const string ApiProbeRecoverAutosave = "/api/probe/recover-autosave";

    // --- WebSocket Commands (message types from browser) ---
    public const string WsCmdJogMode = "jog-mode";
    public const string WsCmdHome = "home";
    public const string WsCmdUnlock = "unlock";
    public const string WsCmdReset = "reset";
    public const string WsCmdFeedhold = "feedhold";
    public const string WsCmdResume = "resume";
    public const string WsCmdGotoOrigin = "goto-origin";
    public const string WsCmdGotoCenter = "goto-center";
    public const string WsCmdGotoSafe = "goto-safe";
    public const string WsCmdGotoRef = "goto-ref";
    public const string WsCmdGotoZ0 = "goto-z0";
    public const string WsCmdProbeZ = "probe-z";

    /// <summary>
    /// The browser's keep-alive. Receiving it is the point: it marks the client as still
    /// there.
    /// </summary>
    public const string WsCmdPing = "ping";

    // --- WebSocket Message Fields ---
    // Protocol wire values, read only where a command carries one.
    public const string WsFieldType = "type";
    public const string WsFieldAxis = "axis";
    public const string WsFieldDirection = "direction";
    public const string WsFieldModeIndex = "modeIndex";

    // --- Query String Parameter Keys ---
    // Protocol wire values (like ApiXxx/WsCmdXxx), consumed only server-side.
    public const string QueryParamPath = "path";
    public const string QueryParamWidth = "width";
    public const string QueryParamHeight = "height";
    public const string QueryParamClientId = "clientId";

    // --- Request Headers (see RequestGuard) ---
    public const string HeaderOrigin = "Origin";
    public const string HeaderSecFetchSite = "Sec-Fetch-Site";

    /// <summary>Sec-Fetch-Site values naming an origin other than ours.</summary>
    public const string SecFetchSiteCrossSite = "cross-site";
    public const string SecFetchSiteSameSite = "same-site";

    /// <summary>The mDNS namespace, which only the local network can answer for.</summary>
    public const string HostMdnsSuffix = ".local";

    // --- Request Path Prefixes ---
    public const string WsPath = "/ws";
    public const string ApiPathPrefix = "/api/";

    // --- Response Security Headers (see ApplySecurityHeaders) ---
    public const string HeaderFrameOptions = "X-Frame-Options";
    public const string HeaderContentSecurityPolicy = "Content-Security-Policy";
    public const string HeaderContentTypeOptions = "X-Content-Type-Options";
    public const string HeaderReferrerPolicy = "Referrer-Policy";
    public const string FrameOptionsDeny = "DENY";
    public const string CspFrameAncestorsNone = "frame-ancestors 'none'";
    public const string ContentTypeOptionsNoSniff = "nosniff";
    public const string ReferrerPolicyNone = "no-referrer";

    // --- HTTP Methods ---
    public const string MethodPost = "POST";
    public const string MethodGet = "GET";

    // --- HTTP Status Codes ---
    public const int HttpStatusBadRequest = 400;
    public const int HttpStatusForbidden = 403;
    public const int HttpStatusNotFound = 404;
    public const int HttpStatusMethodNotAllowed = 405;
    public const int HttpStatusConflict = 409;
    public const int HttpStatusPayloadTooLarge = 413;
    public const int HttpStatusServerError = 500;

    // Note: MillStopDelayMs is in CliConstants, MillCompleteZ is in coppercli.Core.Util.Constants

    // --- API Error Messages ---
    public const string ErrorNoFileLoaded = "No file loaded";
    public const string ErrorNotFound = "Not found";
    public const string ErrorInvalidRequest = "Invalid request";
    public const string ErrorMethodNotAllowed = "Method not allowed";
    public const string ErrorForbidden =
        "Refused. Open coppercli at the numeric address it printed at startup, from a browser "
        + "on the same network. A domain name will not work unless it is a plain machine name "
        + "such as mill or mill.local.";
    public const string ErrorServerFailure =
        "Something went wrong. Try again; if it keeps happening, check the computer running coppercli.";
    public const string ErrorMachineNotConnected = "Machine not connected";
    public const string ErrorCannotPauseNotRunning = "Cannot pause: not running";
    public const string ErrorCannotResumeNotPaused = "Cannot resume: not paused";
    public const string ErrorCannotResumeToolChangeActive = "Cannot resume: tool change in progress";
    public const string ErrorProbingNotRunning = "Cannot pause: probing not running";

    /// <summary>
    /// Shown when Pause arrives on a run that is already paused. The height check stops a
    /// run on its own, so this is an ordinary thing to meet.
    /// </summary>
    public const string ErrorProbingAlreadyPaused = "Probing is already paused";
    public const string ErrorProbingNotPaused = "Cannot resume: probing not paused";
    public const string ErrorExpectedMultipart = "Expected multipart/form-data";
    public const string ErrorMissingBoundary = "Missing boundary";
    public const string ErrorNoFileInUpload = "No file in upload";
    public const string ErrorNoPathSpecified = "No path specified";
    public const string ErrorFileNotFound = "File not found";
    public const string ErrorNoCompleteProbeData = "No complete probe data to save";
    public const string ErrorProbeSaveFailed = "Could not save the probe data. Try another folder.";
    public const string ErrorNoToolChangeInProgress = "No tool change in progress";
    public const string ErrorMillingNotPaused = "Milling not paused";
    public const string ErrorNoPendingUserInput = "No pending user input request";

    /// <summary>Shown when an answer is not one of the choices the question offered.</summary>
    public const string ErrorNotAnOption = "That is not one of the choices offered.";

    /// <summary>Shown when an answer names a prompt other than the one on screen.</summary>
    public const string ErrorPromptAlreadyAnswered =
        "That question has already been answered. Answer the one on screen now.";

    public const string ErrorMachineBusy = "The machine is busy with a job. Stop it first.";
    public const string ErrorMillingAlreadyRunning = "A job is already running. Stop it first.";
    public const string ErrorNoProbeGrid = "No probe grid. Run Setup first.";
    public const string ErrorBodyTooLarge = "Too much data in one request. Nothing was sent to the machine.";

    /// <summary>Formatted with the limit in megabytes, so the number has one home.</summary>
    public const string ErrorUploadTooLarge = "File too large to upload. The limit is {0} MB.";

    public const string ErrorAlreadyConnected = "Already connected. Close the existing connection first.";
    public const string ErrorPortInUse = "Serial port is in use by another connection. Close the existing connection first.";
    public const string ErrorNoStoredWorkZero = "No stored work zero to trust";
    public const string ErrorNoAutosavedProbeData = "No autosaved probe data";

    // --- API Error Message Formats ---
    public const string ErrorInvalidFileType = "Invalid file type: {0}";

    // --- API Warnings ---
    /// <summary>
    /// Appended to CliConstants.SleepPreventionWarning, which names the condition.
    /// </summary>
    public const string WarningSleepPreventionAction =
        "Plug in the computer running coppercli, and turn sleep off.";

    // --- Depth Adjustment Actions ---
    public const string DepthActionIncrease = "increase";
    public const string DepthActionDecrease = "decrease";
    public const string DepthActionReset = "reset";

    // --- Preflight Error Messages ---
    public const string PreflightErrorNotConnected = "Machine not connected";
    public const string PreflightErrorNoFile = "No G-Code file loaded";
    public const string PreflightErrorProbeNotApplied = "Probe data exists but not applied";
    public const string PreflightErrorProbeSetupChanged =
        "The applied height map was measured for a different file or work origin. Probe again before milling.";
    public const string PreflightErrorProbeIncomplete = "Probe incomplete ({0})";
    public const string PreflightErrorAlarm = "Machine is in ALARM state - home or unlock first";
    public const string PreflightWarningNotHomed = "Machine not homed - will home before milling";
    public const string PreflightWarningNoProfile = "No machine profile selected";

    /// <summary>Generic preflight error when error type is unknown.</summary>
    public const string PreflightErrorUnknown = "Unknown error";
}
