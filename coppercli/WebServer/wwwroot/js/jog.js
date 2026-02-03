// coppercli Web UI Jog Screen

import { state } from './state.js';
import { $, addTouchRepeat, showInfo, showConfirm, showError, updatePauseButton } from './helpers.js';
import { sendCommand } from './websocket.js';
import { showScreen } from './screens.js';
import { isWaitingForZeroZ } from './mill.js';
import {
    API_CONFIG,
    API_PROBE_STATUS,
    API_MILL_TOOLCHANGE_INPUT,
    CMD_JOG_MODE,
    CMD_HOME,
    CMD_UNLOCK,
    CMD_RESET,
    CMD_ZERO,
    CMD_GOTO_ORIGIN,
    CMD_GOTO_CENTER,
    CMD_GOTO_SAFE,
    CMD_GOTO_REF,
    CMD_GOTO_Z0,
    CMD_PROBE_Z,
    CMD_FEEDHOLD,
    CMD_RESUME,
    DEFAULT_JOG_MODE_INDEX,
    CLASS_ACTIVE,
    CLASS_HIDDEN,
    STATUS_ALARM_PREFIX,
    STATUS_DOOR,
    STATUS_RUN,
    STATUS_HOLD,
    PROBE_STATE_NONE,
    SCREEN_MILL
} from './constants.js';

export async function loadConfig() {
    try {
        const response = await fetch(API_CONFIG);
        const config = await response.json();
        state.jogModes = config.jogModes || [];
        // Load server-provided constants to avoid duplicating values
        if (config.probeDefaults) {
            state.probeDefaults = config.probeDefaults;
        }
        if (config.millGrid) {
            state.millGrid = config.millGrid;
        }
    } catch (err) {
        console.error('Failed to load config:', err);
        // Fallback - will be validated server-side anyway
        state.jogModes = [
            { name: 'Fast' },
            { name: 'Normal' },
            { name: 'Slow' },
            { name: 'Creep' }
        ];
    }
}

export function jogWithMode(axis, direction) {
    if (state.ws && state.ws.readyState === WebSocket.OPEN && state.jogModes.length > 0) {
        // Send mode index - server uses the actual values from its config
        state.ws.send(JSON.stringify({
            type: CMD_JOG_MODE,
            axis: axis,
            direction: direction,
            modeIndex: state.jogModeIndex
        }));
    }
}

export function setJogMode(index) {
    if (index >= 0 && index < state.jogModes.length) {
        state.jogModeIndex = index;
        // Update button states
        document.querySelectorAll('.mode-btn[data-mode]').forEach(btn => {
            btn.classList.toggle(CLASS_ACTIVE, parseInt(btn.dataset.mode) === index);
        });
    }
}

function togglePause() {
    const btn = $('jog-pause-btn');
    if (btn.dataset.paused === 'true') {
        sendCommand(CMD_RESUME);
    } else {
        sendCommand(CMD_FEEDHOLD);
    }
}

// Check if probe data exists and warn before zeroing (only for X/Y changes)
async function zeroWithWarning(axes, retract) {
    // Only warn if X or Y is being zeroed (Z-only preserves probe corrections)
    const zeroingXY = axes.some(a => a === 'X' || a === 'Y');

    if (zeroingXY) {
        try {
            const response = await fetch(API_PROBE_STATUS);
            const data = await response.json();

            if (data.state && data.state !== PROBE_STATE_NONE) {
                const stateDesc = data.state === 'partial' ? 'partial' : 'complete';
                if (!await showConfirm(`You have ${stateDesc} probe data. Zeroing X/Y will invalidate it. Continue?`, 'Zero')) {
                    return;
                }
            }
        } catch (err) {
            // If check fails, proceed anyway
            console.error('Failed to check probe state:', err);
        }
    }

    sendCommand(CMD_ZERO, { axes, retract });
    showInfo(axes.length === 1 ? 'Z zeroed' : 'All axes zeroed');
}

