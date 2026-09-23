import { state } from './state.js';
import { $, setText, showError, showInfo, format } from './helpers.js';
import { pollProbeStatus, dismissProbeComplete, fetchAndDisplayProbeData, showProbeCompleteAndOfferSave, refreshProbeState, updateProbeButtonsFromState, applyProbeRunLock, getIsTracing, updateProbeInfoDisplay } from './probe.js';
import { loadFiles } from './file.js';
import { updateJogButtons, updateContinueMillingButton } from './jog.js';
import {
    updateToolChangeDisplay,
    updateDoorOverlay,
    updateDepthDisplay,
    updateMillGrid,
    applyMillControllerState,
    endMillRun,
    isWaitingForZeroZ
} from './mill.js';
import {
    SCREEN_DASHBOARD,
    SCREEN_FILE,
    SCREEN_MILL,
    SCREEN_PROBE,
    SCREEN_JOG,
    SCREEN_SUFFIX,
    CLASS_HIDDEN,
    CLASS_ACTIVE,
    CLASS_CONNECTED,
    CLASS_ALARM,
    CLASS_DISABLED,
    CLASS_PROBE_OPEN,
    CLASS_PROBE_CONTACT,
    CLASS_STATUS_ERROR,
    CLASS_STATUS_SUCCESS,
    CLASS_STATUS_WARNING,
    CLASS_DISABLED_REASON,
    DISABLED_REASON_SELECTOR,
    CLASS_NONE,
    CLASS_CLICKABLE,
    HEADER_TEXT_BY_ACTIVITY,
    TEXT_DISCONNECTED,
    TEXT_CONNECTED,
    TEXT_RECONNECTING,
    TEXT_UNKNOWN,
    TEXT_MILLING_COMPLETE,
    CONTROLLER_STATE_COMPLETED,
    TEXT_PROBING_IN_PROGRESS,
    TEXT_MILLING_IN_PROGRESS,
    TEXT_PROBE_PIN_OPEN,
    TEXT_PROBE_PIN_CONTACT,
    TEXT_NOT_LOADED,
    TEXT_POINT_COUNT,
    TEXT_LINE_PROGRESS,
    POSITION_DECIMALS_BRIEF,
    TEXT_POSITION_BRIEF,
    POSITION_DECIMALS_FULL,
    PROGRESS_PERCENT_MULTIPLIER,
    PROBE_STATE_NONE,
    PROBE_STATE_COMPLETE,
    TEXT_PROBE_APPLIED,
    TEXT_PROBE_NOT_APPLIED,
    TEXT_NO_FILE_LOADED
} from './constants.js';

/**
 * The screen the operator is locked to, or null. Derived from the last status, so no path
 * that ends a run has to remember to unlock.
 */
function lockedScreen() {
    // A trace moves the tool, so it locks the screen like a grid probe. getIsTracing is read
    // rather than the last status, because a trace this page just asked for is already moving.
    if (state.isProbing || getIsTracing()) {
        return SCREEN_PROBE;
    }
    return state.isMilling ? SCREEN_MILL : null;
}

/**
 * Whether the operator may switch to `screenId`. Everything is locked except the screen
 * the machine is working on, and the jog screen while a tool change waits for Z0.
 */
function navigationAllowed(screenId) {
    const locked = lockedScreen();
    if (locked === null || screenId === locked) {
        return true;
    }
    return locked === SCREEN_MILL && state.awaitingZeroZ && screenId === SCREEN_JOG;
}

