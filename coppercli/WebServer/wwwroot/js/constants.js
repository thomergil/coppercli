// Constants in this file have three sources.
//
// 1. JS-only: no server counterpart.
// 2. Duplicated from the server: the value must match its C# counterpart, because the
//    modules need it at load time. validateConstants compares them against /api/constants
//    at startup and logs a mismatch. Sources: WebConstants.cs, GrblProtocol.cs,
//    ControllerState.
// 3. Fetched at run time through /api/config; see the foot of this file.

// JS-only: timing.
export const MAX_RECONNECT_ATTEMPTS = 120;  // Together with RECONNECT_DELAY_MS, how long to keep trying
export const RECONNECT_DELAY_MS = 1000;
export const FORCE_DISCONNECT_RECONNECT_DELAY_MS = 5000;

// Delay reloading after takeover until the server closes the previous client's socket.
export const FORCE_DISCONNECT_RELOAD_DELAY_MS = 500;
export const WEBSOCKET_PING_INTERVAL_MS = 10000;  // Keep-alive ping; must stay under the server's WebSocketTimeoutMs
export const JOG_TOUCH_REPEAT_MS = 200;
export const DOUBLE_TAP_DELAY_MS = 300;

// How long a newly drawn prompt refuses to be answered. A run publishes its next prompt in
// the same place as soon as the last is answered, so a double-tap would answer one the
// operator has not read; this is therefore longer than DOUBLE_TAP_DELAY_MS.
export const PROMPT_SETTLE_MS = 600;

// Stands in for a prompt id when the operator releases a door hold with no run behind it.
// That release sends a cycle start too, so it settles like any other answer.
export const DOOR_RELEASE_PROMPT_ID = 'door-release';
export const TOAST_ANIMATION_DELAY_MS = 10;
export const TOAST_FADE_DURATION_MS = 300;
export const TOAST_ERROR_DURATION_MS = 5000;
export const TOAST_INFO_DURATION_MS = 3000;
export const PROBE_POLL_INTERVAL_MS = 500;
// How long the start button stays disabled after a trace stops, so a second tap on STOP
// does not start a probe.
export const TRACE_BUTTON_SETTLE_MS = 500;
// Failed status reads in a row before a trace gives up and reports the machine
// unreachable, rather than holding the screen locked.
export const TRACE_POLL_MAX_FAILURES = 20;

// JS-only: display formatting.
export const PROGRESS_PERCENT_MULTIPLIER = 100;
export const PROBE_GRID_CELL_SIZE_PX = 20;
export const BYTES_PER_KB = 1024;

// How a file size is written. Its own decimals: the position readout's are about the
// machine, and a change there must not reformat the file list.
export const FILE_SIZE_DECIMALS = 1;
export const TEXT_SIZE_BYTES = '{0} B';
export const TEXT_SIZE_KB = '{0} KB';
export const TEXT_SIZE_MB = '{0} MB';
export const BYTES_PER_MB = 1024 * 1024;

// JS-only: UI defaults.
export const PROBE_FILE_EXTENSION = '.pgrid';

// JS-only: screen ids, which are element ids in index.html.
export const SCREEN_DASHBOARD = 'dashboard-screen';
export const SCREEN_JOG = 'jog-screen';
export const SCREEN_FILE = 'file-screen';
export const SCREEN_MILL = 'mill-screen';
export const SCREEN_PROBE = 'probe-screen';
export const SCREEN_PROBE_FILES = 'probe-files-screen';
export const SCREEN_SUFFIX = '-screen';

// JS-only: CSS class names.
export const CLASS_HIDDEN = 'hidden';

// The door overlay when there is nothing to answer: a banner across the page rather than a
// layer over it.
export const CLASS_DOOR_MESSAGE = 'door-overlay-message';
export const CLASS_ACTIVE = 'active';
export const CLASS_CONNECTED = 'connected';
export const CLASS_ALARM = 'alarm';
export const CLASS_SELECTED = 'selected';
export const CLASS_PROBED = 'probed';
export const CLASS_DISABLED = 'disabled';
export const CLASS_SHOW = 'show';
export const CLASS_CONFIRM_DANGER = 'confirm-danger';
export const CLASS_STATUS_ERROR = 'status-error';
export const CLASS_STATUS_SUCCESS = 'status-success';
export const CLASS_STATUS_WARNING = 'status-warning';
export const CLASS_DISABLED_REASON = 'disabled-reason';
export const DISABLED_REASON_SELECTOR = '.' + CLASS_DISABLED_REASON;
export const CLASS_NONE = '';
export const CLASS_LOADING = 'loading';

