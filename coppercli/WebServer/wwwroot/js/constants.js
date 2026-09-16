// coppercli Web UI Constants
//
// Constants are organized into three categories:
//
// 1. JS-ONLY: No server counterpart. Used only by the web UI.
//
// 2. DUPLICATED FROM SERVER: These values MUST match their C# counterparts.
//    JS needs static values for switch statements and module initialization.
//    Validated against /api/constants at startup - mismatches log warnings.
//    Source of truth: WebConstants.cs, GrblProtocol.cs, ControllerState enum
//
// 3. FETCHED FROM SERVER: Loaded at runtime via /api/config to avoid duplication.
//    See state.js for runtime values (jogModes, probeDefaults, millGrid).

// =============================================================================
// JS-ONLY: Timing (no server counterpart)
// =============================================================================
export const MAX_RECONNECT_ATTEMPTS = 120;  // 2 minutes of reconnect attempts
export const RECONNECT_DELAY_MS = 1000;
export const FORCE_DISCONNECT_RECONNECT_DELAY_MS = 5000;  // Longer delay when kicked by another client
export const WEBSOCKET_PING_INTERVAL_MS = 10000;  // Keep-alive ping (server timeout is 30s)
export const JOG_TOUCH_REPEAT_MS = 200;
export const DOUBLE_TAP_DELAY_MS = 300;  // Max time between taps for double-tap detection

// How long a newly drawn prompt refuses to be answered. A run puts its next question up in
// the same place the moment the last is answered, so a double-tap would answer one the
// operator has not read. Longer than DOUBLE_TAP_DELAY_MS.
export const PROMPT_SETTLE_MS = 600;
export const TOAST_ANIMATION_DELAY_MS = 10;
export const TOAST_FADE_DURATION_MS = 300;
export const TOAST_ERROR_DURATION_MS = 5000;
export const TOAST_INFO_DURATION_MS = 3000;
export const PROBE_POLL_INTERVAL_MS = 500;
// How long the start button stays dead after a trace stops, so a finger still on STOP
// does not start a probe.
export const TRACE_BUTTON_SETTLE_MS = 500;
// Failed status reads in a row before a trace stops waiting and says the machine is out of
// contact, rather than holding the screen locked for ever.
export const TRACE_POLL_MAX_FAILURES = 20;
export const ERROR_OTHER_CLIENT_SUBSTRING = 'another client';  // Substring to detect other client connection error

// =============================================================================
// JS-ONLY: Display formatting (no server counterpart)
// =============================================================================
export const PROGRESS_PERCENT_MULTIPLIER = 100;
export const PROBE_GRID_CELL_SIZE_PX = 20;
export const BYTES_PER_KB = 1024;
export const BYTES_PER_MB = 1024 * 1024;

// =============================================================================
// JS-ONLY: UI defaults (no server counterpart)
// =============================================================================
export const PROBE_FILE_EXTENSION = '.pgrid';

// =============================================================================
// JS-ONLY: Screen IDs (HTML element IDs)
// =============================================================================
export const SCREEN_DASHBOARD = 'dashboard-screen';
export const SCREEN_JOG = 'jog-screen';
export const SCREEN_FILE = 'file-screen';
export const SCREEN_MILL = 'mill-screen';
export const SCREEN_PROBE = 'probe-screen';
export const SCREEN_PROBE_FILES = 'probe-files-screen';
export const SCREEN_SUFFIX = '-screen';

// =============================================================================
// JS-ONLY: CSS classes (no server counterpart)
// =============================================================================
export const CLASS_HIDDEN = 'hidden';
export const CLASS_ACTIVE = 'active';
export const CLASS_CONNECTED = 'connected';
export const CLASS_ALARM = 'alarm';
export const CLASS_SELECTED = 'selected';
export const CLASS_PROBED = 'probed';
export const CLASS_DISABLED = 'disabled';
export const CLASS_RUNNING = 'running';
export const CLASS_HOLD = 'hold';
export const CLASS_SHOW = 'show';
export const CLASS_PROBE_OPEN = 'open';
export const CLASS_PROBE_CONTACT = 'contact';
export const CLASS_CLICKABLE = 'clickable';

