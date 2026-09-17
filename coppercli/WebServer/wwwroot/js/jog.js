import { state } from './state.js';
import { $, addTouchRepeat, showInfo, showError, showConfirm, updatePauseButton, format, postJson } from './helpers.js';
import { sendCommand } from './websocket.js';
import { showScreen } from './screens.js';
import { isWaitingForZeroZ, continueLastPrompt } from './mill.js';
import {
    API_CONFIG,
    API_PROBE_STATUS,
    API_RESUME,
    API_ZERO,
    ERROR_RESUME_NOT_SENT,
    ERROR_ZERO_NOT_SENT,
    TEXT_ZEROED_Z,
    TEXT_ZEROED_ALL,
    TEXT_ZEROED_WITH_MAP,
    MAP_DESCRIPTION_BY_STATE,
    HEIGHT_MAP_TEXT_BY_OUTCOME,
    CMD_JOG_MODE,
    CMD_HOME,
    CMD_UNLOCK,
    CMD_RESET,
    CMD_GOTO_ORIGIN,
    CMD_GOTO_CENTER,
    CMD_GOTO_SAFE,
    CMD_GOTO_REF,
    CMD_GOTO_Z0,
    CMD_PROBE_Z,
    CMD_FEEDHOLD,
    CMD_RESUME,
    CLASS_ACTIVE,
    CLASS_HIDDEN,
    PROBE_STATE_NONE,
    SCREEN_MILL,
    TEXT_ZERO_XY_INVALIDATES,
    TEXT_ZERO_AXES_XY,
    TEXT_ZERO_TITLE,
} from './constants.js';

export async function loadConfig() {
    try {
        const response = await fetch(API_CONFIG);
        const config = await response.json();
        state.jogModes = config.jogModes || [];
        state.jogModeIndex = config.defaultJogModeIndex ?? slowestJogMode();
        if (config.probeDefaults) {
            state.probeDefaults = config.probeDefaults;
        }
        if (config.millGrid) {
            state.millGrid = config.millGrid;
        }
    } catch (err) {
        console.error('Failed to load config:', err);
        // A fallback list; the server validates the mode index it is sent.
        state.jogModes = [
            { name: 'Fast' },
            { name: 'Normal' },
            { name: 'Slow' },
            { name: 'Creep' }
        ];
        // Without the server's list, use the mode that moves least per press.
        state.jogModeIndex = slowestJogMode();
    }
}

// The modes are ordered fastest to finest, so the last one moves least.
function slowestJogMode() {
    return Math.max(0, state.jogModes.length - 1);
}

export function jogWithMode(axis, direction) {
    if (state.ws && state.ws.readyState === WebSocket.OPEN && state.jogModes.length > 0) {
        // Only the index goes over the wire; the distances stay in the server's config.
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
        document.querySelectorAll('.mode-btn[data-mode]').forEach(btn => {
            btn.classList.toggle(CLASS_ACTIVE, parseInt(btn.dataset.mode) === index);
        });
    }
}

// Resume can be refused and the reason cannot come back over the WebSocket, so it goes over
// HTTP like zeroing. A feed hold is never refused and stays on the socket.
async function togglePause() {
    const btn = $('jog-pause-btn');
    if (btn.dataset.paused !== 'true') {
        sendCommand(CMD_FEEDHOLD);
        return;
    }

    const resumed = await postJson(API_RESUME);
    if (!resumed.ok) {
        showError(resumed.error || ERROR_RESUME_NOT_SENT);
    }
}

async function zeroWithWarning(axes) {
    // Z-only keeps the probe corrections, so it needs no warning.
    const zeroingXY = axes.some(a => a === 'X' || a === 'Y');

    if (zeroingXY) {
        try {
            const response = await fetch(API_PROBE_STATUS);
            const data = await response.json();

            if (data.state && data.state !== PROBE_STATE_NONE) {
                // One entry per state the server can send. Collapsing three states into two
                // once described a grid with nothing measured as a complete height map.
                const stateDesc = MAP_DESCRIPTION_BY_STATE[data.state];
                if (!await showConfirm(
                    format(TEXT_ZERO_XY_INVALIDATES, stateDesc, TEXT_ZERO_AXES_XY),
                    TEXT_ZERO_TITLE)) {
                    return;
                }
            }
        } catch (err) {
            console.error('Failed to check probe state:', err);
        }
    }

    // Over HTTP rather than the socket, because the server can refuse this and the operator
    // must not be told the origin was set when it was not.
    const result = await postJson(API_ZERO, { axes });
    if (!result.ok) {
        showError(result.error || ERROR_ZERO_NOT_SENT);
        return;
    }

    const zeroed = result.data.heightMap;
    const reloadTheFile = result.data.reloadTheFile === true;

    // The height map's outcome decides whether the next cut is at the right depth, so it is
    // shown with the confirmation.
    const what = zeroingXY ? TEXT_ZEROED_ALL : TEXT_ZEROED_Z;
    const map = HEIGHT_MAP_TEXT_BY_OUTCOME[zeroed];
    const line = map ? format(TEXT_ZEROED_WITH_MAP, what, map) : what;

    if (reloadTheFile) {
        showError(line);
        return;
    }

    showInfo(line);
}

