// coppercli Web UI Screen Management

import { state } from './state.js';
import { $, showError, showInfo, isProblematicStatus } from './helpers.js';
import { pollProbeStatus, showProbeComplete, dismissProbeComplete, fetchAndDisplayProbeData, refreshProbeState, updateProbeButtonsFromState, applyProbeRunLock, getIsTracing, showProbeSaveModal } from './probe.js';
import { loadFiles } from './file.js';
import { updateJogButtons, updateContinueMillingButton } from './jog.js';
import {
    updateToolChangeDisplay,
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
    CLASS_RUNNING,
    CLASS_HOLD,
    CLASS_PROBE_OPEN,
    CLASS_PROBE_CONTACT,
    CLASS_CLICKABLE,
    STATUS_RUN,
    STATUS_HOLD,
    STATUS_IDLE,
    DOOR_OPEN_TEXT,
    DOOR_CLOSED_TEXT,
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
    POSITION_DECIMALS_BRIEF,
    POSITION_DECIMALS_FULL,
    PROGRESS_PERCENT_MULTIPLIER,
    PROBE_STATE_NONE,
    TEXT_PROBE_APPLIED,
    TEXT_PROBE_NOT_APPLIED,
    TEXT_MILL_RUNNING,
    TEXT_MILL_PAUSED,
    TEXT_NO_FILE_LOADED
} from './constants.js';

/**
 * The screen the machine is holding the operator on, or null. Derived from what the server
 * last reported, so no path that ends a run has to remember to unlock.
 */
function lockedScreen() {
    // A trace moves the tool over the board, so it locks the screen as a grid probe does.
    if (state.isProbing || state.tracingOutline) {
        return SCREEN_PROBE;
    }
    return state.isMilling ? SCREEN_MILL : null;
}

