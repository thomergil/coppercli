// coppercli Web UI Probe Screen

import { state } from './state.js';
import { $, setText, addClass, removeClass, showError, showInfo, showConfirm, FileBrowser, updatePauseButton, postJson } from './helpers.js';
import { showScreen } from './screens.js';
import {
    API_STATUS,
    API_PROBE_SETUP,
    API_PROBE_TRACE,
    API_PROBE_START,
    API_PROBE_PAUSE,
    API_PROBE_RESUME,
    API_PROBE_STOP,
    API_PROBE_STATUS,
    API_PROBE_APPLY,
    API_PROBE_SAVE,
    API_PROBE_LOAD,
    API_PROBE_FILES,
    API_PROBE_DISCARD,
    API_PROBE_RECOVER_AUTOSAVE,
    SCREEN_DASHBOARD,
    SCREEN_PROBE,
    SCREEN_PROBE_FILES,
    CLASS_HIDDEN,
    CLASS_BTN_DANGER,
    CLASS_BTN_SUCCESS,
    CLASS_BTN_WARNING,
    CLASS_PROBED,
    PROBE_FILE_EXTENSION,
    PROBE_STATE_NONE,
    PROBE_STATE_READY,
    PROBE_STATE_PARTIAL,
    PROBE_STATE_COMPLETE,
    TEXT_PROBING_TITLE,
    TEXT_PROBING_DONE_TITLE,
    TEXT_PROBE_DATA_SAVED,
    TEXT_PROBE_DATA_LOADED,
    TEXT_PROBE_DATA_APPLIED,
    TEXT_PROBE_DATA_CLEARED,
    TEXT_LOADING,
    TEXT_LOAD,
    TEXT_SAVE,
    TEXT_SAVING,
    TEXT_SAVE_PROBE_DATA,
    TEXT_LOAD_PROBE_DATA,
    TEXT_DISCARD,
    TEXT_CLEAR,
    TEXT_ENTER_FILENAME,
    TEXT_PROBE_RECOVERED,
    TEXT_RECOVERY_FAILED,
    TEXT_SETUP_FAILED,
    TEXT_TRACE_FAILED,
    TEXT_APPLY_FAILED,
    TEXT_PROBE_APPLIED_TO_GCODE,
    TEXT_DISCARD_FAILED,
    TEXT_DISCARD_CONFIRM,
    TEXT_DISCARD_TITLE,
    TEXT_SOURCE_GCODE_MISSING,
    TEXT_PAUSE_FAILED,
    TEXT_PROBE_LOAD_FAILED,
    TEXT_PROBE_SAVE_FAILED,
    TEXT_START_PROBING,
    TEXT_CONTINUE_PROBING,
    TEXT_STOP,
    PROBE_POLL_INTERVAL_MS,
    TRACE_BUTTON_SETTLE_MS,
    TRACE_POLL_MAX_FAILURES,
    ERROR_LOST_CONTACT,
    PROBE_GRID_CELL_SIZE_PX,
    POSITION_DECIMALS_FULL,
    PHASE_TRACING_OUTLINE,
    ERROR_PROBE_NOT_STARTED,
    ERROR_STOP_NOT_SENT
} from './constants.js';

export async function setupProbeGrid() {
    const margin = parseFloat(document.getElementById('probe-margin').value) || state.probeDefaults.margin;
    const gridSize = parseFloat(document.getElementById('probe-grid-size').value) || state.probeDefaults.gridSize;

    const { ok, error, data } = await postJson(API_PROBE_SETUP, { margin, gridSize });
    if (!ok) {
        showError(error || TEXT_SETUP_FAILED);
        return;
    }

    updateProbeInfoDisplay(data.sizeX, data.sizeY, data.totalPoints, 0);
    renderProbeGrid(data.sizeX, data.sizeY);
    // The new grid decides every button on this screen, so read it back rather than
    // setting them here as well.
    await refreshProbeState();
}