export function initJogScreen() {
    // Quick action buttons (jog screen only - matches TUI)
    $('jog-home-btn').addEventListener('click', () => sendCommand(CMD_HOME));
    $('jog-unlock-btn').addEventListener('click', () => sendCommand(CMD_UNLOCK));
    $('jog-pause-btn').addEventListener('click', togglePause);
    $('jog-stop-btn').addEventListener('click', () => sendCommand(CMD_RESET));

    // Jog buttons - use current mode's base distance
    document.querySelectorAll('.jog-btn[data-axis]').forEach(btn => {
        const axis = btn.dataset.axis;
        const dir = parseInt(btn.dataset.dir);
        const action = () => jogWithMode(axis, dir);

        btn.addEventListener('click', action);
        addTouchRepeat(btn, action);
    });

    // Zero buttons - warn if probe data exists
    $('jog-zero-all-btn').addEventListener('click', () => zeroWithWarning(['X', 'Y', 'Z'], true));
    $('jog-zero-z-btn').addEventListener('click', () => zeroWithWarning(['Z'], true));

    // Probe Z at current position
    $('jog-probe-z-btn').addEventListener('click', () => sendCommand(CMD_PROBE_Z));

    // Go to position buttons
    $('jog-goto-origin-btn').addEventListener('click', () => sendCommand(CMD_GOTO_ORIGIN));
    $('jog-goto-center-btn').addEventListener('click', () => sendCommand(CMD_GOTO_CENTER));
    $('jog-goto-safe-btn').addEventListener('click', () => sendCommand(CMD_GOTO_SAFE));
    $('jog-goto-ref-btn').addEventListener('click', () => sendCommand(CMD_GOTO_REF));
    $('jog-goto-z0-btn').addEventListener('click', () => sendCommand(CMD_GOTO_Z0));

    // Continue Milling button (shown during tool change WaitingForZeroZ phase)
    const continueBtn = $('jog-continue-milling-btn');
    if (continueBtn) {
        continueBtn.addEventListener('click', continueMilling);
    }

    // Set default jog mode
    setJogMode(DEFAULT_JOG_MODE_INDEX);

    // Mode selector buttons
    document.querySelectorAll('.mode-btn[data-mode]').forEach(btn => {
        btn.addEventListener('click', () => setJogMode(parseInt(btn.dataset.mode)));
    });
}

/**
 * Continue milling after setting Z0 (tool change Mode B).
 * Sends "Continue" response to the tool change controller.
 */
async function continueMilling() {
    try {
        const response = await fetch(API_MILL_TOOLCHANGE_INPUT, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ response: 'Continue' })
        });
        const result = await response.json();
        if (result.success) {
            showScreen(SCREEN_MILL);
        } else {
            showError(result.error || 'Failed to continue');
        }
    } catch (err) {
        showError('Failed to continue: ' + err.message);
    }
}

/**
 * Update "Continue Milling" button visibility based on tool change phase.
 * Called from screens.js when status is received.
 */
export function updateContinueMillingButton(toolChange) {
    const btn = $('jog-continue-milling-btn');
    if (!btn) return;

    if (isWaitingForZeroZ(toolChange)) {
        btn.classList.remove(CLASS_HIDDEN);
    } else {
        btn.classList.add(CLASS_HIDDEN);
    }
}

// IDs of buttons that should be disabled in alarm/door state
const alarmDisabledButtons = [
    'jog-home-btn',
    'jog-zero-all-btn',
    'jog-zero-z-btn',
    'jog-probe-z-btn',
    'jog-goto-origin-btn',
    'jog-goto-center-btn',
    'jog-goto-safe-btn',
    'jog-goto-ref-btn',
    'jog-goto-z0-btn'
];

// IDs of buttons that involve X/Y movement (disabled when probe is in contact)
const xyMovementButtons = [
    'jog-goto-origin-btn',
    'jog-goto-center-btn'
];

// Update jog screen button states based on machine status
export function updateJogButtons(status) {
    const statusStr = status?.status || '';
    const isAlarm = statusStr.startsWith(STATUS_ALARM_PREFIX) || statusStr === STATUS_DOOR;
    const isRun = statusStr === STATUS_RUN;
    const isHold = statusStr.startsWith(STATUS_HOLD);
    const probeContact = status?.probePin || false;

    // Update pause/resume button text and state
    const pauseBtn = $('jog-pause-btn');
    if (pauseBtn) {
        updatePauseButton(pauseBtn, isHold);
        pauseBtn.disabled = !isRun && !isHold;
    }

    // Disable/enable specific buttons
    alarmDisabledButtons.forEach(id => {
        const btn = document.getElementById(id);
        if (btn) {
            const isXYMove = xyMovementButtons.includes(id);
            btn.disabled = isAlarm || (isXYMove && probeContact);
        }
    });

    // Disable/enable jog direction buttons
    // X/Y blocked when probe is in contact (prevents dragging probe across workpiece)
    document.querySelectorAll('.jog-btn[data-axis]').forEach(btn => {
        const axis = btn.dataset.axis?.toUpperCase();
        const isXY = axis === 'X' || axis === 'Y';
        btn.disabled = isAlarm || (isXY && probeContact);
    });

    // Disable/enable jog mode selector buttons
    document.querySelectorAll('.mode-btn[data-mode]').forEach(btn => {
        btn.disabled = isAlarm;
    });
}