export function showScreen(screenId, force = false) {
    if (!force && !navigationAllowed(screenId)) {
        showError(lockedScreen() === SCREEN_PROBE
            ? TEXT_PROBING_IN_PROGRESS
            : TEXT_MILLING_IN_PROGRESS);
        return;
    }

    // state.connected keeps this off the first render, before any status has arrived.
    if ((screenId === SCREEN_PROBE || screenId === SCREEN_MILL) && !state.hasFile && !force && state.connected) {
        showError(TEXT_NO_FILE_LOADED);
        screenId = SCREEN_DASHBOARD;
    }

    document.querySelectorAll('.screen').forEach(screen => {
        screen.classList.remove(CLASS_ACTIVE);
    });
    document.getElementById(screenId).classList.add(CLASS_ACTIVE);
    state.currentScreen = screenId;

    // The hash survives a reload, so the page comes back on the same screen.
    window.location.hash = screenId.replace(SCREEN_SUFFIX, '');

    if (screenId === SCREEN_FILE) {
        loadFiles();
    }

    if (screenId === SCREEN_PROBE) {
        refreshProbeState();
    }
}

export function restoreScreenFromHash() {
    const hash = window.location.hash.slice(1);
    if (hash) {
        const screenId = hash + SCREEN_SUFFIX;
        const screen = document.getElementById(screenId);
        if (screen) {
            showScreen(screenId);
            return;
        }
    }
    showScreen(SCREEN_DASHBOARD);
}

/**
 * Write a position into the three elements named `<where>-x`, `-y` and `-z`. One place
 * decides the number of decimals, so the screens cannot format it differently.
 */
function setAxisText(where, position) {
    for (const axis of ['x', 'y', 'z']) {
        setText(`${where}-${axis}`, position[axis].toFixed(POSITION_DECIMALS_FULL));
    }
}