export function renderProbeGrid(sizeX, sizeY) {
    const grid = document.getElementById('probe-grid');
    grid.style.gridTemplateColumns = `repeat(${sizeX}, ${PROBE_GRID_CELL_SIZE_PX}px)`;
    grid.innerHTML = '';

    for (let y = sizeY - 1; y >= 0; y--) {
        for (let x = 0; x < sizeX; x++) {
            const cell = document.createElement('div');
            cell.className = 'probe-cell';
            cell.dataset.x = x;
            cell.dataset.y = y;
            grid.appendChild(cell);
        }
    }
}

// What this page knows about the trace that the server has not reported yet: true from
// asking for one until the first status shows it, false from stopping until the last status
// stops showing it. Null the rest of the time, when the server's answer is the only one.
let traceOverride = null;

export function getIsTracing() {
    if (traceOverride !== null && traceOverride === state.tracingOutline) {
        traceOverride = null;
    }
    return traceOverride ?? state.tracingOutline;
}

export async function traceOutline() {
    const startBtn = document.getElementById('probe-start-btn');

    traceOverride = true;

    // Turns the start button into the stop and disables everything else.
    applyProbeRunLock();

    try {
        const { ok, error } = await postJson(API_PROBE_TRACE);
        if (!ok) {
            showError(error || TEXT_TRACE_FAILED);
            return;
        }

        // Poll until trace is complete
        await pollTraceStatus();
    } finally {
        traceOverride = false;

        // Hands every control back to the state the server reports: label, colour, enabled.
        await refreshProbeState();

        // Then keep the start button disabled a moment longer, in case a finger is still
        // on STOP, and let the state decide again rather than forcing it enabled.
        startBtn.disabled = true;
        setTimeout(() => { refreshProbeState(); }, TRACE_BUTTON_SETTLE_MS);
    }
}

async function stopTrace() {
    // Prevent status updates from showing probe progress view after trace stops
    state.probeDataDisplayed = true;

    // The server answers whether it confirmed the machine stopped, the same answer
    // stopProbing reads. The finally in traceOutline restores the button either way.
    const { ok, error } = await postJson(API_PROBE_STOP);
    if (!ok) {
        showError(error || ERROR_STOP_NOT_SENT);
    }
}

// Ends when the server says the trace is over. One failed request must not unlock the
// screen while the tool is still moving, but a server that has stopped answering is
// reported rather than left holding the screen locked.
async function pollTraceStatus() {
    let failures = 0;

    for (;;) {
        try {
            const response = await fetch(API_PROBE_STATUS);
            const data = await response.json();
            if (data.phase !== PHASE_TRACING_OUTLINE) {
                return;
            }
            failures = 0;
        } catch (err) {
            console.error('Trace poll error:', err);
            if (++failures >= TRACE_POLL_MAX_FAILURES) {
                showError(ERROR_LOST_CONTACT);
                return;
            }
        }

        await new Promise(resolve => setTimeout(resolve, PROBE_POLL_INTERVAL_MS));
    }
}

export async function startProbing() {
    const { ok, error } = await postJson(API_PROBE_START);
    if (!ok) {
        // Nothing is probing, so leave the setup view up.
        showError(error || ERROR_PROBE_NOT_STARTED);
        return;
    }

    document.getElementById('probe-setup').classList.add(CLASS_HIDDEN);
    document.getElementById('probe-progress').classList.remove(CLASS_HIDDEN);
    state.probeDataDisplayed = false;  // Reset for new probing session

    pollProbeStatus();
}

// Reset probe UI to initial state (shared by stop and dismiss)
function resetProbeUI() {
    setText('probe-progress-title', TEXT_PROBING_TITLE);
    removeClass('probe-stop-btn', CLASS_HIDDEN);
    removeClass('probe-pause-btn', CLASS_HIDDEN);
    addClass('probe-done-btn', CLASS_HIDDEN);
    removeClass('probe-setup', CLASS_HIDDEN);
    addClass('probe-progress', CLASS_HIDDEN);
    // Reset pause button to default state
    updateProbePauseButton(false);
}