// How the mill picture is drawn. The colors come from the stylesheet at run time; these are
// its measurements, in canvas pixels.
export const MILL_GRID_PADDING_PX = 10;
export const MILL_GRID_CELL_GAP_PX = 1;
export const MILL_MARKER_CELL_FRACTION = 3;
export const CLASS_PROBE_OPEN = 'open';
export const CLASS_PROBE_CONTACT = 'contact';
export const CLASS_CLICKABLE = 'clickable';

// JS-only: text the operator reads.
export const TEXT_DISCONNECTED = 'Disconnected';
export const TEXT_CONNECTED = 'Connected';
export const TEXT_RECONNECTING = 'Reconnecting...';
export const TEXT_LOADING = 'Loading...';
export const TEXT_LOAD = 'Load';
export const TEXT_PAUSE = 'Pause';
export const TEXT_RESUME = 'Resume';

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
export const TEXT_CONTINUE_PROBING = 'Continue';
export const TEXT_UNKNOWN = 'Unknown';
export const TEXT_NO_FILES = 'No files found';
export const TEXT_PROBING_TITLE = 'Probing';
export const TEXT_PROBING_DONE_TITLE = 'Probing Complete';
export const TEXT_MILLING_COMPLETE = 'Milling complete';
export const TEXT_CONNECTION_LOST = 'Connection lost. Refresh the page.';
export const TEXT_CONNECTION_ERROR = 'Connection error.';
export const TEXT_CONFIRM_TITLE = 'Confirm';
export const TEXT_PROBING_IN_PROGRESS = 'Probing in progress';
export const TEXT_MILLING_IN_PROGRESS = 'Milling in progress';
export const TEXT_PROBE_DATA_SAVED = 'Probe data saved';
export const TEXT_PROBE_DATA_LOADED = 'Probe data loaded';
export const TEXT_PROBE_DATA_APPLIED = ' (applied to G-code)';
export const TEXT_PROBE_DATA_CLEARED = 'Probe data cleared';
export const TEXT_SETTINGS_SAVED = 'Settings saved';
export const TEXT_FILE_UPLOADED = 'Uploaded: {0} ({1} lines)';
export const TEXT_PROBE_PIN_OPEN = 'Open';
export const TEXT_PROBE_PIN_CONTACT = 'Contact';
export const TEXT_SAVE = 'Save';
export const TEXT_SAVING = 'Saving...';
export const TEXT_SAVE_PROBE_DATA = 'Save Probe Data';
export const TEXT_LOAD_PROBE_DATA = 'Load Probe Data';
export const TEXT_DISCARD = 'Discard';
export const TEXT_CLEAR = 'Clear';
export const TEXT_ENTER_FILENAME = 'Enter a filename';
export const TEXT_WORK_ZERO_TRUSTED = 'Work origin kept';
export const TEXT_ZEROED_Z = 'Z zeroed';
export const TEXT_ZEROED_ALL = 'All axes zeroed';
// The axes line and what became of the height map, on one line.
export const TEXT_ZEROED_WITH_MAP = '{0} - {1}';

// What became of the height map, named by WorkZeroOutcome. validateConstants checks the
// names against /api/constants; the words are this screen's.
export const ZEROED_MAP_REAPPLIED = 'MapReapplied';
export const ZEROED_MAP_NOT_REAPPLIED = 'MapNotReapplied';
export const ZEROED_MAP_NOT_DISCARDED = 'MapNotDiscarded';
export const ZEROED_MAP_DISCARDED = 'MapDiscarded';
export const ZEROED_FILE_LEFT_ALONE = 'FileLeftAlone';

export const HEIGHT_MAP_TEXT_BY_OUTCOME = {
    [ZEROED_MAP_REAPPLIED]: 'height map re-applied',
    [ZEROED_MAP_NOT_REAPPLIED]: 'height map was not re-applied - reload the file before milling',
    [ZEROED_MAP_NOT_DISCARDED]: 'height map was not removed - reload the file before milling',
    [ZEROED_MAP_DISCARDED]: 'height map discarded',
    [ZEROED_FILE_LEFT_ALONE]: 'height map kept for this run'
};
export const TEXT_WORK_ZERO_NOT_TRUSTED =
    'The work origin was not kept. Set it again before probing or milling.';
export const TEXT_PROBE_RECOVERED = 'Recovered {0}/{1} probe points';
export const TEXT_GRID_SUMMARY = 'Grid: {0}x{1} = {2} points';
export const TEXT_GRID_COMPLETE = '{0} (complete)';
export const TEXT_GRID_PROGRESS = '{0} ({1} probed, {2}%)';
export const TEXT_GRID_SIZE_UNKNOWN = '?';
export const TEXT_POSITION_BRIEF = 'X:{0} Y:{1} Z:{2}';
export const TEXT_FILE_LOADED = 'Loaded: {0} ({1} lines)';
export const TEXT_LINE_COUNT = '{0} lines';
export const TEXT_POINT_COUNT = '{0}/{1} points';
export const TEXT_LINE_PROGRESS = '{0} / {1}';
// Shown when loading a file dropped the loaded height map.
export const TEXT_HEIGHT_MAP_DROPPED =
    'Height map discarded - {0}. Probe again before milling.';