export function updateStatus(status) {
    state.connected = status.connected;

    // Set before the probing and milling branches below, which read it.
    state.awaitingZeroZ = isWaitingForZeroZ(status.toolChange);
    state.tracingOutline = !!status.tracingOutline;

    if (status.probing) {
        if (!state.isProbing) {
            // Either probing just started, or this page connected while it was already running.
            state.isProbing = true;
            document.getElementById('probe-setup').classList.add(CLASS_HIDDEN);
            document.getElementById('probe-progress').classList.remove(CLASS_HIDDEN);
            showScreen(SCREEN_PROBE, true);
            // The height map comes from this poll, not from the status broadcast.
            pollProbeStatus();
        }
    } else if (state.isProbing) {
        state.isProbing = false;
        // The server's state decides, because a skipped point comes off the queue without
        // being measured and progress can reach total on an unusable map.
        if (status.probe && status.probe.state === PROBE_STATE_COMPLETE) {
            showProbeCompleteAndOfferSave();
        } else {
            dismissProbeComplete();
        }
    } else if (status.probe && status.probe.state === PROBE_STATE_COMPLETE && !state.probeDataDisplayed && !getIsTracing() && !status.milling) {
        // After a reconnect the finished probe data is fetched once. Only on the dashboard or
        // with probe-progress visible, because a file load leaves probe-setup up instead.
        const probeProgressVisible = state.currentScreen === SCREEN_PROBE &&
            !document.getElementById('probe-progress').classList.contains(CLASS_HIDDEN);
        if (state.currentScreen === SCREEN_DASHBOARD || probeProgressVisible) {
            state.probeDataDisplayed = true;
            fetchAndDisplayProbeData();
        }
    }

    if (status.milling) {
        if (!state.isMilling) {
            // Either milling just started, or this page connected while it was already running.
            state.isMilling = true;
            if (status.file) {
                document.getElementById('mill-filename').textContent = status.file.name || TEXT_UNKNOWN;
            }
            // millingPhase is the controller's own name for its step, so it is not shown here.
            // The next progress message supplies the operator text.
            document.getElementById('mill-phase').textContent = '';
            showScreen(SCREEN_MILL, true);
        }
    } else if (state.isMilling) {
        // End the run here for every stop reason. The earlier mill:state broadcast
        // does not cover all stop paths.
        state.isMilling = false;
        endMillRun();
        if (status.controllerState === CONTROLLER_STATE_COMPLETED) {
            showInfo(TEXT_MILLING_COMPLETE);
        }
        showScreen(SCREEN_DASHBOARD, true);
    }

    // The back button goes to the jog screen, so it uses the same check.
    document.getElementById('mill-back-btn').disabled = !navigationAllowed(SCREEN_JOG);

    applyMillControllerState(status.controllerState);

    const indicator = document.getElementById('status-indicator');
    const statusText = document.getElementById('status-text');
    const posDisplay = document.getElementById('position-display');

    indicator.classList.remove(CLASS_CONNECTED, CLASS_ALARM);
    statusText.classList.remove(CLASS_CLICKABLE);
    if (status.connected) {
        if (status.needsAttention) {
            indicator.classList.add(CLASS_ALARM);
            statusText.classList.add(CLASS_CLICKABLE);
        } else {
            indicator.classList.add(CLASS_CONNECTED);
        }
        statusText.textContent =
            HEADER_TEXT_BY_ACTIVITY[status.machineActivity] || status.status || TEXT_CONNECTED;
    } else {
        statusText.textContent = TEXT_DISCONNECTED;
    }

    if (status.workPos) {
        posDisplay.textContent = format(
            TEXT_POSITION_BRIEF,
            status.workPos.x.toFixed(POSITION_DECIMALS_BRIEF),
            status.workPos.y.toFixed(POSITION_DECIMALS_BRIEF),
            status.workPos.z.toFixed(POSITION_DECIMALS_BRIEF));

        setAxisText('work', status.workPos);
        setAxisText('jog-work', status.workPos);
    }

    if (status.machinePos) {
        setAxisText('jog-machine', status.machinePos);
    }

    const jogProbePin = document.getElementById('jog-probe-pin');
    if (jogProbePin && status.probePin !== undefined) {
        jogProbePin.classList.remove(CLASS_PROBE_OPEN, CLASS_PROBE_CONTACT);
        if (status.probePin) {
            jogProbePin.textContent = TEXT_PROBE_PIN_CONTACT;
            jogProbePin.classList.add(CLASS_PROBE_CONTACT);
        } else {
            jogProbePin.textContent = TEXT_PROBE_PIN_OPEN;
            jogProbePin.classList.add(CLASS_PROBE_OPEN);
        }
    }

    const profileNameEl = document.getElementById('profile-status-name');
    if (status.machineProfile) {
        profileNameEl.textContent = status.machineProfile;
        profileNameEl.className = CLASS_NONE;
    } else {
        profileNameEl.textContent = TEXT_NOT_LOADED;
        profileNameEl.className = CLASS_STATUS_ERROR;
    }

    const fileNameEl = document.getElementById('file-status-name');
    const hasFile = status.file && status.file.name;

    // The server's state decides; a point count can reach total on a map that is not usable.
    const hasProbe = status.probe && status.probe.state !== PROBE_STATE_NONE;
    state.hasFile = !!hasFile;
    if (hasFile) {
        fileNameEl.textContent = status.file.name;
        fileNameEl.className = CLASS_NONE;
    } else {
        fileNameEl.textContent = TEXT_NOT_LOADED;
        fileNameEl.className = CLASS_STATUS_ERROR;
        // The probe screen stays up when there is probe data, which is how an autosave is
        // recovered.
        if ((state.currentScreen === SCREEN_PROBE || state.currentScreen === SCREEN_MILL)
            && lockedScreen() === null) {
            if (!(state.currentScreen === SCREEN_PROBE && hasProbe)) {
                showScreen(SCREEN_DASHBOARD);
            }
        }
    }

    const probeStatusEl = document.getElementById('probe-status-info');
    const appliedEl = document.getElementById('probe-status-applied');
    if (hasProbe) {
        probeStatusEl.textContent = format(
            TEXT_POINT_COUNT, status.probe.measured, status.probe.total);
        probeStatusEl.className = CLASS_NONE;
        if (status.probeApplied) {
            appliedEl.textContent = TEXT_PROBE_APPLIED;
            appliedEl.className = CLASS_STATUS_SUCCESS;
        } else {
            appliedEl.textContent = TEXT_PROBE_NOT_APPLIED;
            appliedEl.className = CLASS_STATUS_WARNING;
        }
    } else {
        probeStatusEl.textContent = TEXT_NOT_LOADED;
        probeStatusEl.className = CLASS_STATUS_ERROR;
        appliedEl.textContent = '';
        appliedEl.className = CLASS_NONE;
    }

    if (status.probe && status.probe.state) {
        updateProbeButtonsFromState(status.probe.state, status.probe.hasUnsavedData);
    }

    // Called on every status, because the screen lock depends on whether a run is using the
    // machine, not on whether there is a grid to report.
    applyProbeRunLock();

    // probe.js holds the wording; this only decides when to show it.
    if (hasProbe && !state.isProbing && !getIsTracing()) {
        const p = status.probe;
        updateProbeInfoDisplay(p.sizeX, p.sizeY, p.total, p.progress);
    }

    if (status.feedOverride) {
        const feedEl = document.getElementById('feed-percent');
        if (feedEl) feedEl.textContent = status.feedOverride + '%';
    }

    if (status.file) {
        const progress = status.file.progress || 0;
        const progressFill = document.getElementById('progress-fill');
        const progressPercent = document.getElementById('progress-percent');
        const progressLines = document.getElementById('progress-lines');

        if (progressFill) progressFill.style.width = (progress * PROGRESS_PERCENT_MULTIPLIER) + '%';
        if (progressPercent) progressPercent.textContent = Math.round(progress * PROGRESS_PERCENT_MULTIPLIER) + '%';
        if (progressLines && status.file.currentLine != null && status.file.totalLines != null) {
            progressLines.textContent = format(
                TEXT_LINE_PROGRESS, status.file.currentLine, status.file.totalLines);
        }
    }

    if (status.buttons) {
        updateButtonState('jog-btn', status.buttons.jog);
        updateButtonState('probe-btn', status.buttons.probe);
        updateButtonState('mill-btn', status.buttons.mill);
    }

    updateJogButtons(status);

    updateContinueMillingButton(status.toolChange);

    updateToolChangeDisplay(status.toolChange);

    // The enclosure message, shown when no run is prompting about it.
    updateDoorOverlay(status);

    updateDepthDisplay(status.depthAdjustment);

    if (status.milling) {
        updateMillGrid(status);
    }
}

