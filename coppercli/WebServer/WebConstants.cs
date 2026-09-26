namespace coppercli.WebServer;

public static class WebConstants
{
    public const int WebSocketBufferSize = 4096;

    /// <summary>
    /// Largest WebSocket message accepted. Commands are a few hundred bytes; anything larger
    /// is refused rather than accumulated.
    /// </summary>
    public const int WebSocketMaxMessageBytes = 64 * 1024;

    /// <summary>
    /// Send status at this interval because controller events occur faster than socket
    /// updates should be sent.
    /// </summary>
    public const int WebSocketBroadcastIntervalMs = 300;

    /// <summary>How often the server tries to connect to the machine while it is not connected.</summary>
    public const int ReconnectIntervalMs = 2000;

    /// <summary>
    /// How long a terminal that asked for the machine has to attach to the proxy before the
    /// server takes the machine back. The terminal waits CliConstants.TakeoverDelayMs
    /// after asking, then connects.
    /// </summary>
    public const int TerminalTakeoverWindowMs = 10000;

    public const int RequestPollTimeoutMs = 5000;

    public const int WebServerStartTimeoutMs = 5000;

    /// <summary>
    /// Forces exit if shutdown hangs. Covers a run unwinding
    /// (Constants.ControllerCancelTimeoutMs) and then stopping the machine.
    /// </summary>
    public const int ShutdownTimeoutMs = 20000;

    /// <summary>How long a client that was served the page has to open its WebSocket before
    /// the pending slot is freed for another client.</summary>
    public const int PendingClientTimeoutMs = 10000;

    /// <summary>How long a WebSocket close may take before the socket is aborted.</summary>
    public const int WebSocketCloseTimeoutMs = 1000;

    /// <summary>
    /// Largest JSON request body accepted. Each is a handful of fields, and the whole body is
    /// held in memory while it is parsed.
    /// </summary>
    public const int RequestBodyMaxBytes = 1024 * 1024;

    /// <summary>
    /// Largest file upload accepted. Parsing holds the bytes, decoded text, and split
    /// parts in memory together; board G-code is usually a few megabytes.
    /// </summary>
    public const int UploadMaxBytes = 16 * 1024 * 1024;

    /// <summary><see cref="UploadMaxBytes"/> in the megabytes an operator is told about.</summary>
    public const int UploadMaxMegabytes = UploadMaxBytes / (1024 * 1024);

    public const int BodyReadChunkBytes = 64 * 1024;

    public const string ContentTypeJson = "application/json";
    public const string ContentTypeHtml = "text/html";
    public const string ContentTypeCss = "text/css";
    public const string ContentTypeJs = "application/javascript";
    public const string ContentTypeText = "text/plain";

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
    /// from the tool-change prompt arriving in the same field. The browser draws the prompt
    /// from its own fields and does not branch on this, so it is not published through
    /// /api/constants.
    /// </summary>
    public const string PromptKindOperatorPause = "WaitingForOperator";

    public const string WsCloseReasonForceDisconnect = "Disconnected by another client";

    /// <summary>Sent to a client that has gone silent. The browser reconnects on it.</summary>
    public const string WsCloseReasonTimeout = "Timeout";

    // Published to the browser through /api/constants and checked there by
    // validateConstants, so both sides read one definition.

    public const int PositionDecimalsBrief = 1;

    public const int PositionDecimalsFull = 3;

    public const double MinProbeMargin = 0.0;
    public const double MaxProbeMargin = 10.0;
    public const double MinProbeGridSize = 1.0;
    public const double MaxProbeGridSize = 50.0;

    // What each state means, and which buttons it allows, is defined once at the top of
    // coppercli.Core/Controllers/ProbeController.cs.
    public const string ProbeStateNone = "none";
    public const string ProbeStateReady = "ready";
    public const string ProbeStatePartial = "partial";
    public const string ProbeStateComplete = "complete";

    public const string ApiStatus = "/api/status";
    public const string ApiConfig = "/api/config";
    public const string ApiConstants = "/api/constants";
    public const string ApiHome = "/api/home";
    public const string ApiUnlock = "/api/unlock";
    public const string ApiReset = "/api/reset";
    public const string ApiFeedhold = "/api/feedhold";
    public const string ApiResume = "/api/resume";