export async function stopProbing() {
    const { ok, error } = await postJson(API_PROBE_STOP);
    if (!ok) {
        // The tool may still be down, so leave the progress view up rather than showing a
        // setup screen that says the run is over.
        showError(error || ERROR_STOP_NOT_SENT);
        return;
    }

    resetProbeUI();
}

export async function toggleProbePause() {
    const pauseBtn = $('probe-pause-btn');
    if (!pauseBtn) {
        return;
    }

    const { ok, error } = await postJson(
        pauseBtn.dataset.paused === 'true' ? API_PROBE_RESUME : API_PROBE_PAUSE);
    if (!ok) {
        showError(error || TEXT_PAUSE_FAILED);
    }
    // The next status decides what the button says.
}

// Update pause button based on probe status
export function updateProbePauseButton(isPaused) {
    updatePauseButton($('probe-pause-btn'), isPaused, CLASS_BTN_WARNING, CLASS_BTN_SUCCESS);
}

export async function showProbeComplete() {
    // Show completion state - keep grid visible, swap Pause/Stop for Done button
    setText('probe-progress-title', TEXT_PROBING_DONE_TITLE);
    addClass('probe-pause-btn', CLASS_HIDDEN);
    addClass('probe-stop-btn', CLASS_HIDDEN);
    removeClass('probe-done-btn', CLASS_HIDDEN);
    state.probeDataDisplayed = true;  // Mark as displayed to prevent loops

    // Auto-apply probe data to G-code (matches TUI default behavior)
    const { ok, error } = await postJson(API_PROBE_APPLY);
    if (ok) {
        showInfo(TEXT_PROBE_APPLIED_TO_GCODE);
    } else {
        showError(error || TEXT_APPLY_FAILED);
    }
}

export function dismissProbeComplete() {
    resetProbeUI();
    showScreen(SCREEN_DASHBOARD);
}

// Update probe UI from status data (shared by poll and reconnect)
function displayProbeStatus(data) {
    document.getElementById('probe-progress-text').textContent =
        `${data.progress} / ${data.total}`;

    if (data.hasHeights) {
        document.getElementById('probe-height-range').textContent =
            `Z: ${data.minHeight.toFixed(POSITION_DECIMALS_FULL)} to ${data.maxHeight.toFixed(POSITION_DECIMALS_FULL)}`;
    }

    // Ensure grid is created with correct dimensions (needed when browser connects mid-probe or on reconnect)
    if (data.sizeX && data.sizeY) {
        const grid = document.getElementById('probe-grid');
        const expectedCells = data.sizeX * data.sizeY;
        // Re-render if grid is empty OR has wrong number of cells (stale from page sleep)
        if (grid.children.length !== expectedCells) {
            renderProbeGrid(data.sizeX, data.sizeY);
        }
    }

    // Update grid visualization with height-based colors
    if (data.points) {
        updateProbeGridDisplay(data.points, data.colours);
    }

    // Update pause button based on paused state
    if (data.paused !== undefined) {
        updateProbePauseButton(data.paused);
    }
}

export async function pollProbeStatus() {
    // Prevent multiple poll loops
    if (state.isProbePollRunning) return;
    state.isProbePollRunning = true;

    try {
        // The server's answer ends the loop, so a dropped status broadcast cannot leave it
        // spinning.
        for (;;) {
            try {
                const response = await fetch(API_PROBE_STATUS);
                const data = await response.json();

                if (!data.active) {
                    break;
                }

                displayProbeStatus(data);

                await new Promise(resolve => setTimeout(resolve, PROBE_POLL_INTERVAL_MS));
            } catch (err) {
                console.error('Failed to get probe status:', err);
                await new Promise(resolve => setTimeout(resolve, PROBE_POLL_INTERVAL_MS));
            }
        }
    } finally {
        state.isProbePollRunning = false;
    }
    // Note: completion handling is done in updateStatus based on status.probing flag
}

