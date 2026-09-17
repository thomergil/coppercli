// coppercli Web UI DOM Helpers

import { state } from './state.js';
import {
    JOG_TOUCH_REPEAT_MS,
    DOUBLE_TAP_DELAY_MS,
    TOAST_ANIMATION_DELAY_MS,
    TOAST_FADE_DURATION_MS,
    TOAST_ERROR_DURATION_MS,
    TOAST_INFO_DURATION_MS,
    CLASS_SHOW,
    API_CONSTANTS,
    PROMPT_OPTION_CONTINUE,
    PROMPT_OPTION_ABORT,
    // Duplicated constants (validated against server)
    PROBE_STATE_NONE,
    PROBE_STATE_READY,
    PROBE_STATE_PARTIAL,
    PROBE_STATE_COMPLETE,
    DEPTH_ACTION_INCREASE,
    DEPTH_ACTION_DECREASE,
    DEPTH_ACTION_RESET,
    WS_PATH,
    WS_QUERY_PARAM_CLIENT_ID,
    CLIENT_ID_COOKIE_NAME,
    WEBSOCKET_PING_INTERVAL_MS,
    POSITION_DECIMALS_BRIEF,
    POSITION_DECIMALS_FULL,
    HEIGHT_RANGE_EPSILON,
    MILL_MIN_RANGE_THRESHOLD,
    MACHINE_ACTIVITY_DOOR_OPEN,
    MACHINE_ACTIVITY_DOOR_HOLDING,
    MACHINE_ACTIVITY_DOOR_RESUMING,
    CONTROLLER_STATE_IDLE,
    CONTROLLER_STATE_INITIALIZING,
    CONTROLLER_STATE_RUNNING,
    CONTROLLER_STATE_PAUSED,
    CONTROLLER_STATE_WAITING_FOR_USER_INPUT,
    CONTROLLER_STATE_COMPLETING,
    CONTROLLER_STATE_COMPLETED,
    CONTROLLER_STATE_FAILED,
    CONTROLLER_STATE_CANCELLED,
    PHASE_MILLING,
    PHASE_TRACING_OUTLINE,
    PHASE_WAITING_FOR_ZERO_Z,
    CMD_PING,
    CMD_JOG_MODE,
    CMD_HOME,
    CMD_UNLOCK,
    CMD_RESET,
    CMD_FEEDHOLD,
    CMD_RESUME,
    CMD_GOTO_ORIGIN,
    CMD_GOTO_CENTER,
    CMD_GOTO_SAFE,
    CMD_GOTO_REF,
    CMD_GOTO_Z0,
    CMD_PROBE_Z,
    MSG_TYPE_STATUS,
    MSG_TYPE_MILL_STATE,
    MSG_TYPE_MILL_PROGRESS,
    MSG_TYPE_MILL_TOOLCHANGE,
    MSG_TYPE_MILL_ERROR,
    MSG_TYPE_TOOLCHANGE_STATE,
    MSG_TYPE_TOOLCHANGE_PROGRESS,
    MSG_TYPE_TOOLCHANGE_INPUT,
    MSG_TYPE_TOOLCHANGE_COMPLETE,
    MSG_TYPE_TOOLCHANGE_ERROR,
    MSG_TYPE_PROBE_ERROR,
    MSG_TYPE_CONNECTION_ERROR,
    WS_CLOSE_REASON_FORCE_DISCONNECT,
    TEXT_PAUSE,
    TEXT_RESUME,
    ICON_PAUSE,
    ICON_RESUME,
    TEXT_NO_FILES,
    CLASS_SELECTED,
    CLASS_HIDDEN,
    CLASS_CONFIRM_DANGER,
    CLASS_LOADING,
    TEXT_CONFIRM_TITLE,
    TEXT_ZERO_XY_INVALIDATES,
    TEXT_PROBE_STATE_READY,
    TEXT_PROBE_STATE_PARTIAL,
    TEXT_PROBE_STATE_COMPLETE,
    ZEROED_MAP_REAPPLIED,
    ZEROED_MAP_NOT_REAPPLIED,
    ZEROED_MAP_NOT_DISCARDED,
    PROBE_FILE_EXTENSION,
    ZEROED_MAP_DISCARDED,
    ZEROED_FILE_LEFT_ALONE,
} from './constants.js';