// =============================================================================
// JS-ONLY: UI text (no server counterpart)
// =============================================================================
export const TEXT_DISCONNECTED = 'Disconnected';
export const TEXT_CONNECTED = 'Connected';
export const TEXT_RECONNECTING = 'Reconnecting...';
export const TEXT_LOADING = 'Loading...';
export const TEXT_LOAD = 'Load';
export const TEXT_PAUSE = 'Pause';
export const TEXT_RESUME = 'Resume';

// SVG Icons (14x14 for inline buttons)
export const ICON_PAUSE = '<svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><rect x="6" y="4" width="4" height="16"/><rect x="14" y="4" width="4" height="16"/></svg>';
export const ICON_RESUME = '<svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><polygon points="5 3 19 12 5 21 5 3"/></svg>';
export const TEXT_START_PROBING = 'Start Probing';
export const TEXT_STOP = 'STOP';
export const CLASS_BTN_DANGER = 'btn-danger';
export const CLASS_BTN_SUCCESS = 'btn-success';
export const CLASS_BTN_WARNING = 'btn-warning';
export const TEXT_UPLOAD = 'Upload';
export const TEXT_UPLOADING = 'Uploading...';
export const TEXT_PROBE_APPLIED = 'applied';
export const TEXT_PROBE_NOT_APPLIED = 'not applied';
export const TEXT_MILL_RUNNING = 'Running';
export const TEXT_MILL_PAUSED = 'Paused';
export const TEXT_TOOL_CHANGE = 'Tool Change';
export const TEXT_CONTINUE_PROBING = 'Continue';
export const TEXT_UNKNOWN = 'Unknown';
export const TEXT_NO_FILES = 'No files found';
export const TEXT_PROBING_COMPLETE = 'Probing complete!';
export const TEXT_PROBING_TITLE = 'Probing';
export const TEXT_PROBING_DONE_TITLE = 'Probing Complete';
export const TEXT_MILLING_COMPLETE = 'Milling complete!';
export const TEXT_CONNECTION_LOST = 'Connection lost. Please refresh the page.';
export const TEXT_PROBING_IN_PROGRESS = 'Probing in progress';
export const TEXT_MILLING_IN_PROGRESS = 'Milling in progress';
export const TEXT_PROBE_DATA_SAVED = 'Probe data saved';
export const TEXT_PROBE_DATA_LOADED = 'Probe data loaded';
export const TEXT_PROBE_DATA_APPLIED = ' (applied to G-code)';
export const TEXT_PROBE_DATA_CLEARED = 'Probe data cleared';
export const TEXT_SETTINGS_SAVED = 'Settings saved';
export const TEXT_NO_PROBE_DATA = 'No probe data to save';
export const TEXT_FILE_UPLOADED = 'File uploaded';
export const TEXT_PROBE_PIN_OPEN = 'Open';
export const TEXT_PROBE_PIN_CONTACT = 'Contact';
export const TEXT_SAVE = 'Save';
export const TEXT_SAVING = 'Saving...';
export const TEXT_SAVE_PROBE_DATA = 'Save Probe Data';
export const TEXT_LOAD_PROBE_DATA = 'Load Probe Data';
export const TEXT_DISCARD = 'Discard';
export const TEXT_CLEAR = 'Clear';
export const TEXT_ENTER_FILENAME = 'Please enter a filename';
export const TEXT_WORK_ZERO_TRUSTED = 'Work zero trusted';
export const TEXT_PROBE_RECOVERED = 'Recovered {0}/{1} probe points';
export const TEXT_RECOVERY_FAILED = 'Recovery failed';
export const TEXT_SETUP_FAILED = 'Could not set up the grid';
export const TEXT_TRACE_FAILED = 'The outline trace did not finish';
export const TEXT_APPLY_FAILED = 'Could not apply the probe data to the G-code';
export const TEXT_PROBE_APPLIED_TO_GCODE = 'Probe data applied to G-code';
export const TEXT_DISCARD_FAILED = 'Could not discard the probe data';
export const TEXT_DISCARD_CONFIRM = 'Discard all probe data?';
export const TEXT_DISCARD_TITLE = 'Discard Probe Data';
export const TEXT_SOURCE_GCODE_MISSING =
    'The G-code this map was measured for is missing. Load it to carry on probing.';