// Paints the measured cells in the colours the server computed. The gradient itself
// lives in HeightGradient.cs, so this view and the terminal's draw the same board.
function updateProbeGridDisplay(points, colours) {
    document.querySelectorAll('.probe-cell').forEach(cell => {
        const x = parseInt(cell.dataset.x);
        const y = parseInt(cell.dataset.y);

        if (points[x] && points[x][y] !== null) {
            cell.classList.add(CLASS_PROBED);
            cell.style.backgroundColor = colours?.[x]?.[y] ?? '';
        }
    });
}

export async function fetchAndDisplayProbeData() {
    try {
        const response = await fetch(API_PROBE_STATUS);
        const data = await response.json();

        if (data.sizeX && data.sizeY && data.points) {
            const isComplete = data.state === PROBE_STATE_COMPLETE;

            if (isComplete) {
                // Complete probe: show progress view with Done button
                document.getElementById('probe-setup').classList.add(CLASS_HIDDEN);
                document.getElementById('probe-progress').classList.remove(CLASS_HIDDEN);
                displayProbeStatus(data);
                await showProbeComplete();
            } else {
                // Partial probe: show setup view with grid and Continue button
                document.getElementById('probe-setup').classList.remove(CLASS_HIDDEN);
                document.getElementById('probe-progress').classList.add(CLASS_HIDDEN);
                updateProbeInfoDisplay(data.sizeX, data.sizeY, data.total, data.progress);
                renderProbeGrid(data.sizeX, data.sizeY);
                // Update grid cells with existing probe data
                if (data.points) {
                    updateProbeGridDisplay(data.points, data.colours);
                }
                // State machine will enable Continue button via status updates
                updateProbeButtonsFromState(data.state, data.hasUnsavedData);
            }

            // Warn if the source G-Code file is missing
            if (data.sourceGCodeMissing) {
                showError(TEXT_SOURCE_GCODE_MISSING);
            }
        }
    } catch (err) {
        console.error('Failed to fetch probe data:', err);
    }
}

// --- Probe Data Save/Load/Clear ---

export function saveProbeData() {
    showProbeFileBrowser('save');
}

function generateDefaultProbeName() {
    const now = new Date();
    const pad = n => n.toString().padStart(2, '0');
    return `probe-${now.getFullYear()}-${pad(now.getMonth()+1)}-${pad(now.getDate())}-${pad(now.getHours())}-${pad(now.getMinutes())}${PROBE_FILE_EXTENSION}`;
}

// Normalize probe filename: add extension if missing
function normalizeProbeFilename(filename) {
    if (!filename.endsWith(PROBE_FILE_EXTENSION)) {
        return filename + PROBE_FILE_EXTENSION;
    }
    return filename;
}

// Save probe data to path, returns true on success
async function saveProbeDataToPath(path) {
    const { ok, error } = await postJson(API_PROBE_SAVE, { path });
    if (!ok) {
        showError(error || TEXT_PROBE_SAVE_FAILED);
        return false;
    }

    showInfo(TEXT_PROBE_DATA_SAVED);
    return true;
}

// Reset probe UI to initial setup state (clear grid, show setup view)
function clearProbeGridUI() {
    $('probe-grid').innerHTML = '';
    $('probe-info').textContent = '';
    removeClass('probe-setup', CLASS_HIDDEN);
    addClass('probe-progress', CLASS_HIDDEN);
    state.probeDataDisplayed = false;
}

/** Discards the probe data, and says whether the server agreed to. */
async function discardOnServer() {
    const { ok, error } = await postJson(API_PROBE_DISCARD);
    if (!ok) {
        showError(error || TEXT_DISCARD_FAILED);
    }
    return ok;
}

export async function discardProbeData() {
    if (!await showConfirm(TEXT_DISCARD_CONFIRM, TEXT_DISCARD_TITLE)) {
        return;
    }

    if (!await discardOnServer()) {
        return;
    }

    showInfo(TEXT_PROBE_DATA_CLEARED);
    clearProbeGridUI();
    // The now-empty grid decides every button on this screen.
    await refreshProbeState();
}