// Get element by ID with null safety
/**
 * Fill a `{0}`-style template. The text lives in constants.js; this puts the values in, so
 * no module writes a sentence at the point of use.
 */
export function format(template, ...values) {
    return values.reduce((text, value, i) => text.split(`{${i}}`).join(String(value)), template);
}

export function $(id) {
    return document.getElementById(id);
}

// Set text content safely
export function setText(id, text) {
    const el = $(id);
    if (el) el.textContent = text;
}

// Toggle class on element
export function toggleClass(id, className, enabled) {
    const el = $(id);
    if (el) el.classList.toggle(className, enabled);
}

// Add class to element
export function addClass(id, className) {
    const el = $(id);
    if (el) el.classList.add(className);
}

// Remove class from element
export function removeClass(id, className) {
    const el = $(id);
    if (el) el.classList.remove(className);
}

// Update pause/resume button state (sets data-paused, innerHTML, and optional classes)
export function updatePauseButton(btn, isPaused, pauseClass = null, resumeClass = null) {
    if (!btn) return;
    btn.dataset.paused = isPaused;
    btn.innerHTML = isPaused ? `${ICON_RESUME} ${TEXT_RESUME}` : `${ICON_PAUSE} ${TEXT_PAUSE}`;
    if (pauseClass && resumeClass) {
        btn.classList.toggle(pauseClass, !isPaused);
        btn.classList.toggle(resumeClass, isPaused);
    }
}

/**
 * POST to the server and report whether it succeeded. A refusal the server explained comes
 * back as `error`; a request that never completed does not, because its exception text is
 * for the log.
 */
export async function postJson(url, body = null) {
    try {
        const options = body === null
            ? { method: 'POST' }
            : {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(body)
            };
        const response = await fetch(url, options);
        const data = await response.json();
        return { ok: response.ok && data.success !== false, error: data.error, data };
    } catch (err) {
        console.error(`POST ${url} failed`, err);
        return { ok: false, error: null, data: {} };
    }
}

// Check if WebSocket is ready
export function isWsReady() {
    return state.ws && state.ws.readyState === WebSocket.OPEN;
}

// Add touch-repeat behavior to a button (for jog buttons)
export function addTouchRepeat(btn, action) {
    let interval = null;
    const clear = () => {
        if (interval) clearInterval(interval);
        interval = null;
    };

    btn.addEventListener('touchstart', (e) => {
        e.preventDefault();
        action();
        interval = setInterval(action, JOG_TOUCH_REPEAT_MS);
    });
    btn.addEventListener('touchend', clear);
    btn.addEventListener('touchcancel', clear);
}

/**
 * Text from the filesystem, ready to put inside markup. A name carrying a quote would
 * otherwise end the attribute it sits in, and the file becomes unloadable; an angle bracket
 * corrupts the rest of the list.
 */