export const TEXT_PAUSE_FAILED = 'Could not pause or resume the probe';
export const TEXT_PROBE_LOAD_FAILED = 'Could not load the probe data';
export const TEXT_PROBE_SAVE_FAILED = 'Could not save the probe data';
export const TEXT_FILE_LOAD_FAILED = 'Could not load the file';
export const TEXT_UPLOAD_FAILED = 'Could not upload the file';
export const TEXT_SETTINGS_SAVE_FAILED = 'Could not save the settings';
export const TEXT_NO_FILE_LOADED = 'Load a G-code file first';
export const TEXT_ABORT_MILLING_CONFIRM = 'Abort milling?';
export const TEXT_ABORT_MILLING_TITLE = 'Abort';
export const TEXT_PROBE_REMOVED_CONFIRM = 'Probing equipment removed?';
export const TEXT_START_MILLING_TITLE = 'Start Milling';
export const TEXT_TOOL_CHANGE_FAILED = 'The tool change did not finish';
export const ERROR_LOST_CONTACT =
    'Lost contact with the machine. Check it before moving anything.';
export const TEXT_NOT_LOADED = '[not loaded]';
export const TEXT_FORCE_DISCONNECT_CONFIRM = 'Another client is connected. Force disconnect to take over?';
export const TEXT_FORCE_DISCONNECT_FAILED = 'Failed to disconnect';
export const TITLE_FORCE_DISCONNECT = 'Force Disconnect';

// =============================================================================
// DUPLICATED FROM SERVER: Display decimals
// Source: GetSharedConstants() in CncWebServer.cs
// =============================================================================
export const POSITION_DECIMALS_BRIEF = 1;
export const POSITION_DECIMALS_FULL = 3;

// =============================================================================
// DUPLICATED FROM SERVER: Visualization thresholds
// Source: Constants.cs (HeightRangeEpsilon, MillMinRangeThreshold)
// =============================================================================
export const HEIGHT_RANGE_EPSILON = 0.0001;  // Minimum height range for color gradient
export const MILL_MIN_RANGE_THRESHOLD = 0.001;  // Minimum coordinate range

// =============================================================================
// DUPLICATED FROM SERVER: Machine status strings
// Source: GrblProtocol.cs (StatusRun, StatusHold, StatusIdle, StatusAlarm, StatusDoor)
// =============================================================================
export const STATUS_ALARM_PREFIX = 'Alarm';
export const STATUS_DOOR = 'Door';
export const STATUS_RUN = 'Run';
export const STATUS_HOLD = 'Hold';
export const STATUS_IDLE = 'Idle';

// =============================================================================
// DUPLICATED FROM SERVER: Controller states
// Source: ControllerState enum in coppercli.Core/Controllers/ControllerConstants.cs
// =============================================================================
export const CONTROLLER_STATE_IDLE = 'Idle';
export const CONTROLLER_STATE_INITIALIZING = 'Initializing';
export const CONTROLLER_STATE_RUNNING = 'Running';
export const CONTROLLER_STATE_PAUSED = 'Paused';
export const CONTROLLER_STATE_WAITING_FOR_USER_INPUT = 'WaitingForUserInput';
export const CONTROLLER_STATE_COMPLETING = 'Completing';
export const CONTROLLER_STATE_COMPLETED = 'Completed';
export const CONTROLLER_STATE_FAILED = 'Failed';
export const CONTROLLER_STATE_CANCELLED = 'Cancelled';