    /// <summary>
    /// Releases a door hold the operator has confirmed is safe. Separate from ApiResume,
    /// which refuses at a door: a cycle start there restarts the spindle, so it goes
    /// through this path, which only the door modal calls.
    /// </summary>
    public const string ApiDoorRelease = "/api/door/release";
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
    public const string ApiMillCanStart = "/api/mill/can-start";
    public const string ApiMillStart = "/api/mill/start";
    public const string ApiMillPause = "/api/mill/pause";
    public const string ApiMillResume = "/api/mill/resume";
    public const string ApiMillStop = "/api/mill/stop";
    public const string ApiMillToolChangeAbort = "/api/mill/toolchange/abort";
    public const string ApiMillToolChangeUserInput = "/api/mill/toolchange/input";
    public const string ApiMillDepth = "/api/mill/depth";
    public const string ApiMillGrid = "/api/mill/grid";  // Takes width and height as query parameters
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
    public const string ApiBrowserTakeover = "/api/browser-takeover";

    /// <summary>A terminal asking for the machine; the browser's takeover is ApiBrowserTakeover.</summary>
    public const string ApiTerminalTakeover = "/api/terminal-takeover";

    /// <summary>Questions and answers for restoring a session. The terminal startup
    /// uses the same source.</summary>
    public const string ApiSessionRestore = "/api/session/restore";
    public const string ApiProbeRecoverAutosave = "/api/probe/recover-autosave";

    // Sent by the browser; the WsMessageType* values go the other way.
    public const string WsCmdJogMode = "jog-mode";
    public const string WsCmdReset = "reset";
    public const string WsCmdFeedhold = "feedhold";
    public const string WsCmdGotoOrigin = "goto-origin";
    public const string WsCmdGotoCenter = "goto-center";
    public const string WsCmdGotoSafe = "goto-safe";
    public const string WsCmdGotoRef = "goto-ref";
    public const string WsCmdGotoZ0 = "goto-z0";
    public const string WsCmdProbeZ = "probe-z";

    /// <summary>The browser's keep-alive. Its arrival is what marks the client as still
    /// there.</summary>
    public const string WsCmdPing = "ping";

    public const string WsFieldType = "type";
    public const string WsFieldAxis = "axis";
    public const string WsFieldDirection = "direction";
    public const string WsFieldModeIndex = "modeIndex";

    public const string QueryParamPath = "path";
    public const string QueryParamWidth = "width";
    public const string QueryParamHeight = "height";
    public const string QueryParamClientId = "clientId";

    // Read by RequestPolicy, whose summary gives what each one decides.
    public const string HeaderOrigin = "Origin";
    public const string HeaderSecFetchSite = "Sec-Fetch-Site";

    /// <summary>Sec-Fetch-Site values naming an origin other than ours.</summary>
    public const string SecFetchSiteCrossSite = "cross-site";
    public const string SecFetchSiteSameSite = "same-site";

    /// <summary>The mDNS namespace, which only the local network can answer for.</summary>
    public const string HostMdnsSuffix = ".local";

    public const string WsPath = "/ws";

    /// <summary>
    /// Identifies the browser holding this session. The socket carries it as a query
    /// parameter and the page carries it as a cookie, so both sides read it from here.
    /// </summary>
    public const string ClientIdCookieName = "coppercli_client_id";
    public const string ApiPathPrefix = "/api/";

    // Set by ApplySecurityHeaders.
    public const string HeaderFrameOptions = "X-Frame-Options";
    public const string HeaderContentSecurityPolicy = "Content-Security-Policy";
    public const string HeaderContentTypeOptions = "X-Content-Type-Options";
    public const string HeaderReferrerPolicy = "Referrer-Policy";
    public const string FrameOptionsDeny = "DENY";
    public const string CspFrameAncestorsNone = "frame-ancestors 'none'";
    public const string ContentTypeOptionsNoSniff = "nosniff";
    public const string ReferrerPolicyNone = "no-referrer";

    public const string MethodPost = "POST";
    public const string MethodGet = "GET";

    public const int HttpStatusBadRequest = 400;
    public const int HttpStatusForbidden = 403;
    public const int HttpStatusNotFound = 404;
    public const int HttpStatusMethodNotAllowed = 405;
    public const int HttpStatusConflict = 409;
    public const int HttpStatusPayloadTooLarge = 413;
    public const int HttpStatusServerError = 500;

    // MillStopDelayMs is in CliConstants; SafeClearanceZ is in coppercli.Core.Util.Constants.

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

