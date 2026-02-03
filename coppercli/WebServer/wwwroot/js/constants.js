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
export const TOAST_ANIMATION_DELAY_MS = 10;
export const TOAST_FADE_DURATION_MS = 300;
export const TOAST_ERROR_DURATION_MS = 5000;
export const TOAST_INFO_DURATION_MS = 3000;
export const PROBE_POLL_INTERVAL_MS = 500;
export const ERROR_OTHER_CLIENT_SUBSTRING = 'another client';  // Substring to detect other client connection error

// =============================================================================
// JS-ONLY: Display formatting (no server counterpart)
// =============================================================================
export const PROGRESS_PERCENT_MULTIPLIER = 100;
export const PROBE_GRID_CELL_SIZE_PX = 20;
export const BYTES_PER_KB = 1024;
export const BYTES_PER_MB = 1024 * 1024;
export const COLOR_GRADIENT_STEP = 0.25;
export const COLOR_MAX_VALUE = 255;

// =============================================================================
// JS-ONLY: UI defaults (no server counterpart)
// =============================================================================
export const DEFAULT_JOG_MODE_INDEX = 1;  // Normal mode
export const PROBE_FILE_EXTENSION = '.pgrid';

// =============================================================================
// JS-ONLY: Screen IDs (HTML element IDs)
// =============================================================================
export const SCREEN_DASHBOARD = 'dashboard-screen';
export const SCREEN_JOG = 'jog-screen';
export const SCREEN_FILE = 'file-screen';
export const SCREEN_MILL = 'mill-screen';
export const SCREEN_PROBE = 'probe-screen';
export const SCREEN_SETTINGS = 'settings-screen';
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
export const TEXT_PROBE_RECOVERY_AVAILABLE = 'Autosaved probe data available';
export const TEXT_SETTINGS_SAVED = 'Settings saved';
export const TEXT_NO_PROBE_DATA = 'No probe data to save';
export const TEXT_FILE_UPLOADED = 'File uploaded';
export const TEXT_PROBE_PIN_OPEN = 'Open';
export const TEXT_PROBE_PIN_CONTACT = 'Contact';
export const TEXT_TOOL_CHANGE = 'Tool Change Required';
export const TEXT_TOOL_CHANGE_INSTRUCTIONS = 'Change tool and probe, then press Continue';
export const TEXT_DEPTH_ADJUSTMENT = 'Depth Adjustment';
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
export const CONTROLLER_STATE_COMPLETING = 'Completing';
export const CONTROLLER_STATE_COMPLETED = 'Completed';
export const CONTROLLER_STATE_FAILED = 'Failed';
export const CONTROLLER_STATE_CANCELLED = 'Cancelled';

// =============================================================================
// DUPLICATED FROM SERVER: Probe states (4-state model)
// Source: WebConstants.cs (ProbeStateNone, ProbeStateReady, etc.)
// =============================================================================
export const PROBE_STATE_NONE = 'none';         // no grid
export const PROBE_STATE_READY = 'ready';       // grid exists, progress=0
export const PROBE_STATE_PARTIAL = 'partial';   // 0 < progress < total
export const PROBE_STATE_COMPLETE = 'complete'; // progress = total

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
export const CMD_JOG_MODE = 'jog-mode';
export const CMD_HOME = 'home';
export const CMD_UNLOCK = 'unlock';
export const CMD_RESET = 'reset';
export const CMD_ZERO = 'zero';
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
export const API_FILES = '/api/files';
export const API_FILE_LOAD = '/api/file/load';
export const API_FILE_UPLOAD = '/api/file/upload';
export const API_FILE_INFO = '/api/file/info';
export const API_MILL_PREFLIGHT = '/api/mill/preflight';
export const API_MILL_START = '/api/mill/start';
export const API_MILL_PAUSE = '/api/mill/pause';
export const API_MILL_RESUME = '/api/mill/resume';
export const API_MILL_STOP = '/api/mill/stop';
export const API_MILL_TOOLCHANGE_CONTINUE = '/api/mill/toolchange/continue';
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
export const API_PROBE_RECOVER = '/api/probe/recover';
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