// =============================================================================
// DUPLICATED FROM SERVER: The workflow phases this UI changes its display for.
// Source: GetSharedConstants() in CncWebServer.cs (MillingPhase, ProbePhase,
// ToolChangePhase). Every other phase is shown as it arrives, so only these must agree.
// =============================================================================
export const PHASE_MILLING = 'Milling';
export const PHASE_TRACING_OUTLINE = 'TracingOutline';
export const PHASE_WAITING_FOR_TOOL_CHANGE = 'WaitingForToolChange';
export const PHASE_WAITING_FOR_ZERO_Z = 'WaitingForZeroZ';

// Marks a plain program stop inside status.toolChange, telling it from a tool change.
// Validated against the server's own value at startup; see validateConstants.
export const PROMPT_KIND_OPERATOR_PAUSE = 'WaitingForOperator';

// The choice that carries a prompt on. The server refuses anything that is not one of the
// choices it offered, so both sides spell it the same way.
export const PROMPT_OPTION_CONTINUE = 'Continue';

// GRBL reports Door both while the enclosure is open and after it is closed, waiting to
// be resumed. The server tells us which; showing the raw state would leave the operator
// looking at "Door" having already shut it.
export const DOOR_OPEN_TEXT = 'Door open';
export const DOOR_CLOSED_TEXT = 'Door closed - press resume';

// Shown when a request never reaches coppercli. The browser's own exception text names
// nothing an operator standing at the machine can act on, and the machine may still be
// moving, so these say what to do instead.
export const ERROR_STOP_NOT_SENT =
    'Could not reach coppercli to stop the job. The machine may still be moving - check it directly.';
export const ERROR_ABORT_NOT_SENT =
    'Could not reach coppercli to abort. The machine may still be moving - check it directly.';
export const ERROR_START_NOT_SENT =
    'Could not reach coppercli to start the job. Check the connection, then try again.';
export const ERROR_PAUSE_NOT_SENT =
    'Could not reach coppercli. The job is still running - check the machine directly.';
export const ERROR_INPUT_NOT_SENT =
    'Could not reach coppercli. The job is still waiting for an answer - try again.';
export const ERROR_PROBE_NOT_STARTED =
    'Could not start probing. Check the connection, then try again.';
export const ERROR_NOTHING_TO_ANSWER = 'Nothing is waiting for an answer.';
export const ERROR_ZERO_NOT_SENT =
    'Could not reach coppercli to set the work zero. It has not been set.';

// =============================================================================
// Mirrors WebConstants.cs; validateConstants checks them against /api/constants at
// startup. What each state means is defined once, in the remarks block at the top of
// coppercli.Core/Controllers/ProbeController.cs.
// =============================================================================
export const PROBE_STATE_NONE = 'none';
export const PROBE_STATE_READY = 'ready';
export const PROBE_STATE_PARTIAL = 'partial';
export const PROBE_STATE_COMPLETE = 'complete';

// =============================================================================
// DUPLICATED FROM SERVER: WebSocket message types
// Source: WebConstants.cs (WsMessageType* constants)
// =============================================================================
export const MSG_TYPE_STATUS = 'status';
export const MSG_TYPE_MILL_STATE = 'mill:state';
export const MSG_TYPE_MILL_PROGRESS = 'mill:progress';
export const MSG_TYPE_MILL_TOOLCHANGE = 'mill:toolchange';
export const MSG_TYPE_MILL_ERROR = 'mill:error';
export const MSG_TYPE_TOOLCHANGE_STATE = 'toolchange:state';
export const MSG_TYPE_TOOLCHANGE_PROGRESS = 'toolchange:progress';
export const MSG_TYPE_TOOLCHANGE_INPUT = 'toolchange:input';
export const MSG_TYPE_TOOLCHANGE_COMPLETE = 'toolchange:complete';
export const MSG_TYPE_TOOLCHANGE_ERROR = 'toolchange:error';
export const MSG_TYPE_PROBE_ERROR = 'probe:error';
export const MSG_TYPE_CONNECTION_ERROR = 'connection:error';