export const TEXT_ZERO_TITLE = 'Zero';
export const TEXT_PROBE_STATE_READY = 'an unmeasured';
export const TEXT_PROBE_STATE_PARTIAL = 'a partly measured';
export const TEXT_PROBE_STATE_COMPLETE = 'a complete';
export const TEXT_ZERO_XY_INVALIDATES =
    'You have {0} height map for this board, measured from the current X/Y origin. '
    + 'Zeroing {1} deletes it and the saved copy, and you will have to probe again. '
    + 'Zeroing only Z keeps it. Continue?';
// The axes this warning names in the browser, which only ever zeroes both.
export const TEXT_ZERO_AXES_XY = 'X/Y';
export const TEXT_RECOVERY_FAILED = 'Could not recover the height map';
export const TEXT_SETUP_FAILED = 'Could not set up the grid';
export const TEXT_TRACE_FAILED = 'The outline trace did not finish';
export const TEXT_APPLY_FAILED = 'Could not apply the probe data';
export const TEXT_PROBE_APPLIED_TO_GCODE = 'Probe data applied to G-code';
export const TEXT_DISCARD_FAILED = 'Could not discard the probe data';
export const TEXT_DISCARD_CONFIRM = 'Discard all probe data?';
export const TEXT_DISCARD_TITLE = 'Discard Probe Data';
export const TEXT_SOURCE_GCODE_MISSING = 'The G-code this map was measured for is missing.';
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
export const ERROR_LOST_CONTACT = 'Lost contact with the machine. Check it.';
export const TEXT_NOT_LOADED = '[not loaded]';
export const TEXT_FORCE_DISCONNECT_CONFIRM = 'Another client is connected. Take over?';
export const TEXT_FORCE_DISCONNECT_FAILED = 'Could not disconnect the other client';
export const TITLE_FORCE_DISCONNECT = 'Take over the machine';

// Duplicated from GetSharedConstants() in CncWebServer.cs.
export const POSITION_DECIMALS_BRIEF = 1;
export const POSITION_DECIMALS_FULL = 3;

// Duplicated from Constants.cs (HeightRangeEpsilon, MillMinRangeThreshold).
export const HEIGHT_RANGE_EPSILON = 0.0001;  // Minimum height range for color gradient
export const MILL_MIN_RANGE_THRESHOLD = 0.001;

// Duplicated from the MachineActivity enum in coppercli.Core/Controllers/MachineActivity.cs.
export const MACHINE_ACTIVITY_DOOR_OPEN = 'DoorOpen';
export const MACHINE_ACTIVITY_DOOR_HOLDING = 'DoorHolding';
export const MACHINE_ACTIVITY_DOOR_RESUMING = 'DoorResuming';

// Duplicated from the ControllerState enum in coppercli.Core/Controllers/ControllerState.cs.
export const CONTROLLER_STATE_IDLE = 'Idle';
export const CONTROLLER_STATE_INITIALIZING = 'Initializing';
export const CONTROLLER_STATE_RUNNING = 'Running';
export const CONTROLLER_STATE_PAUSED = 'Paused';
export const CONTROLLER_STATE_WAITING_FOR_USER_INPUT = 'WaitingForUserInput';
export const CONTROLLER_STATE_COMPLETING = 'Completing';
export const CONTROLLER_STATE_COMPLETED = 'Completed';
export const CONTROLLER_STATE_FAILED = 'Failed';
export const CONTROLLER_STATE_CANCELLED = 'Cancelled';

// The workflow phases this UI changes its display for, duplicated from GetSharedConstants()
// in CncWebServer.cs (MillingPhase, ProbePhase, ToolChangePhase). Every other phase is shown
// as it arrives, so only these must agree.
export const PHASE_MILLING = 'Milling';
export const PHASE_TRACING_OUTLINE = 'TracingOutline';
export const PHASE_WAITING_FOR_ZERO_Z = 'WaitingForZeroZ';

// The answer that continues a prompt. The server rejects any answer that is not one of the
// options it offered, so both sides use the same string.
export const PROMPT_OPTION_CONTINUE = 'Continue';
export const PROMPT_OPTION_ABORT = 'Abort';

// GRBL reports Door in three cases: enclosure open, closed and parked, and restoring after
// a cycle start. The header uses these strings for those three and GRBL's own word for every
// other state.
export const TEXT_DOOR_OPEN = 'Door open';
export const TEXT_DOOR_HOLDING = 'Door closed - machine holding';
export const TEXT_DOOR_RESUMING = 'Door closed - machine resuming';