function updateButtonState(buttonId, buttonState) {
    const btn = document.getElementById(buttonId);
    if (!btn) return;

    if (buttonState.enabled) {
        btn.disabled = false;
        btn.classList.remove(CLASS_DISABLED);
        btn.title = '';
        const reasonSpan = btn.querySelector(DISABLED_REASON_SELECTOR);
        if (reasonSpan) reasonSpan.remove();
    } else {
        btn.disabled = true;
        btn.classList.add(CLASS_DISABLED);
        btn.title = buttonState.reason || '';
        let reasonSpan = btn.querySelector(DISABLED_REASON_SELECTOR);
        if (!reasonSpan) {
            reasonSpan = document.createElement('span');
            reasonSpan.className = CLASS_DISABLED_REASON;
            btn.appendChild(reasonSpan);
        }
        reasonSpan.textContent = buttonState.reason ? ` (${buttonState.reason})` : '';
    }
}

export function showConnectionStatus(isConnected) {
    const indicator = document.getElementById('status-indicator');
    const statusText = document.getElementById('status-text');

    if (isConnected) {
        indicator.classList.add(CLASS_CONNECTED);
        statusText.textContent = TEXT_CONNECTED;
    } else {
        indicator.classList.remove(CLASS_CONNECTED);
        statusText.textContent = TEXT_RECONNECTING;
    }
}

export function initHeader() {
    const statusText = document.getElementById('status-text');
    if (statusText) {
        statusText.addEventListener('click', () => {
            // The class is only on the status text while the machine needs attention.
            if (statusText.classList.contains(CLASS_CLICKABLE)) {
                showScreen(SCREEN_JOG);
            }
        });
    }
}