export async function recoverAutosave() {
    const { ok, error, data } = await postJson(API_PROBE_RECOVER_AUTOSAVE);
    if (!ok) {
        showError(error || TEXT_RECOVERY_FAILED);
        return;
    }

    state.probeDataDisplayed = true;
    showInfo(TEXT_PROBE_RECOVERED.replace('{0}', data.progress).replace('{1}', data.total));
    await fetchAndDisplayProbeData();
}


// --- Probe File Browser ---

// Probe file browser state (mode-specific behavior beyond FileBrowser)
let probeFileBrowserMode = 'load'; // 'load' or 'save'
let probeFileBrowser = null;

function getProbeFileBrowser() {
    if (!probeFileBrowser) {
        probeFileBrowser = new FileBrowser({
            listElementId: 'probe-file-list',
            pathElementId: 'probe-files-path',
            apiEndpoint: API_PROBE_FILES,
            fileIcon: '📊',
            metaField: 'modified',
            onFileSelect: onProbeFileSelect,
            onFileLoad: onProbeFileLoad
        });
    }
    return probeFileBrowser;
}

function onProbeFileSelect(path) {
    if (probeFileBrowserMode === 'save') {
        // In save mode, populate filename input with selected file's name
        const filename = path.split(/[/\\]/).pop();
        $('probe-save-input').value = filename;
    } else {
        // In load mode, enable the action button
        $('probe-file-action-btn').disabled = false;
    }
}

function onProbeFileLoad(path) {
    handleProbeFileAction();
}

export async function showProbeFileBrowser(mode = 'load') {
    probeFileBrowserMode = mode;

    // Update UI for mode
    const titleEl = $('probe-files-title');
    const saveRow = $('probe-save-row');
    const actionBtn = $('probe-file-action-btn');
    const saveInput = $('probe-save-input');

    if (mode === 'save') {
        titleEl.textContent = TEXT_SAVE_PROBE_DATA;
        saveRow.classList.remove(CLASS_HIDDEN);
        actionBtn.textContent = TEXT_SAVE;
        actionBtn.disabled = false; // Enable immediately for save (can type filename)
        saveInput.value = generateDefaultProbeName();
        saveInput.focus();
    } else {
        titleEl.textContent = TEXT_LOAD_PROBE_DATA;
        saveRow.classList.add(CLASS_HIDDEN);
        actionBtn.textContent = TEXT_LOAD;
        actionBtn.disabled = true; // Disabled until file selected
    }

    showScreen(SCREEN_PROBE_FILES);
    await getProbeFileBrowser().load();
}

export async function loadSelectedProbeFile() {
    const selectedFile = getProbeFileBrowser().getSelectedFile();
    if (!selectedFile) {
        return;
    }

    const btn = $('probe-file-action-btn');
    btn.disabled = true;
    btn.textContent = TEXT_LOADING;

    try {
        const { ok, error, data } = await postJson(API_PROBE_LOAD, { path: selectedFile });

        if (ok) {
            // Mark as displayed to prevent auto-navigate when user goes to dashboard
            // (user explicitly loaded a file, they're in control)
            state.probeDataDisplayed = true;

            if (data.complete) {
                // Complete grid: go to dashboard (user likely wants to mill)
                showScreen(SCREEN_DASHBOARD);
                const appliedMsg = data.applied ? TEXT_PROBE_DATA_APPLIED : '';
                showInfo(`${TEXT_PROBE_DATA_LOADED}: ${data.sizeX}x${data.sizeY} points${appliedMsg}`);
            } else {
                // Partial grid: stay on probe screen (user likely wants to continue probing)
                showScreen(SCREEN_PROBE);
                showInfo(`${TEXT_PROBE_DATA_LOADED}: ${data.progress}/${data.totalPoints} points probed`);
            }
        } else {
            showError(error || TEXT_PROBE_LOAD_FAILED);
        }
    } catch (err) {
        console.error('probe data load failed', err);
        showError(TEXT_PROBE_LOAD_FAILED);
    } finally {
        btn.disabled = false;
        btn.textContent = TEXT_LOAD;
    }
}