export const HEADER_TEXT_BY_ACTIVITY = {
    [MACHINE_ACTIVITY_DOOR_OPEN]: TEXT_DOOR_OPEN,
    [MACHINE_ACTIVITY_DOOR_HOLDING]: TEXT_DOOR_HOLDING,
    [MACHINE_ACTIVITY_DOOR_RESUMING]: TEXT_DOOR_RESUMING
};

// Shown when a request never reaches coppercli. The browser's own exception text names
// nothing an operator standing at the machine can act on, and the machine may still be
// moving, so these say what to do instead.
export const ERROR_STOP_NOT_SENT =
    'Could not reach coppercli to stop the job. The machine may still be moving - check it directly.';
export const ERROR_ABORT_NOT_SENT =
    'Could not reach coppercli to abort. The machine may still be moving - check it directly.';
export const ERROR_START_NOT_SENT =
    'Could not reach coppercli. The job did not start.';
export const ERROR_PAUSE_NOT_SENT =
    'Could not reach coppercli. The job is still running - check the machine.';
export const ERROR_INPUT_NOT_SENT =
    'Could not reach coppercli. The job is still waiting - try again.';
export const ERROR_PROBE_NOT_STARTED = 'Could not start probing. Check the connection.';
export const ERROR_NOTHING_TO_ANSWER = 'Nothing is waiting for an answer.';
export const ERROR_RESUME_NOT_SENT = 'Could not reach coppercli. Check the machine.';
export const ERROR_FEED_NOT_SENT =
    'Could not reach coppercli. The feed rate is unchanged - check the machine.';
export const ERROR_DEPTH_NOT_SET =
    'Could not reach coppercli. The cut depth is unchanged.';

export const ERROR_DOOR_NOT_RELEASED = ERROR_RESUME_NOT_SENT;

export const ERROR_ZERO_NOT_SENT = 'Could not reach coppercli. Work zero is not set.';

// Mirrors WebConstants.cs; validateConstants checks them against /api/constants at startup.
// What each state means is defined once at the top of
// coppercli.Core/Controllers/ProbeController.cs.
export const PROBE_STATE_NONE = 'none';
export const PROBE_STATE_READY = 'ready';
export const PROBE_STATE_PARTIAL = 'partial';
export const PROBE_STATE_COMPLETE = 'complete';

// How the warning before an X or Y zero describes the map it would discard. One entry per
// state, so a state with no words is a missing key rather than the wrong sentence.
export const MAP_DESCRIPTION_BY_STATE = {
    [PROBE_STATE_READY]: TEXT_PROBE_STATE_READY,
    [PROBE_STATE_PARTIAL]: TEXT_PROBE_STATE_PARTIAL,
    [PROBE_STATE_COMPLETE]: TEXT_PROBE_STATE_COMPLETE
};

// What the depth buttons send. Mirrors WebConstants.cs DepthAction*.
export const DEPTH_ACTION_INCREASE = 'increase';
export const DEPTH_ACTION_DECREASE = 'decrease';
export const DEPTH_ACTION_RESET = 'reset';

// How the socket is addressed and how the browser names itself on it. Mirrors
// WebConstants.cs WsPath, QueryParamClientId and ClientIdCookieName.
export const WS_PATH = '/ws';
export const WS_QUERY_PARAM_CLIENT_ID = 'clientId';
export const CLIENT_ID_COOKIE_NAME = 'coppercli_client_id';

// Duplicated from WebConstants.cs (WsMessageType*).
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

// Duplicated from WebConstants.cs (WsCloseReason*).
export const WS_CLOSE_REASON_FORCE_DISCONNECT = 'Disconnected by another client';

// Duplicated from WebConstants.cs (WsCmd*), sent from the browser.
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

// Duplicated from WebConstants.cs (Api*). A path that does not match exactly reaches no
// handler.
export const API_STATUS = '/api/status';
export const API_CONFIG = '/api/config';
export const API_CONSTANTS = '/api/constants';
export const API_ZERO = '/api/zero';
export const API_RESUME = '/api/resume';
export const API_DOOR_RELEASE = '/api/door/release';
export const API_FILES = '/api/files';
export const API_FILE_LOAD = '/api/file/load';
export const API_FILE_UPLOAD = '/api/file/upload';
export const API_FILE_INFO = '/api/file/info';
export const API_MILL_CAN_START = '/api/mill/can-start';
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

// Fetched at run time through /api/config and held in state.js: jogModes (names and
// distances), probeDefaults (margin and grid size), millGrid (the mill picture's largest
// width and height).