// =============================================================================
// DUPLICATED FROM SERVER: WebSocket close reasons
// Source: WebConstants.cs (WsCloseReason* constants)
// =============================================================================
export const WS_CLOSE_REASON_FORCE_DISCONNECT = 'Disconnected by another client';

// =============================================================================
// DUPLICATED FROM SERVER: WebSocket commands (sent from browser)
// Source: WebConstants.cs (WsCmd* constants)
// =============================================================================
export const CMD_PING = 'ping';
export const CMD_JOG_MODE = 'jog-mode';
export const CMD_HOME = 'home';
export const CMD_UNLOCK = 'unlock';
export const CMD_RESET = 'reset';
export const CMD_GOTO_ORIGIN = 'goto-origin';
export const CMD_GOTO_CENTER = 'goto-center';
export const CMD_GOTO_SAFE = 'goto-safe';
export const CMD_GOTO_REF = 'goto-ref';
export const CMD_GOTO_Z0 = 'goto-z0';
export const CMD_PROBE_Z = 'probe-z';
export const CMD_FEEDHOLD = 'feedhold';
export const CMD_RESUME = 'resume';

// =============================================================================
// DUPLICATED FROM SERVER: API paths
// Source: WebConstants.cs (Api* constants)
// Note: These must match exactly for fetch() calls to work.
// =============================================================================
export const API_STATUS = '/api/status';
export const API_CONFIG = '/api/config';
export const API_CONSTANTS = '/api/constants';
export const API_ZERO = '/api/zero';
export const API_FILES = '/api/files';
export const API_FILE_LOAD = '/api/file/load';
export const API_FILE_UPLOAD = '/api/file/upload';
export const API_FILE_INFO = '/api/file/info';
export const API_MILL_PREFLIGHT = '/api/mill/preflight';
export const API_MILL_START = '/api/mill/start';
export const API_MILL_PAUSE = '/api/mill/pause';
export const API_MILL_RESUME = '/api/mill/resume';
export const API_MILL_STOP = '/api/mill/stop';
export const API_MILL_TOOLCHANGE_ABORT = '/api/mill/toolchange/abort';
export const API_MILL_TOOLCHANGE_INPUT = '/api/mill/toolchange/input';
export const API_MILL_DEPTH = '/api/mill/depth';
export const API_MILL_GRID = '/api/mill/grid';
export const API_FEED_INCREASE = '/api/feed-override/increase';
export const API_FEED_DECREASE = '/api/feed-override/decrease';
export const API_FEED_RESET = '/api/feed-override/reset';
export const API_PROBE_SETUP = '/api/probe/setup';
export const API_PROBE_TRACE = '/api/probe/trace';
export const API_PROBE_START = '/api/probe/start';
export const API_PROBE_PAUSE = '/api/probe/pause';
export const API_PROBE_RESUME = '/api/probe/resume';
export const API_PROBE_STOP = '/api/probe/stop';
export const API_PROBE_STATUS = '/api/probe/status';
export const API_PROBE_APPLY = '/api/probe/apply';
export const API_PROBE_SAVE = '/api/probe/save';
export const API_PROBE_LOAD = '/api/probe/load';
export const API_PROBE_FILES = '/api/probe/files';
export const API_PROBE_DISCARD = '/api/probe/discard';
export const API_SETTINGS = '/api/settings';
export const API_PROFILES = '/api/profiles';
export const API_TRUST_WORK_ZERO = '/api/trust-work-zero';
export const API_PROBE_RECOVER_AUTOSAVE = '/api/probe/recover-autosave';
export const API_FORCE_DISCONNECT = '/api/force-disconnect';

// =============================================================================
// FETCHED FROM SERVER: These are loaded at runtime via /api/config
// See state.js for the runtime values:
//   - state.jogModes: Jog speed modes with names and distances
//   - state.probeDefaults: Default margin and grid size for probing
//   - state.millGrid: Max width/height for mill grid visualization
// =============================================================================