    public const string ErrorNothingToResume = "The machine is not holding, so there is nothing to resume.";

    public const string ErrorNoDoorToRelease = "The machine is not at the door.";
    public const string ErrorCannotPauseNotRunning = "Cannot pause: not running";
    public const string ErrorCannotResumeNotPaused = "Cannot resume: not paused";
    public const string ErrorCannotResumeToolChangeActive = "Cannot resume: tool change in progress";
    public const string ErrorProbingNotRunning = "Cannot pause: probing not running";

    /// <summary>
    /// Shown when Pause arrives on a run that is already paused. The height check pauses a
    /// run on its own, so this happens in normal use.
    /// </summary>
    public const string ErrorProbingAlreadyPaused = "Probing is already paused";
    public const string ErrorProbingNotPaused = "Cannot resume: probing not paused";
    public const string ErrorExpectedMultipart = "Expected multipart/form-data";
    public const string ErrorMissingBoundary = "Missing boundary";
    public const string ErrorNoFileInUpload = "No file in upload";
    public const string ErrorNoPathSpecified = "No path specified.";
    public const string ErrorFileNotFound = "File not found";
    /// <summary>Uses the terminal's message for the same refusal.</summary>
    public const string ErrorNoCompleteProbeData = CliConstants.ProbeErrorNoComplete;
    public const string ErrorProbeSaveFailed = "Could not save the probe data. Try another folder.";
    public const string ErrorNoToolChangeInProgress = "No tool change in progress";
    public const string ErrorMillingNotPaused = "Milling not paused";
    public const string ErrorNoPendingUserInput = "No pending user input request";

    public const string ErrorNotAnOption = "That is not one of the options offered.";

    public const string ErrorPromptAlreadyAnswered =
        CliConstants.ErrorQuestionAlreadyAnswered + " Answer the one on screen now.";

    public const string ErrorMachineBusy = "The machine is busy with a job. Stop it first.";

    /// <summary>The field of a JSON answer that carries the reason a request was refused.</summary>
    public const string JsonFieldError = "error";

    /// <summary>Refuses a terminal's takeover; see CncWebServer.AnyOperationRunning.</summary>
    public const string ErrorTakeoverWhileBusy =
        "The server is running a job, homing, or probing. Try again when it has finished.";

    public const string ErrorPathNotOnThisComputer = "That path is not on this computer.";

    public const string ErrorUnknownMachineProfile = "Unknown machine profile.";

    /// <summary>The characters Windows accepts as a path separator.</summary>
    public const string PathSeparators = "\\/";

    /// <summary>After a leading separator, marks a Windows device path such as "\??\UNC\host\share".</summary>
    public const char DevicePathMarker = '?';

    public const string ErrorMillingAlreadyRunning = "A job is already running. Stop it first.";
    public const string ErrorNoProbeGrid = "No probe grid. Run Setup first.";
    public const string ErrorBodyTooLarge = "Too much data in one request. Nothing was sent to the machine.";

    /// <summary>Formatted with <see cref="UploadMaxMegabytes"/>.</summary>
    public const string ErrorUploadTooLarge = "File too large to upload. The limit is {0} MB.";

    public const string ErrorInvalidFileType = "Invalid file type: {0}";

    /// <summary>Appended to CliConstants.SleepPreventionWarning, which names the
    /// condition.</summary>
    public const string WarningSleepPreventionAction =
        "Plug in the computer running coppercli, and turn sleep off.";

    public const string DepthActionIncrease = "increase";
    public const string DepthActionDecrease = "decrease";
    public const string DepthActionReset = "reset";

    public const string MillBlockedNotConnected = "Machine not connected";
    public const string MillBlockedNoFile = "No G-Code file loaded";
    public const string MillBlockedProbeNotApplied = "Probe data exists but not applied";
    public const string MillBlockedProbeSetupChanged =
        "The applied height map was measured for a different file or work origin. Probe again before milling.";
    public const string MillBlockedProbeIncomplete = "Probe incomplete ({0})";
    public const string MillBlockedAlarm = "Machine is in ALARM state - home or unlock first";
    public const string MillBlockedAsleep = "Machine is asleep - reset it first";
    public const string MillWarningNotHomed = "Machine not homed - will home before milling";
    public const string MillWarningNoProfile = "No machine profile selected";

    public const string MillBlockedUnknown = "Unknown error";
}