export function escapeMarkup(text) {
    return String(text)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

/**
 * Show `busyText` on a button while something runs, and put the button back afterwards.
 * Restores the markup rather than the word: these buttons carry an icon, and writing
 * textContent removes it for good.
 */
export function whileBusy(btn, busyText) {
    if (!btn) {
        return () => { };
    }

    const wasShowing = btn.innerHTML;
    btn.disabled = true;
    btn.textContent = busyText;

    return () => {
        btn.disabled = false;
        btn.innerHTML = wasShowing;
    };
}

// Toast notifications (DRY helper)
function showToast(message, type, duration) {
    const toast = document.createElement('div');
    toast.className = `toast ${type}`;
    toast.textContent = message;
    document.body.appendChild(toast);

    setTimeout(() => toast.classList.add(CLASS_SHOW), TOAST_ANIMATION_DELAY_MS);
    setTimeout(() => {
        toast.classList.remove(CLASS_SHOW);
        setTimeout(() => toast.remove(), TOAST_FADE_DURATION_MS);
    }, duration);
}

export function showError(message) {
    showToast(message, 'error', TOAST_ERROR_DURATION_MS);
}

export function showInfo(message) {
    showToast(message, 'info', TOAST_INFO_DURATION_MS);
}

/** The question currently on screen, so a second one does not strand it. */
let pendingConfirm = null;

// Confirm dialog (replaces browser confirm())
// Returns a Promise that resolves to true (yes) or false (no)
// Options: { danger: true } adds warning styling (red text, warning icon)
export function showConfirm(message, title = TEXT_CONFIRM_TITLE, options = {}) {
    return new Promise((resolve) => {
        // One modal and one pair of buttons, so a second question overwrites the first's
        // handlers. Answered false first, or the first promise never settles.
        if (pendingConfirm) {
            const stranded = pendingConfirm;
            pendingConfirm = null;
            stranded(false);
        }
        pendingConfirm = resolve;

        const modal = $('confirm-modal');
        const titleEl = $('confirm-title');
        const messageEl = $('confirm-message');
        const yesBtn = $('confirm-yes-btn');
        const noBtn = $('confirm-no-btn');

        // Apply danger styling if requested
        titleEl.textContent = title;
        if (options.danger) {
            messageEl.innerHTML = '⚠️ ' + escapeMarkup(message);
            messageEl.classList.add(CLASS_CONFIRM_DANGER);
        } else {
            messageEl.textContent = message;
            messageEl.classList.remove(CLASS_CONFIRM_DANGER);
        }

        const cleanup = () => {
            pendingConfirm = null;
            modal.classList.add(CLASS_HIDDEN);
            messageEl.classList.remove(CLASS_CONFIRM_DANGER);
            yesBtn.onclick = null;
            noBtn.onclick = null;
        };

        yesBtn.onclick = () => { cleanup(); resolve(true); };
        noBtn.onclick = () => { cleanup(); resolve(false); };

        modal.classList.remove(CLASS_HIDDEN);
    });
}

/**
 * Shared file browser component. Handles rendering, navigation, selection, and double-tap.
 * Used by both G-code file browser and probe file browser.
 */
export class FileBrowser {
    /**
     * @param {Object} config
     * @param {string} config.listElementId - ID of the list container element
     * @param {string} config.pathElementId - ID of the path display element
     * @param {string} config.apiEndpoint - API endpoint for fetching files
     * @param {string} config.fileIcon - Icon for files (default: '📄')
     * @param {string} config.metaField - Which field to show as meta ('size' or 'modified')
     * @param {Function} config.onFileSelect - Called when file is selected (path)
     * @param {Function} config.onFileLoad - Called on double-tap to load file (path)
     * @param {Function} [config.formatMeta] - Optional formatter for meta field
     */
    constructor(config) {
        this.listEl = $(config.listElementId);
        this.pathEl = $(config.pathElementId);
        this.apiEndpoint = config.apiEndpoint;
        this.fileIcon = config.fileIcon || '📄';
        this.metaField = config.metaField || 'size';
        this.onFileSelect = config.onFileSelect;
        this.onFileLoad = config.onFileLoad;
        this.formatMeta = config.formatMeta || (v => v);
        this.currentPath = null;
        this.selectedFile = null;
        this._lastTapTime = 0;
        this._lastTapItem = null;
    }

    async load(path) {
        const url = path
            ? `${this.apiEndpoint}?path=${encodeURIComponent(path)}`
            : this.apiEndpoint;
        try {
            const response = await fetch(url);
            const data = await response.json();
            this.render(data);
        } catch (err) {
            console.error('Failed to load files:', err);
        }
    }

    render(data) {
        if (this.pathEl) {
            this.pathEl.textContent = data.currentPath || '/';
        }
        this.currentPath = data.currentPath;
        this.selectedFile = null;

        if (!data.entries || data.entries.length === 0) {
            this.listEl.innerHTML = `<div class="${CLASS_LOADING}">${TEXT_NO_FILES}</div>`;
            return;
        }

        this.listEl.innerHTML = data.entries.map(entry => `
            <div class="file-item" data-path="${escapeMarkup(entry.path)}" data-isdir="${entry.isDir === true}">
                <span class="file-icon">${entry.isDir ? '📁' : this.fileIcon}</span>
                <span class="file-name">${escapeMarkup(entry.name)}</span>
                ${entry[this.metaField] ? `<span class="file-meta">${escapeMarkup(this.formatMeta(entry[this.metaField]))}</span>` : ''}
            </div>
        `).join('');

        this._setupClickHandlers();
    }

    _setupClickHandlers() {
        this._lastTapTime = 0;
        this._lastTapItem = null;

        this.listEl.querySelectorAll('.file-item').forEach(item => {
            item.addEventListener('click', (e) => this._handleClick(e, item));

            // dblclick for desktop browsers
            if (item.dataset.isdir !== 'true') {
                item.addEventListener('dblclick', (e) => this._handleFileLoad(e, item));
            }
        });
    }

    // Handle file load action (dblclick on desktop, double-tap on touch)
    _handleFileLoad(e, item) {
        e.preventDefault();
        this._selectItem(item);
        if (this.onFileLoad) {
            this.onFileLoad(this.selectedFile);
        }
    }

    _handleClick(e, item) {
        const isFile = item.dataset.isdir !== 'true';
        const now = Date.now();

        // Double-tap detection for touch devices
        if (isFile && this._lastTapItem === item && (now - this._lastTapTime) < DOUBLE_TAP_DELAY_MS) {
            this._handleFileLoad(e, item);
            this._lastTapTime = 0;
            this._lastTapItem = null;
        } else {
            // Single click
            if (item.dataset.isdir === 'true') {
                this.load(item.dataset.path);
            } else {
                this._selectItem(item);
                if (this.onFileSelect) {
                    this.onFileSelect(this.selectedFile);
                }
            }
            if (isFile) {
                this._lastTapTime = now;
                this._lastTapItem = item;
            }
        }
    }

    _selectItem(item) {
        this.listEl.querySelectorAll('.file-item').forEach(i => i.classList.remove(CLASS_SELECTED));
        item.classList.add(CLASS_SELECTED);
        this.selectedFile = item.dataset.path;
    }

    getSelectedFile() {
        return this.selectedFile;
    }
}

/**
 * Validate that duplicated JS constants match server values.
 * Called during app initialization to catch mismatches early.
 * Only logs warnings - doesn't break functionality.
 */
export async function validateConstants() {
    try {
        const response = await fetch(API_CONSTANTS);
        const server = await response.json();

        const mismatches = [];

        // Helper to check and report mismatch
        const check = (jsValue, serverValue, name) => {
            if (jsValue !== serverValue) {
                mismatches.push(`${name}: JS="${jsValue}" server="${serverValue}"`);
            }
        };

        // Probe states
        if (server.probeStates) {
            check(PROBE_STATE_NONE, server.probeStates.none, 'PROBE_STATE_NONE');
            check(PROBE_STATE_READY, server.probeStates.ready, 'PROBE_STATE_READY');
            check(PROBE_STATE_PARTIAL, server.probeStates.partial, 'PROBE_STATE_PARTIAL');
            check(PROBE_STATE_COMPLETE, server.probeStates.complete, 'PROBE_STATE_COMPLETE');
        }

        // The socket's own address, and the ping interval that has to stay inside the
        // server's idle timeout.
        if (server.socket) {
            check(WS_PATH, server.socket.path, 'WS_PATH');
            check(WS_QUERY_PARAM_CLIENT_ID, server.socket.clientIdParam, 'WS_QUERY_PARAM_CLIENT_ID');
            check(CLIENT_ID_COOKIE_NAME, server.socket.clientIdCookie, 'CLIENT_ID_COOKIE_NAME');

            if (WEBSOCKET_PING_INTERVAL_MS >= server.socket.timeoutMs) {
                mismatches.push(
                    `WEBSOCKET_PING_INTERVAL_MS=${WEBSOCKET_PING_INTERVAL_MS} is not under the `
                    + `server's socket timeout of ${server.socket.timeoutMs}ms`);
            }
        }

        // Depth adjustment actions
        if (server.depthActions) {
            check(DEPTH_ACTION_INCREASE, server.depthActions.increase, 'DEPTH_ACTION_INCREASE');
            check(DEPTH_ACTION_DECREASE, server.depthActions.decrease, 'DEPTH_ACTION_DECREASE');
            check(DEPTH_ACTION_RESET, server.depthActions.reset, 'DEPTH_ACTION_RESET');
        }

        // Display decimals
        if (server.decimals) {
            check(POSITION_DECIMALS_BRIEF, server.decimals.brief, 'POSITION_DECIMALS_BRIEF');
            check(POSITION_DECIMALS_FULL, server.decimals.full, 'POSITION_DECIMALS_FULL');
        }

        // Visualization thresholds
        if (server.thresholds) {
            check(HEIGHT_RANGE_EPSILON, server.thresholds.heightRangeEpsilon, 'HEIGHT_RANGE_EPSILON');
            check(MILL_MIN_RANGE_THRESHOLD, server.thresholds.millMinRange, 'MILL_MIN_RANGE_THRESHOLD');
        }

        if (server.probeGridExtension) {
            check(PROBE_FILE_EXTENSION, server.probeGridExtension, 'PROBE_FILE_EXTENSION');
        }

        if (server.zeroWarning) {
            check(TEXT_ZERO_XY_INVALIDATES, server.zeroWarning.discardsMap,
                'TEXT_ZERO_XY_INVALIDATES');
            check(TEXT_PROBE_STATE_READY, server.zeroWarning.unmeasured,
                'TEXT_PROBE_STATE_READY');
            check(TEXT_PROBE_STATE_PARTIAL, server.zeroWarning.partlyMeasured,
                'TEXT_PROBE_STATE_PARTIAL');
            check(TEXT_PROBE_STATE_COMPLETE, server.zeroWarning.complete,
                'TEXT_PROBE_STATE_COMPLETE');
        }

        if (server.heightMapOutcomes) {
            check(ZEROED_MAP_REAPPLIED, server.heightMapOutcomes.reapplied, 'ZEROED_MAP_REAPPLIED');
            check(ZEROED_MAP_NOT_REAPPLIED, server.heightMapOutcomes.notReapplied,
                'ZEROED_MAP_NOT_REAPPLIED');
            check(ZEROED_MAP_NOT_DISCARDED, server.heightMapOutcomes.notDiscarded,
                'ZEROED_MAP_NOT_DISCARDED');
            check(ZEROED_MAP_DISCARDED, server.heightMapOutcomes.discarded, 'ZEROED_MAP_DISCARDED');
            check(ZEROED_FILE_LEFT_ALONE, server.heightMapOutcomes.fileLeftAlone,
                'ZEROED_FILE_LEFT_ALONE');
        }

        // What the machine is doing
        if (server.machineActivities) {
            check(MACHINE_ACTIVITY_DOOR_OPEN, server.machineActivities.doorOpen, 'MACHINE_ACTIVITY_DOOR_OPEN');
            check(MACHINE_ACTIVITY_DOOR_HOLDING, server.machineActivities.doorHolding,
                'MACHINE_ACTIVITY_DOOR_HOLDING');
            check(MACHINE_ACTIVITY_DOOR_RESUMING, server.machineActivities.doorResuming,
                'MACHINE_ACTIVITY_DOOR_RESUMING');
        }

        // Controller states
        if (server.controllerStates) {
            check(CONTROLLER_STATE_IDLE, server.controllerStates.idle, 'CONTROLLER_STATE_IDLE');
            check(CONTROLLER_STATE_INITIALIZING, server.controllerStates.initializing, 'CONTROLLER_STATE_INITIALIZING');
            check(CONTROLLER_STATE_RUNNING, server.controllerStates.running, 'CONTROLLER_STATE_RUNNING');
            check(CONTROLLER_STATE_PAUSED, server.controllerStates.paused, 'CONTROLLER_STATE_PAUSED');
            check(CONTROLLER_STATE_WAITING_FOR_USER_INPUT, server.controllerStates.waitingForUserInput,
                'CONTROLLER_STATE_WAITING_FOR_USER_INPUT');
            check(CONTROLLER_STATE_COMPLETING, server.controllerStates.completing, 'CONTROLLER_STATE_COMPLETING');
            check(CONTROLLER_STATE_COMPLETED, server.controllerStates.completed, 'CONTROLLER_STATE_COMPLETED');
            check(CONTROLLER_STATE_FAILED, server.controllerStates.failed, 'CONTROLLER_STATE_FAILED');
            check(CONTROLLER_STATE_CANCELLED, server.controllerStates.cancelled, 'CONTROLLER_STATE_CANCELLED');
        }

        if (server.promptOptions) {
            check(PROMPT_OPTION_CONTINUE, server.promptOptions.carryOn, 'PROMPT_OPTION_CONTINUE');
            check(PROMPT_OPTION_ABORT, server.promptOptions.abandon, 'PROMPT_OPTION_ABORT');
        }

        // Workflow phases
        if (server.phases) {
            check(PHASE_MILLING, server.phases.milling, 'PHASE_MILLING');
            check(PHASE_TRACING_OUTLINE, server.phases.tracingOutline, 'PHASE_TRACING_OUTLINE');
            check(PHASE_WAITING_FOR_ZERO_Z, server.phases.waitingForZeroZ, 'PHASE_WAITING_FOR_ZERO_Z');
        }

        // WebSocket commands
        if (server.commands) {
            check(CMD_PING, server.commands.ping, 'CMD_PING');
            check(CMD_JOG_MODE, server.commands.jogMode, 'CMD_JOG_MODE');
            check(CMD_HOME, server.commands.home, 'CMD_HOME');
            check(CMD_UNLOCK, server.commands.unlock, 'CMD_UNLOCK');
            check(CMD_RESET, server.commands.reset, 'CMD_RESET');
            check(CMD_FEEDHOLD, server.commands.feedhold, 'CMD_FEEDHOLD');
            check(CMD_RESUME, server.commands.resume, 'CMD_RESUME');
            check(CMD_GOTO_ORIGIN, server.commands.gotoOrigin, 'CMD_GOTO_ORIGIN');
            check(CMD_GOTO_CENTER, server.commands.gotoCenter, 'CMD_GOTO_CENTER');
            check(CMD_GOTO_SAFE, server.commands.gotoSafe, 'CMD_GOTO_SAFE');
            check(CMD_GOTO_REF, server.commands.gotoRef, 'CMD_GOTO_REF');
            check(CMD_GOTO_Z0, server.commands.gotoZ0, 'CMD_GOTO_Z0');
            check(CMD_PROBE_Z, server.commands.probeZ, 'CMD_PROBE_Z');
        }

        // WebSocket message types
        if (server.wsMessageTypes) {
            check(MSG_TYPE_STATUS, server.wsMessageTypes.status, 'MSG_TYPE_STATUS');
            check(MSG_TYPE_MILL_STATE, server.wsMessageTypes.millState, 'MSG_TYPE_MILL_STATE');
            check(MSG_TYPE_MILL_PROGRESS, server.wsMessageTypes.millProgress, 'MSG_TYPE_MILL_PROGRESS');
            check(MSG_TYPE_MILL_TOOLCHANGE, server.wsMessageTypes.millToolChange, 'MSG_TYPE_MILL_TOOLCHANGE');
            check(MSG_TYPE_MILL_ERROR, server.wsMessageTypes.millError, 'MSG_TYPE_MILL_ERROR');
            check(MSG_TYPE_TOOLCHANGE_STATE, server.wsMessageTypes.toolChangeState, 'MSG_TYPE_TOOLCHANGE_STATE');
            check(MSG_TYPE_TOOLCHANGE_PROGRESS, server.wsMessageTypes.toolChangeProgress, 'MSG_TYPE_TOOLCHANGE_PROGRESS');
            check(MSG_TYPE_TOOLCHANGE_INPUT, server.wsMessageTypes.toolChangeInput, 'MSG_TYPE_TOOLCHANGE_INPUT');
            check(MSG_TYPE_TOOLCHANGE_COMPLETE, server.wsMessageTypes.toolChangeComplete, 'MSG_TYPE_TOOLCHANGE_COMPLETE');
            check(MSG_TYPE_TOOLCHANGE_ERROR, server.wsMessageTypes.toolChangeError, 'MSG_TYPE_TOOLCHANGE_ERROR');
            check(MSG_TYPE_PROBE_ERROR, server.wsMessageTypes.probeError, 'MSG_TYPE_PROBE_ERROR');
            check(MSG_TYPE_CONNECTION_ERROR, server.wsMessageTypes.connectionError, 'MSG_TYPE_CONNECTION_ERROR');
        }

        // WebSocket close reasons
        if (server.wsCloseReasons) {
            check(WS_CLOSE_REASON_FORCE_DISCONNECT, server.wsCloseReasons.forceDisconnect, 'WS_CLOSE_REASON_FORCE_DISCONNECT');
        }

        if (mismatches.length > 0) {
            console.warn('JS/Server constant mismatches detected:');
            mismatches.forEach(m => console.warn('  ' + m));
        }
    } catch (err) {
        // Non-fatal - constants validation is optional
        console.debug('Could not validate constants:', err.message);
    }
}