/**
 * Whether the operator may move to `screenId` now. Everything is held except the screen the
 * machine is working on, and the jog screen while a tool change waits for Z0.
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

    // Redirect to dashboard if trying to access Probe or Mill without a file loaded
    // Skip this check if not connected yet (page just loaded, waiting for first status)
    if ((screenId === SCREEN_PROBE || screenId === SCREEN_MILL) && !state.hasFile && !force && state.connected) {
        showError(TEXT_NO_FILE_LOADED);
        screenId = SCREEN_DASHBOARD;
    }

    document.querySelectorAll('.screen').forEach(screen => {
        screen.classList.remove(CLASS_ACTIVE);
    });
    document.getElementById(screenId).classList.add(CLASS_ACTIVE);
    state.currentScreen = screenId;

    // Save current screen to URL hash for reload persistence
    window.location.hash = screenId.replace(SCREEN_SUFFIX, '');

    // Load files when entering file screen
    if (screenId === SCREEN_FILE) {
        loadFiles();
    }

    // Update probe buttons based on state when entering probe screen
    if (screenId === SCREEN_PROBE) {
        refreshProbeState();
    }
}

// Restore screen from URL hash on page load
export function restoreScreenFromHash() {
    const hash = window.location.hash.slice(1); // Remove '#'
    if (hash) {
        const screenId = hash + SCREEN_SUFFIX;
        const screen = document.getElementById(screenId);
        if (screen) {
            showScreen(screenId);
            return;
        }
    }
    // Default to dashboard
    showScreen(SCREEN_DASHBOARD);
}

// Fetch probe data and optionally show save modal if complete and unsaved
function fetchProbeDataAndMaybeShowSaveModal() {
    fetchAndDisplayProbeData().then(() => {
        // Save modal is now handled by checkAndShowUnsavedProbe on startup
        // No need to show it here since state-based buttons handle this
    });
}

export function updateStatus(status) {
    state.connected = status.connected;

    // Set before the probing and milling branches below, which lock the screen and read this.
    state.awaitingZeroZ = isWaitingForZeroZ(status.toolChange);
    state.tracingOutline = !!status.tracingOutline;

    // Enforce screen lock for active operations
    if (status.probing) {
        if (!state.isProbing) {
            // Just started probing (or browser connected while probing) - navigate and lock
            state.isProbing = true;
            document.getElementById('probe-setup').classList.add(CLASS_HIDDEN);
            document.getElementById('probe-progress').classList.remove(CLASS_HIDDEN);
            showScreen(SCREEN_PROBE, true);
            // Start polling for detailed probe status (includes height map)
            pollProbeStatus();
        }
    } else if (state.isProbing) {
        // Probing just finished - fetch complete data and show completion state
        state.isProbing = false;
        if (status.probe && status.probe.progress === status.probe.total && status.probe.total > 0) {
            fetchProbeDataAndMaybeShowSaveModal();
        } else {
            // Probing was cancelled - go back to setup
            dismissProbeComplete();
        }
    } else if (status.probe && status.probe.progress === status.probe.total && status.probe.total > 0 && !state.probeDataDisplayed && !getIsTracing() && !status.milling) {
        // Reconnected after probing completed - fetch and display the probe data (once)
        // Handle both dashboard and probe screen cases, but only if probe-progress is visible
        // (to avoid interfering with file load which keeps probe-setup visible)
        // Skip if milling is in progress (probe data was already applied)
        const probeProgressVisible = state.currentScreen === SCREEN_PROBE &&
            !document.getElementById('probe-progress').classList.contains(CLASS_HIDDEN);
        if (state.currentScreen === SCREEN_DASHBOARD || probeProgressVisible) {
            state.probeDataDisplayed = true;
            fetchProbeDataAndMaybeShowSaveModal();
        }
    }

    if (status.milling) {
        if (!state.isMilling) {
            // Just started milling (or reconnecting to running operation) - navigate and lock
            state.isMilling = true;
            if (status.file) {
                document.getElementById('mill-filename').textContent = status.file.name || TEXT_UNKNOWN;
            }
            // Show current phase from status (prevents showing stale text on reconnect)
            if (status.millingPhase) {
                document.getElementById('mill-phase').textContent = status.millingPhase;
            }
            showScreen(SCREEN_MILL, true);
        }
    } else if (state.isMilling) {
        // The one place a milling run is declared over, however it ended. The mill:state
        // broadcast is sooner, but only this sees every way a run can stop.
        state.isMilling = false;
        endMillRun();
        if (status.controllerState === CONTROLLER_STATE_COMPLETED) {
            showInfo(TEXT_MILLING_COMPLETE);
        }
        showScreen(SCREEN_DASHBOARD, true);
    }

    // The back button goes where the jog screen does, so it asks the same question.
    document.getElementById('mill-back-btn').disabled = !navigationAllowed(SCREEN_JOG);

    // Pause button and feed controls follow the controller state.
    applyMillControllerState(status.controllerState);

    // Update header
    const indicator = document.getElementById('status-indicator');
    const statusText = document.getElementById('status-text');
    const posDisplay = document.getElementById('position-display');

    // Update connection indicator
    // Treat status "Disconnected" as not connected even if serial port is open
    // (GRBL hasn't responded yet)
    const statusStr = status.status || '';
    const effectivelyConnected = state.connected && statusStr !== TEXT_DISCONNECTED;
    indicator.classList.remove(CLASS_CONNECTED, CLASS_ALARM);
    statusText.classList.remove(CLASS_CLICKABLE);
    if (effectivelyConnected) {
        const isAlarm = isProblematicStatus(statusStr);
        if (isAlarm) {
            indicator.classList.add(CLASS_ALARM);
            statusText.classList.add(CLASS_CLICKABLE);
        } else {
            indicator.classList.add(CLASS_CONNECTED);
        }
        statusText.textContent = statusStr || TEXT_CONNECTED;
    } else {
        statusText.textContent = TEXT_DISCONNECTED;
    }

    // Update position displays
    if (status.workPos) {
        posDisplay.textContent = `X:${status.workPos.x.toFixed(POSITION_DECIMALS_BRIEF)} Y:${status.workPos.y.toFixed(POSITION_DECIMALS_BRIEF)} Z:${status.workPos.z.toFixed(POSITION_DECIMALS_BRIEF)}`;

        document.getElementById('work-x').textContent = status.workPos.x.toFixed(POSITION_DECIMALS_FULL);
        document.getElementById('work-y').textContent = status.workPos.y.toFixed(POSITION_DECIMALS_FULL);
        document.getElementById('work-z').textContent = status.workPos.z.toFixed(POSITION_DECIMALS_FULL);

        // Update jog screen work positions
        const jogWorkX = document.getElementById('jog-work-x');
        const jogWorkY = document.getElementById('jog-work-y');
        const jogWorkZ = document.getElementById('jog-work-z');
        if (jogWorkX) jogWorkX.textContent = status.workPos.x.toFixed(POSITION_DECIMALS_FULL);
        if (jogWorkY) jogWorkY.textContent = status.workPos.y.toFixed(POSITION_DECIMALS_FULL);
        if (jogWorkZ) jogWorkZ.textContent = status.workPos.z.toFixed(POSITION_DECIMALS_FULL);

        // Update milling screen positions
        const millX = document.getElementById('mill-x');
        const millY = document.getElementById('mill-y');
        const millZ = document.getElementById('mill-z');
        if (millX) millX.textContent = status.workPos.x.toFixed(POSITION_DECIMALS_FULL);
        if (millY) millY.textContent = status.workPos.y.toFixed(POSITION_DECIMALS_FULL);
        if (millZ) millZ.textContent = status.workPos.z.toFixed(POSITION_DECIMALS_FULL);
    }

    // Update jog screen machine positions
    if (status.machinePos) {
        const jogMachX = document.getElementById('jog-machine-x');
        const jogMachY = document.getElementById('jog-machine-y');
        const jogMachZ = document.getElementById('jog-machine-z');
        if (jogMachX) jogMachX.textContent = status.machinePos.x.toFixed(POSITION_DECIMALS_FULL);
        if (jogMachY) jogMachY.textContent = status.machinePos.y.toFixed(POSITION_DECIMALS_FULL);
        if (jogMachZ) jogMachZ.textContent = status.machinePos.z.toFixed(POSITION_DECIMALS_FULL);
    }

    // Update probe pin indicator (BitZero status)
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

    // Update dashboard profile status
    const profileNameEl = document.getElementById('profile-status-name');
    if (status.machineProfile) {
        profileNameEl.textContent = status.machineProfile;
        profileNameEl.className = '';
    } else {
        profileNameEl.textContent = TEXT_NOT_LOADED;
        profileNameEl.className = 'status-error';
    }

    // Update dashboard file status
    const fileNameEl = document.getElementById('file-status-name');
    const hasFile = status.file && status.file.name;
    state.hasFile = !!hasFile;
    if (hasFile) {
        fileNameEl.textContent = status.file.name;
        fileNameEl.className = '';
    } else {
        fileNameEl.textContent = TEXT_NOT_LOADED;
        fileNameEl.className = 'status-error';
        // Redirect to dashboard if on Probe/Mill screen without a file (and not locked)
        // Exception: stay on Probe screen if there's probe data (recovering from autosave)
        const hasProbeData = status.probe && status.probe.state !== PROBE_STATE_NONE;
        if ((state.currentScreen === SCREEN_PROBE || state.currentScreen === SCREEN_MILL)
            && lockedScreen() === null) {
            if (!(state.currentScreen === SCREEN_PROBE && hasProbeData)) {
                showScreen(SCREEN_DASHBOARD);
            }
        }
    }

    // Update dashboard probe status
    const probeStatusEl = document.getElementById('probe-status-info');
    const appliedEl = document.getElementById('probe-status-applied');
    const hasProbe = status.probe && status.probe.total > 0;
    if (hasProbe) {
        probeStatusEl.textContent = `${status.probe.progress}/${status.probe.total} points`;
        probeStatusEl.className = '';
        if (status.probeApplied) {
            appliedEl.textContent = TEXT_PROBE_APPLIED;
            appliedEl.className = 'status-success';
        } else {
            appliedEl.textContent = TEXT_PROBE_NOT_APPLIED;
            appliedEl.className = 'status-warning';
        }
    } else {
        probeStatusEl.textContent = TEXT_NOT_LOADED;
        probeStatusEl.className = 'status-error';
        appliedEl.textContent = '';
        appliedEl.className = '';
    }

    // Update probe screen buttons based on state from server
    if (status.probe && status.probe.state) {
        updateProbeButtonsFromState(status.probe.state, status.probe.hasUnsavedData);
    }

    // Unconditional: the screen is held by whether a run owns the machine, which is true
    // whether or not there is a grid to report.
    applyProbeRunLock();

    // Update probe setup screen info display
    const probeInfoEl = document.getElementById('probe-info');
    if (probeInfoEl && hasProbe && !state.isProbing && !getIsTracing()) {
        const p = status.probe;
        const pct = Math.round((p.progress / p.total) * 100);
        if (p.progress === p.total) {
            probeInfoEl.textContent = `Grid: ${p.sizeX || '?'}x${p.sizeY || '?'} = ${p.total} points (complete)`;
        } else if (p.progress > 0) {
            probeInfoEl.textContent = `Grid: ${p.sizeX || '?'}x${p.sizeY || '?'} = ${p.total} points (${p.progress} probed, ${pct}%)`;
        } else {
            probeInfoEl.textContent = `Grid: ${p.sizeX || '?'}x${p.sizeY || '?'} = ${p.total} points`;
        }
    }


    // Update feed override
    if (status.feedOverride) {
        const feedEl = document.getElementById('feed-percent');
        if (feedEl) feedEl.textContent = status.feedOverride + '%';
    }

    // Update milling progress
    if (status.file) {
        const progress = status.file.progress || 0;
        const progressFill = document.getElementById('progress-fill');
        const progressPercent = document.getElementById('progress-percent');
        const progressLines = document.getElementById('progress-lines');
        const millStatus = document.getElementById('mill-status');

        if (progressFill) progressFill.style.width = (progress * PROGRESS_PERCENT_MULTIPLIER) + '%';
        if (progressPercent) progressPercent.textContent = Math.round(progress * PROGRESS_PERCENT_MULTIPLIER) + '%';
        if (progressLines && status.file.currentLine != null && status.file.totalLines != null) {
            progressLines.textContent = `${status.file.currentLine} / ${status.file.totalLines}`;
        }

        // Update status indicator
        if (millStatus) {
            if (status.status === STATUS_RUN) {
                millStatus.textContent = TEXT_MILL_RUNNING;
                millStatus.className = 'mill-status ' + CLASS_RUNNING;
            } else if (status.doorOpen) {
                millStatus.textContent = DOOR_OPEN_TEXT;
                millStatus.className = 'mill-status ' + CLASS_HOLD;
            } else if (status.doorAwaitingResume) {
                millStatus.textContent = DOOR_CLOSED_TEXT;
                millStatus.className = 'mill-status ' + CLASS_HOLD;
            } else if (status.status === STATUS_HOLD) {
                millStatus.textContent = TEXT_MILL_PAUSED;
                millStatus.className = 'mill-status ' + CLASS_HOLD;
            } else {
                millStatus.textContent = status.status || STATUS_IDLE;
                millStatus.className = 'mill-status';
            }
        }
    }

    // Update dashboard button states
    if (status.buttons) {
        updateButtonState('jog-btn', status.buttons.jog);
        updateButtonState('probe-btn', status.buttons.probe);
        updateButtonState('mill-btn', status.buttons.mill);
    }

    // Update jog screen button states (for alarm/door state)
    updateJogButtons(status);

    // Update "Continue Milling" button on jog screen (tool change WaitingForZeroZ phase)
    updateContinueMillingButton(status.toolChange);

    // Update tool change display on mill screen
    updateToolChangeDisplay(status.toolChange);

    // Update depth adjustment display
    updateDepthDisplay(status.depthAdjustment);

    // Update mill grid visualization (only when milling)
    if (status.milling) {
        updateMillGrid(status);
    }
}

// Updates a button's enabled/disabled state and shows reason if disabled
function updateButtonState(buttonId, buttonState) {
    const btn = document.getElementById(buttonId);
    if (!btn) return;

    if (buttonState.enabled) {
        btn.disabled = false;
        btn.classList.remove(CLASS_DISABLED);
        btn.title = '';
        // Remove reason text if present
        const reasonSpan = btn.querySelector('.disabled-reason');
        if (reasonSpan) reasonSpan.remove();
    } else {
        btn.disabled = true;
        btn.classList.add(CLASS_DISABLED);
        btn.title = buttonState.reason || '';
        // Add or update reason text
        let reasonSpan = btn.querySelector('.disabled-reason');
        if (!reasonSpan) {
            reasonSpan = document.createElement('span');
            reasonSpan.className = 'disabled-reason';
            btn.appendChild(reasonSpan);
        }
        reasonSpan.textContent = buttonState.reason ? ` (${buttonState.reason})` : '';
    }
}

// Connection status indicator
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

// Initialize header click handlers
export function initHeader() {
    const statusText = document.getElementById('status-text');
    if (statusText) {
        statusText.addEventListener('click', () => {
            // Only navigate if clickable (in alarm/door state)
            if (statusText.classList.contains(CLASS_CLICKABLE)) {
                showScreen(SCREEN_JOG);
            }
        });
    }
}