export function initJogScreen() {
    $('jog-home-btn').addEventListener('click', () => sendCommand(CMD_HOME));
    $('jog-unlock-btn').addEventListener('click', () => sendCommand(CMD_UNLOCK));
    $('jog-pause-btn').addEventListener('click', togglePause);
    $('jog-stop-btn').addEventListener('click', () => sendCommand(CMD_RESET));

    document.querySelectorAll('.jog-btn[data-axis]').forEach(btn => {
        const axis = btn.dataset.axis;
        const dir = parseInt(btn.dataset.dir);
        const action = () => jogWithMode(axis, dir);

        btn.addEventListener('click', action);
        addTouchRepeat(btn, action);
    });

    $('jog-zero-all-btn').addEventListener('click', () => zeroWithWarning(['X', 'Y', 'Z']));
    $('jog-zero-z-btn').addEventListener('click', () => zeroWithWarning(['Z']));

    $('jog-probe-z-btn').addEventListener('click', () => sendCommand(CMD_PROBE_Z));

    $('jog-goto-origin-btn').addEventListener('click', () => sendCommand(CMD_GOTO_ORIGIN));
    $('jog-goto-center-btn').addEventListener('click', () => sendCommand(CMD_GOTO_CENTER));
    $('jog-goto-safe-btn').addEventListener('click', () => sendCommand(CMD_GOTO_SAFE));
    $('jog-goto-ref-btn').addEventListener('click', () => sendCommand(CMD_GOTO_REF));
    $('jog-goto-z0-btn').addEventListener('click', () => sendCommand(CMD_GOTO_Z0));

    const continueBtn = $('jog-continue-milling-btn');
    if (continueBtn) {
        continueBtn.addEventListener('click', continueMilling);
    }

    setJogMode(state.jogModeIndex);

    document.querySelectorAll('.mode-btn[data-mode]').forEach(btn => {
        btn.addEventListener('click', () => setJogMode(parseInt(btn.dataset.mode)));
    });
}

/**
 * Continue milling after setting Z0 by hand, which is what a tool change asks for when the
 * machine has no tool setter.
 */
async function continueMilling() {
    if (await continueLastPrompt()) {
        showScreen(SCREEN_MILL);
    }
}

export function updateContinueMillingButton(toolChange) {
    const btn = $('jog-continue-milling-btn');
    if (!btn) return;

    if (isWaitingForZeroZ(toolChange)) {
        btn.classList.remove(CLASS_HIDDEN);
    } else {
        btn.classList.add(CLASS_HIDDEN);
    }
}

const attentionDisabledButtons = [
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

// X/Y moves, disabled while the probe is in contact so it is not dragged across the work.
const xyMovementButtons = [
    'jog-goto-origin-btn',
    'jog-goto-center-btn'
];

export function updateJogButtons(status) {
    // No status yet, so leave the controls disabled.
    const machineUnavailable = status?.machineUnavailable ?? true;
    const canPause = status?.canPause ?? false;
    const canResume = status?.canResume ?? false;
    const probeContact = status?.probePin ?? false;

    const pauseBtn = $('jog-pause-btn');
    if (pauseBtn) {
        updatePauseButton(pauseBtn, canResume);
        pauseBtn.disabled = !canPause && !canResume;
    }

    attentionDisabledButtons.forEach(id => {
        const btn = document.getElementById(id);
        if (btn) {
            const isXYMove = xyMovementButtons.includes(id);
            btn.disabled = machineUnavailable || (isXYMove && probeContact);
        }
    });

    document.querySelectorAll('.jog-btn[data-axis]').forEach(btn => {
        const axis = btn.dataset.axis?.toUpperCase();
        const isXY = axis === 'X' || axis === 'Y';
        btn.disabled = machineUnavailable || (isXY && probeContact);
    });

    document.querySelectorAll('.mode-btn[data-mode]').forEach(btn => {
        btn.disabled = machineUnavailable;
    });
}