// Update the probe info display with grid size and progress
function updateProbeInfoDisplay(sizeX, sizeY, totalPoints, progress) {
    const infoEl = document.getElementById('probe-info');
    const pct = Math.round((progress / totalPoints) * 100);
    if (progress === totalPoints) {
        infoEl.textContent = `Grid: ${sizeX}x${sizeY} = ${totalPoints} points (complete)`;
    } else if (progress > 0) {
        infoEl.textContent = `Grid: ${sizeX}x${sizeY} = ${totalPoints} points (${progress} probed, ${pct}%)`;
    } else {
        infoEl.textContent = `Grid: ${sizeX}x${sizeY} = ${totalPoints} points`;
    }
}

export function initProbeScreen() {
    $('probe-setup-btn').addEventListener('click', setupProbeGrid);
    $('probe-trace-btn').addEventListener('click', traceOutline);
    // One handler: the button is a stop during a trace and a start otherwise, and that
    // is one fact. A second handler slot on the same button fires alongside this one.
    $('probe-start-btn').addEventListener('click', () => {
        if (getIsTracing()) {
            stopTrace();
        } else {
            startProbing();
        }
    });
    $('probe-pause-btn').addEventListener('click', toggleProbePause);
    $('probe-stop-btn').addEventListener('click', stopProbing);
    $('probe-done-btn').addEventListener('click', dismissProbeComplete);

    // Probe data management buttons
    const saveBtn = $('probe-save-btn');
    const loadBtn = $('probe-load-btn');
    const recoverBtn = $('probe-recover-btn');
    const discardBtn = $('probe-discard-btn');

    if (saveBtn) saveBtn.addEventListener('click', saveProbeData);
    if (loadBtn) loadBtn.addEventListener('click', () => showProbeFileBrowser('load'));
    if (recoverBtn) recoverBtn.addEventListener('click', recoverAutosave);
    if (discardBtn) discardBtn.addEventListener('click', discardProbeData);

    // Update button states based on probe state
    refreshProbeState();
}

export function initProbeFilesScreen() {
    const backBtn = $('probe-files-back-btn');
    const actionBtn = $('probe-file-action-btn');

    if (backBtn) backBtn.addEventListener('click', () => showScreen(SCREEN_PROBE));
    if (actionBtn) actionBtn.addEventListener('click', handleProbeFileAction);
}

async function handleProbeFileAction() {
    if (probeFileBrowserMode === 'save') {
        await saveProbeToFile();
    } else {
        await loadSelectedProbeFile();
    }
}

async function saveProbeToFile() {
    const saveInput = $('probe-save-input');
    const rawFilename = saveInput.value.trim();

    if (!rawFilename) {
        showError(TEXT_ENTER_FILENAME);
        return;
    }

    const filename = normalizeProbeFilename(rawFilename);

    // Prepend current path if not absolute
    let fullPath = filename;
    const currentPath = getProbeFileBrowser().currentPath;
    if (currentPath && !filename.startsWith('/')) {
        fullPath = currentPath + '/' + filename;
    }

    const btn = $('probe-file-action-btn');
    btn.disabled = true;
    btn.textContent = TEXT_SAVING;

    try {
        if (await saveProbeDataToPath(fullPath)) {
            // Go to Dashboard after save - probe data stays in memory
            showScreen(SCREEN_DASHBOARD, true);
        }
    } catch (err) {
        console.error('probe data save failed', err);
        showError(TEXT_PROBE_SAVE_FAILED);
    } finally {
        btn.disabled = false;
        btn.textContent = TEXT_SAVE;
    }
}

/**
 * Holds the probe screen while a trace owns the machine: the start button becomes the stop
 * and nothing else takes a tap. Returns true when it took the screen, so the caller stops.
 */
