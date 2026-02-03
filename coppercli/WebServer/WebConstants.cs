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
    /// Timeout for web server shutdown. Forces exit if shutdown hangs.
    /// </summary>
    public const int ShutdownTimeoutMs = 5000;

    /// <summary>
    /// Time to wait for a client to establish WebSocket after being served the page.
    /// After this timeout, the pending slot is freed for other clients.
    /// </summary>
    public const int PendingClientTimeoutMs = 10000;

    // --- Idle disconnect ---
    /// <summary>
    /// Time to wait before disconnecting Machine when no browser clients are connected
    /// after an operation completes. Allows user to reconnect (e.g., phone screen went dark).
    /// </summary>
    public const int IdleDisconnectTimeoutMs = 5 * 60 * 1000;  // 5 minutes

    // --- Content Types ---
    public const string ContentTypeJson = "application/json";
    public const string ContentTypeHtml = "text/html";
    public const string ContentTypeCss = "text/css";
    public const string ContentTypeJs = "application/javascript";

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

    // --- WebSocket Close Reasons ---
    public const string WsCloseReasonForceDisconnect = "Disconnected by another client";

    // --- Probe Parameter Limits ---
    public const double MinProbeMargin = 0.0;
    public const double MaxProbeMargin = 10.0;
    public const double MinProbeGridSize = 1.0;
    public const double MaxProbeGridSize = 50.0;

    // --- Probe State Strings (API response values) ---
    // 4 states based on in-memory grid progress:
    //   none: no grid
    //   ready: grid exists, progress=0
    //   partial: 0 < progress < total
    //   complete: progress = total
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
    public const string ApiMillToolChangeContinue = "/api/mill/toolchange/continue";
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
    public const string ApiProbeRecover = "/api/probe/recover";
    public const string ApiProbeClear = "/api/probe/clear";
    public const string ApiProbeDiscard = "/api/probe/discard";
    public const string ApiSettings = "/api/settings";
    public const string ApiProfiles = "/api/profiles";
    public const string ApiForceDisconnect = "/api/force-disconnect";
    public const string ApiTrustWorkZero = "/api/trust-work-zero";
    public const string ApiProbeRecoverAutosave = "/api/probe/recover-autosave";

    // --- WebSocket Commands (message types from browser) ---
    public const string WsCmdJogMode = "jog-mode";
    public const string WsCmdHome = "home";
    public const string WsCmdUnlock = "unlock";
    public const string WsCmdReset = "reset";
    public const string WsCmdFeedhold = "feedhold";
    public const string WsCmdResume = "resume";
    public const string WsCmdZero = "zero";
    public const string WsCmdGotoOrigin = "goto-origin";
    public const string WsCmdGotoCenter = "goto-center";
    public const string WsCmdGotoSafe = "goto-safe";
    public const string WsCmdGotoRef = "goto-ref";
    public const string WsCmdGotoZ0 = "goto-z0";
    public const string WsCmdProbeZ = "probe-z";

    // --- HTTP Methods ---
    public const string MethodPost = "POST";
    public const string MethodGet = "GET";

    // --- HTTP Status Codes ---
    public const int HttpStatusBadRequest = 400;
    public const int HttpStatusNotFound = 404;
    public const int HttpStatusMethodNotAllowed = 405;
    public const int HttpStatusServerError = 500;

    // Note: MillStopDelayMs is in CliConstants, MillCompleteZ is in coppercli.Core.Util.Constants

    // --- API Error Messages ---
    public const string ErrorNoFileLoaded = "No file loaded";
    public const string ErrorNotFound = "Not found";
    public const string ErrorInvalidRequest = "Invalid request";
    public const string ErrorMethodNotAllowed = "Method not allowed";
    public const string ErrorMachineNotConnected = "Machine not connected";
    public const string ErrorCannotPauseNotRunning = "Cannot pause: not running";
    public const string ErrorCannotResumeNotPaused = "Cannot resume: not paused";
    public const string ErrorProbingNotRunning = "Cannot pause: probing not running";
    public const string ErrorProbingNotPaused = "Cannot resume: probing not paused";
    public const string ErrorExpectedMultipart = "Expected multipart/form-data";
    public const string ErrorMissingBoundary = "Missing boundary";
    public const string ErrorNoFileInUpload = "No file in upload";
    public const string ErrorNoPathSpecified = "No path specified";
    public const string ErrorFileNotFound = "File not found";
    public const string ErrorNoCompleteProbeData = "No complete probe data to save";
    public const string ErrorNoToolChangeInProgress = "No tool change in progress";
    public const string ErrorMillingNotPaused = "Milling not paused";
    public const string ErrorNoPendingUserInput = "No pending user input request";
    public const string ErrorNoResponseProvided = "No response provided";
    public const string ErrorAlreadyConnected = "Already connected. Close the existing connection first.";
    public const string ErrorPortInUse = "Serial port is in use by another connection. Close the existing connection first.";
    public const string ErrorNoStoredWorkZero = "No stored work zero to trust";
    public const string ErrorNoAutosavedProbeData = "No autosaved probe data";

    // --- API Error Message Formats ---
    public const string ErrorInvalidFileType = "Invalid file type: {0}";

    // --- Depth Adjustment Actions ---
    public const string DepthActionIncrease = "increase";
    public const string DepthActionDecrease = "decrease";
    public const string DepthActionReset = "reset";

    // --- Preflight Error Messages ---
    public const string PreflightErrorNotConnected = "Machine not connected";
    public const string PreflightErrorNoFile = "No G-Code file loaded";
    public const string PreflightErrorProbeNotApplied = "Probe data exists but not applied";
    public const string PreflightErrorProbeIncomplete = "Probe incomplete ({0})";
    public const string PreflightErrorAlarm = "Machine is in ALARM state - home or unlock first";
    public const string PreflightWarningNotHomed = "Machine not homed - will home before milling";
    public const string PreflightWarningNoProfile = "No machine profile selected";

    /// <summary>Generic preflight error when error type is unknown.</summary>
    public const string PreflightErrorUnknown = "Unknown error";
}
