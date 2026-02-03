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
    // Duplicated constants (validated against server)
    PROBE_STATE_NONE,
    PROBE_STATE_READY,
    PROBE_STATE_PARTIAL,
    PROBE_STATE_COMPLETE,
    POSITION_DECIMALS_BRIEF,
    POSITION_DECIMALS_FULL,
    HEIGHT_RANGE_EPSILON,
    MILL_MIN_RANGE_THRESHOLD,
    STATUS_ALARM_PREFIX,
    STATUS_DOOR,
    STATUS_RUN,
    STATUS_HOLD,
    STATUS_IDLE,
    CONTROLLER_STATE_IDLE,
    CONTROLLER_STATE_INITIALIZING,
    CONTROLLER_STATE_RUNNING,
    CONTROLLER_STATE_PAUSED,
    CONTROLLER_STATE_COMPLETING,
    CONTROLLER_STATE_COMPLETED,
    CONTROLLER_STATE_FAILED,
    CONTROLLER_STATE_CANCELLED,
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
    ICON_RESUME
} from './constants.js';

// Get element by ID with null safety
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

// Confirm dialog (replaces browser confirm())
// Returns a Promise that resolves to true (yes) or false (no)
// Options: { danger: true } adds warning styling (red text, warning icon)
export function showConfirm(message, title = 'Confirm', options = {}) {
    return new Promise((resolve) => {
        const modal = $('confirm-modal');
        const titleEl = $('confirm-title');
        const messageEl = $('confirm-message');
        const yesBtn = $('confirm-yes-btn');
        const noBtn = $('confirm-no-btn');

        // Apply danger styling if requested
        titleEl.textContent = title;
        if (options.danger) {
            messageEl.innerHTML = '⚠️ ' + message;
            messageEl.classList.add('confirm-danger');
        } else {
            messageEl.textContent = message;
            messageEl.classList.remove('confirm-danger');
        }

        const cleanup = () => {
            modal.classList.add('hidden');
            messageEl.classList.remove('confirm-danger');
            yesBtn.onclick = null;
            noBtn.onclick = null;
        };

        yesBtn.onclick = () => { cleanup(); resolve(true); };
        noBtn.onclick = () => { cleanup(); resolve(false); };

        modal.classList.remove('hidden');
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
            this.listEl.innerHTML = '<div class="loading">No files found</div>';
            return;
        }

        this.listEl.innerHTML = data.entries.map(entry => `
            <div class="file-item" data-path="${entry.path}" data-isdir="${entry.isDir}">
                <span class="file-icon">${entry.isDir ? '📁' : this.fileIcon}</span>
                <span class="file-name">${entry.name}</span>
                ${entry[this.metaField] ? `<span class="file-meta">${this.formatMeta(entry[this.metaField])}</span>` : ''}
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
        this.listEl.querySelectorAll('.file-item').forEach(i => i.classList.remove('selected'));
        item.classList.add('selected');
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

        // Status strings
        if (server.status) {
            check(STATUS_ALARM_PREFIX, server.status.alarm, 'STATUS_ALARM_PREFIX');
            check(STATUS_DOOR, server.status.door, 'STATUS_DOOR');
            check(STATUS_RUN, server.status.run, 'STATUS_RUN');
            check(STATUS_HOLD, server.status.hold, 'STATUS_HOLD');
            check(STATUS_IDLE, server.status.idle, 'STATUS_IDLE');
        }

        // Controller states
        if (server.controllerStates) {
            check(CONTROLLER_STATE_IDLE, server.controllerStates.idle, 'CONTROLLER_STATE_IDLE');
            check(CONTROLLER_STATE_INITIALIZING, server.controllerStates.initializing, 'CONTROLLER_STATE_INITIALIZING');
            check(CONTROLLER_STATE_RUNNING, server.controllerStates.running, 'CONTROLLER_STATE_RUNNING');
            check(CONTROLLER_STATE_PAUSED, server.controllerStates.paused, 'CONTROLLER_STATE_PAUSED');
            check(CONTROLLER_STATE_COMPLETING, server.controllerStates.completing, 'CONTROLLER_STATE_COMPLETING');
            check(CONTROLLER_STATE_COMPLETED, server.controllerStates.completed, 'CONTROLLER_STATE_COMPLETED');
            check(CONTROLLER_STATE_FAILED, server.controllerStates.failed, 'CONTROLLER_STATE_FAILED');
            check(CONTROLLER_STATE_CANCELLED, server.controllerStates.cancelled, 'CONTROLLER_STATE_CANCELLED');
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