export function applyProbeRunLock() {
    const setup = $('probe-setup');
    const startBtn = $('probe-start-btn');
    const backBtn = $('probe-back-btn');
    const tracing = getIsTracing();

    if (backBtn) {
        backBtn.disabled = state.isProbing || tracing;
    }

    if (startBtn) {
        startBtn.classList.toggle(CLASS_BTN_DANGER, tracing);
        startBtn.classList.toggle(CLASS_BTN_SUCCESS, !tracing);
    }

    // The setup inputs are the only controls nothing else sets, so they are set both ways
    // here. Its buttons are all set by the state switch below, which re-enables them.
    if (setup) {
        setup.querySelectorAll('input, select').forEach(el => { el.disabled = tracing; });
    }

    if (!tracing || !setup) {
        return false;
    }

    if (startBtn) {
        startBtn.textContent = TEXT_STOP;
    }

    // Only the setup view: a trace never shows the progress view, whose stop and pause
    // belong to a grid probe.
    setup.querySelectorAll('button').forEach(el => {
        el.disabled = el.id !== 'probe-start-btn';
    });
    return true;
}

// Sets the probe buttons from the state the server computed. The four states, what each
// means and which buttons each allows are defined once, in the remarks block at the top of
// coppercli.Core/Controllers/ProbeController.cs.
export function updateProbeButtonsFromState(probeState, hasUnsavedData = false) {
    // Decided first and returned on, so nothing below can paint over a running trace.
    if (applyProbeRunLock()) {
        return;
    }

    const setupBtn = $('probe-setup-btn');
    const startBtn = $('probe-start-btn');
    const traceBtn = $('probe-trace-btn');
    const saveBtn = $('probe-save-btn');
    const recoverBtn = $('probe-recover-btn');
    const discardBtn = $('probe-discard-btn');
    const loadBtn = $('probe-load-btn');

    // Recover button: enabled when autosave exists
    if (recoverBtn) recoverBtn.disabled = !hasUnsavedData;

    // Trace button: there has to be a grid to walk the outline of.
    if (traceBtn) traceBtn.disabled = probeState === PROBE_STATE_NONE;

    switch (probeState) {
        case PROBE_STATE_NONE:
            // No grid - need to set up first
            if (setupBtn) setupBtn.disabled = false;
            if (startBtn) {
                startBtn.textContent = TEXT_START_PROBING;
                startBtn.disabled = true;
            }
            if (saveBtn) saveBtn.disabled = true;
            if (discardBtn) {
                discardBtn.disabled = true;
                discardBtn.textContent = TEXT_DISCARD;
            }
            if (loadBtn) loadBtn.disabled = false;
            break;

        case PROBE_STATE_READY:
            // Grid exists, ready to start probing
            if (setupBtn) setupBtn.disabled = false;
            if (startBtn) {
                startBtn.textContent = TEXT_START_PROBING;
                startBtn.disabled = false;
            }
            if (saveBtn) saveBtn.disabled = true;
            if (discardBtn) {
                discardBtn.disabled = true;
                discardBtn.textContent = TEXT_DISCARD;
            }
            if (loadBtn) loadBtn.disabled = false;
            break;

        case PROBE_STATE_PARTIAL:
            // Incomplete: [Continue] [Discard if unsaved]
            if (setupBtn) setupBtn.disabled = true;
            if (startBtn) {
                startBtn.textContent = TEXT_CONTINUE_PROBING;
                startBtn.disabled = false;
            }
            if (saveBtn) saveBtn.disabled = true;
            if (discardBtn) {
                discardBtn.disabled = !hasUnsavedData;
                discardBtn.textContent = TEXT_DISCARD;
            }
            if (loadBtn) loadBtn.disabled = false;
            break;

        case PROBE_STATE_COMPLETE:
            // Complete: [Save]* / [Clear]
            if (setupBtn) setupBtn.disabled = false;
            if (startBtn) {
                startBtn.textContent = TEXT_START_PROBING;
                startBtn.disabled = true;
            }
            if (saveBtn) saveBtn.disabled = !hasUnsavedData;
            if (discardBtn) {
                discardBtn.disabled = false;
                discardBtn.textContent = hasUnsavedData ? TEXT_DISCARD : TEXT_CLEAR;
            }
            if (loadBtn) loadBtn.disabled = false;
            break;
    }

}

// Fetch probe state from server and update buttons
export async function refreshProbeState() {
    try {
        const response = await fetch(API_PROBE_STATUS);
        const data = await response.json();
        if (data.state) {
            updateProbeButtonsFromState(data.state, data.hasUnsavedData);
            // Clear stale grid if server has no probe data (e.g., discarded via zeroing)
            if (data.state === PROBE_STATE_NONE) {
                clearProbeGridUI();
            }
        }
    } catch (err) {
        console.error('Failed to fetch probe state:', err);
    }
}

// --- Probe Save Modal (for unsaved completed probes) ---

export function showProbeSaveModal() {
    const modal = $('probe-save-modal');
    modal.classList.remove(CLASS_HIDDEN);
}

export function hideProbeSaveModal() {
    const modal = $('probe-save-modal');
    modal.classList.add(CLASS_HIDDEN);
}

function handleProbeSaveConfirm() {
    // Hide modal and open file browser in save mode
    hideProbeSaveModal();
    showProbeFileBrowser('save');
}

async function handleProbeSaveDiscard() {
    if (!await discardOnServer()) {
        return;
    }

    showInfo(TEXT_PROBE_DATA_CLEARED);
    hideProbeSaveModal();
    clearProbeGridUI();
}

export function initProbeSaveModal() {
    const confirmBtn = $('probe-save-confirm-btn');
    const discardBtn = $('probe-save-discard-btn');

    if (confirmBtn) {
        confirmBtn.addEventListener('click', handleProbeSaveConfirm);
    }
    if (discardBtn) {
        discardBtn.addEventListener('click', handleProbeSaveDiscard);
    }
}

// Check for unsaved/incomplete probe on startup and show appropriate modal
// Only shows modal if probing/milling is NOT actively running
export async function checkAndShowUnsavedProbe() {
    try {
        // First check if milling is in progress - don't show probe modals during milling/tool change
        const statusResponse = await fetch(API_STATUS);
        const status = await statusResponse.json();
        if (status.milling) {
            return false;
        }

        const response = await fetch(API_PROBE_STATUS);
        const data = await response.json();

        // Don't show recovery/save modals while the machine is on the board.
        if (status.probing || status.tracingOutline) {
            return false;
        }

        if (data.state === PROBE_STATE_PARTIAL) {
            // Incomplete probe: show recovery modal
            showProbeRecoveryModal();
            return true;
        }

        if (data.state === PROBE_STATE_COMPLETE && data.hasUnsavedData) {
            // Complete probe with unsaved data: show save modal
            showProbeSaveModal();
            return true;
        }
    } catch (err) {
        console.error('Check unsaved probe failed:', err);
    }
    return false;
}

// --- Probe Recovery Modal (for incomplete probes) ---

function showProbeRecoveryModal() {
    const modal = $('probe-recovery-modal');
    modal.classList.remove(CLASS_HIDDEN);
}

function hideProbeRecoveryModal() {
    const modal = $('probe-recovery-modal');
    modal.classList.add(CLASS_HIDDEN);
}

async function handleProbeRecoveryContinue() {
    hideProbeRecoveryModal();
    // Navigate to probe screen and show the partial grid
    showScreen(SCREEN_PROBE, true);
    await fetchAndDisplayProbeData();
}

async function handleProbeRecoveryDiscard() {
    if (!await discardOnServer()) {
        return;
    }

    hideProbeRecoveryModal();
    showScreen(SCREEN_DASHBOARD);
}

export function initProbeRecoveryModal() {
    const continueBtn = $('probe-recovery-continue-btn');
    const discardBtn = $('probe-recovery-discard-btn');

    if (continueBtn) {
        continueBtn.addEventListener('click', handleProbeRecoveryContinue);
    }
    if (discardBtn) {
        discardBtn.addEventListener('click', handleProbeRecoveryDiscard);
    }
}
