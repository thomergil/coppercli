import { state } from './state.js';
import { $, setText, addClass, removeClass, showError, showInfo, showConfirm, FileBrowser, updatePauseButton, postJson, postOrShowError, format } from './helpers.js';
import { showScreen } from './screens.js';
import {
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
    TEXT_GRID_SUMMARY,
    TEXT_GRID_COMPLETE,
    TEXT_GRID_PROGRESS,
    TEXT_GRID_SIZE_UNKNOWN,
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

    const { ok, data } = await postOrShowError(
        API_PROBE_SETUP, TEXT_SETUP_FAILED, { margin, gridSize });
    if (!ok) {
        return;
    }

    updateProbeInfoDisplay(data.sizeX, data.sizeY, data.totalPoints, 0);
    renderProbeGrid(data.sizeX, data.sizeY);
    // The new grid decides every button on this screen, so read it back rather than
    // setting them here too.
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

// Track trace start and stop until the next server status arrives. Null means the
// server's value applies.
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

    applyProbeRunLock();

    try {
        const { ok } = await postOrShowError(API_PROBE_TRACE, TEXT_TRACE_FAILED);
        if (!ok) {
            return;
        }

        await pollTraceStatus();
    } finally {
        traceOverride = false;

        await refreshProbeState();

        // Keep the start button disabled a moment longer in case of a second tap on STOP,
        // then let the state decide again rather than forcing it enabled.
        startBtn.disabled = true;
        setTimeout(() => { refreshProbeState(); }, TRACE_BUTTON_SETTLE_MS);
    }
}

async function stopTrace() {
    // Stops the status poll putting the probe progress view up once the trace ends.
    state.probeDataDisplayed = true;

    // The server reports whether it confirmed the machine stopped, the same value
    // stopProbing reads. The finally in traceOutline restores the button either way.
    await postOrShowError(API_PROBE_STOP, ERROR_STOP_NOT_SENT);
}

// Ends when the server reports the trace over. One failed request must not unlock the
// screen while the tool is still moving, but a server that has stopped responding is
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
    const { ok } = await postOrShowError(API_PROBE_START, ERROR_PROBE_NOT_STARTED);
    if (!ok) {
        // Nothing is probing, so leave the setup view up.
        return;
    }

    document.getElementById('probe-setup').classList.add(CLASS_HIDDEN);
    document.getElementById('probe-progress').classList.remove(CLASS_HIDDEN);
    state.probeDataDisplayed = false;

    pollProbeStatus();
}

function resetProbeUI() {
    setText('probe-progress-title', TEXT_PROBING_TITLE);
    removeClass('probe-stop-btn', CLASS_HIDDEN);
    removeClass('probe-pause-btn', CLASS_HIDDEN);
    addClass('probe-done-btn', CLASS_HIDDEN);
    removeClass('probe-setup', CLASS_HIDDEN);
    addClass('probe-progress', CLASS_HIDDEN);
    updateProbePauseButton(false);
}

export async function stopProbing() {
    const { ok } = await postOrShowError(API_PROBE_STOP, ERROR_STOP_NOT_SENT);
    if (!ok) {
        // The tool may still be down, so leave the progress view up rather than a setup
        // screen that implies the run is over.
        return;
    }

    resetProbeUI();
}

export async function toggleProbePause() {
    const pauseBtn = $('probe-pause-btn');
    if (!pauseBtn) {
        return;
    }

    await postOrShowError(
        pauseBtn.dataset.paused === 'true' ? API_PROBE_RESUME : API_PROBE_PAUSE, TEXT_PAUSE_FAILED);
    // The next status sets the button text.
}

export function updateProbePauseButton(isPaused) {
    updatePauseButton($('probe-pause-btn'), isPaused, CLASS_BTN_WARNING, CLASS_BTN_SUCCESS);
}

export async function showProbeComplete() {
    setText('probe-progress-title', TEXT_PROBING_DONE_TITLE);
    addClass('probe-pause-btn', CLASS_HIDDEN);
    addClass('probe-stop-btn', CLASS_HIDDEN);
    removeClass('probe-done-btn', CLASS_HIDDEN);
    state.probeDataDisplayed = true;

    // Applied without asking, as the terminal does by default.
    const { ok } = await postOrShowError(API_PROBE_APPLY, TEXT_APPLY_FAILED);
    if (ok) {
        showInfo(TEXT_PROBE_APPLIED_TO_GCODE);
    }
}

/**
 * Call when a probe run has just ended: shows the map and, when it is complete, opens the
 * save screen, as the terminal does. Not for a page that only reconnects onto a map.
 */
export async function showProbeCompleteAndOfferSave() {
    if (await fetchAndDisplayProbeData()) {
        await saveProbeData();
    }
}

export function dismissProbeComplete() {
    resetProbeUI();
    showScreen(SCREEN_DASHBOARD);
}

function displayProbeStatus(data) {
    document.getElementById('probe-progress-text').textContent =
        `${data.progress} / ${data.total}`;

    if (data.hasHeights) {
        document.getElementById('probe-height-range').textContent =
            `Z: ${data.minHeight.toFixed(POSITION_DECIMALS_FULL)} to ${data.maxHeight.toFixed(POSITION_DECIMALS_FULL)}`;
    }

    // The grid may not exist yet: this page can connect in the middle of a probe.
    if (data.sizeX && data.sizeY) {
        const grid = document.getElementById('probe-grid');
        const expectedCells = data.sizeX * data.sizeY;
        // A page that slept can come back with a grid of the wrong size.
        if (grid.children.length !== expectedCells) {
            renderProbeGrid(data.sizeX, data.sizeY);
        }
    }

    if (data.points) {
        updateProbeGridDisplay(data.points, data.colors);
    }

    if (data.paused !== undefined) {
        updateProbePauseButton(data.paused);
    }
}

export async function pollProbeStatus() {
    if (state.isProbePollRunning) return;
    state.isProbePollRunning = true;

    try {
        // The server's reply ends the loop, so a dropped status broadcast cannot leave it
        // running.
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
    // Completion is handled in updateStatus, from the status broadcast.
}

// Paint measured cells using server colors from HeightGradient.cs, shared with the terminal.
function updateProbeGridDisplay(points, colors) {
    document.querySelectorAll('.probe-cell').forEach(cell => {
        const x = parseInt(cell.dataset.x);
        const y = parseInt(cell.dataset.y);

        if (points[x] && points[x][y] !== null) {
            cell.classList.add(CLASS_PROBED);
            cell.style.backgroundColor = colors?.[x]?.[y] ?? '';
        }
    });
}

/** Resolves true when a complete map is now on screen. */
export async function fetchAndDisplayProbeData() {
    try {
        const response = await fetch(API_PROBE_STATUS);
        const data = await response.json();

        if (data.sizeX && data.sizeY && data.points) {
            const isComplete = data.state === PROBE_STATE_COMPLETE;

            if (isComplete) {
                document.getElementById('probe-setup').classList.add(CLASS_HIDDEN);
                document.getElementById('probe-progress').classList.remove(CLASS_HIDDEN);
                displayProbeStatus(data);
                await showProbeComplete();
            } else {
                document.getElementById('probe-setup').classList.remove(CLASS_HIDDEN);
                document.getElementById('probe-progress').classList.add(CLASS_HIDDEN);
                updateProbeInfoDisplay(data.sizeX, data.sizeY, data.total, data.progress);
                renderProbeGrid(data.sizeX, data.sizeY);
                if (data.points) {
                    updateProbeGridDisplay(data.points, data.colors);
                }
                updateProbeButtonsFromState(data.state, data.hasUnsavedData);
            }

            if (data.sourceGCodeMissing) {
                showError(TEXT_SOURCE_GCODE_MISSING);
            }

            return isComplete;
        }
    } catch (err) {
        console.error('Failed to fetch probe data:', err);
    }
    return false;
}


export function saveProbeData() {
    return showProbeFileBrowser('save');
}

// The server supplies the name, so the terminal and the browser offer the same one. Empty if
// the server cannot be reached; the operator types a name.
async function suggestedProbeFileName() {
    try {
        const response = await fetch(API_PROBE_STATUS);
        const data = await response.json();
        return data.suggestedFileName || '';
    } catch (err) {
        console.error('Reading the suggested map name failed', err);
        return '';
    }
}

function normalizeProbeFilename(filename) {
    if (!filename.endsWith(PROBE_FILE_EXTENSION)) {
        return filename + PROBE_FILE_EXTENSION;
    }
    return filename;
}

async function saveProbeDataToPath(path) {
    let { ok, error, data } = await postJson(API_PROBE_SAVE, { path });

    // The server asks before replacing a file; its error is the question.
    if (!ok && data.fileExists) {
        if (!await showConfirm(error)) {
            return false;
        }
        ({ ok, error } = await postJson(API_PROBE_SAVE, { path, overwrite: true }));
    }

    if (!ok) {
        showError(error || TEXT_PROBE_SAVE_FAILED);
        return false;
    }

    showInfo(TEXT_PROBE_DATA_SAVED);
    return true;
}

function clearProbeGridUI() {
    $('probe-grid').innerHTML = '';
    $('probe-info').textContent = '';
    removeClass('probe-setup', CLASS_HIDDEN);
    addClass('probe-progress', CLASS_HIDDEN);
    state.probeDataDisplayed = false;
}

async function discardOnServer() {
    const { ok } = await postOrShowError(API_PROBE_DISCARD, TEXT_DISCARD_FAILED);
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
    const { ok, data } = await postOrShowError(API_PROBE_RECOVER_AUTOSAVE, TEXT_RECOVERY_FAILED);
    if (!ok) {
        return;
    }

    state.probeDataDisplayed = true;
    showInfo(format(TEXT_PROBE_RECOVERED, data.progress, data.total));
    await fetchAndDisplayProbeData();
}


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
        const filename = path.split(/[/\\]/).pop();
        $('probe-save-input').value = filename;
    } else {
        $('probe-file-action-btn').disabled = false;
    }
}

function onProbeFileLoad(path) {
    handleProbeFileAction();
}

export async function showProbeFileBrowser(mode = 'load') {
    probeFileBrowserMode = mode;

    const titleEl = $('probe-files-title');
    const saveRow = $('probe-save-row');
    const actionBtn = $('probe-file-action-btn');
    const saveInput = $('probe-save-input');

    if (mode === 'save') {
        titleEl.textContent = TEXT_SAVE_PROBE_DATA;
        saveRow.classList.remove(CLASS_HIDDEN);
        actionBtn.textContent = TEXT_SAVE;
        actionBtn.disabled = false; // A name can be typed, so nothing has to be selected
        saveInput.value = await suggestedProbeFileName();
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
        const { ok, data } = await postOrShowError(
            API_PROBE_LOAD, TEXT_PROBE_LOAD_FAILED, { path: selectedFile });

        if (ok) {
            // The operator loaded this file, so the dashboard must not navigate away from it.
            state.probeDataDisplayed = true;

            if (data.complete) {
                // A complete grid: the next step is milling.
                showScreen(SCREEN_DASHBOARD);
                const appliedMsg = data.applied ? TEXT_PROBE_DATA_APPLIED : '';
                showInfo(`${TEXT_PROBE_DATA_LOADED}: ${data.sizeX}x${data.sizeY} points${appliedMsg}`);
            } else {
                // A partial grid: the next step is more probing.
                showScreen(SCREEN_PROBE);
                showInfo(`${TEXT_PROBE_DATA_LOADED}: ${data.progress}/${data.totalPoints} points probed`);
            }
        }
    } catch (err) {
        console.error('probe data load failed', err);
        showError(TEXT_PROBE_LOAD_FAILED);
    } finally {
        btn.disabled = false;
        btn.textContent = TEXT_LOAD;
    }
}

/**
 * Format the grid line on the probe setup screen. The status poll in screens.js also
 * writes this element, so both callers use this formatter.
 */
export function updateProbeInfoDisplay(sizeX, sizeY, totalPoints, progress) {
    const summary = format(
        TEXT_GRID_SUMMARY,
        sizeX || TEXT_GRID_SIZE_UNKNOWN,
        sizeY || TEXT_GRID_SIZE_UNKNOWN,
        totalPoints);

    const pct = Math.round((progress / totalPoints) * 100);
    setText('probe-info',
        progress === totalPoints ? format(TEXT_GRID_COMPLETE, summary)
        : progress > 0 ? format(TEXT_GRID_PROGRESS, summary, progress, pct)
        : summary);
}

export function initProbeScreen() {
    $('probe-setup-btn').addEventListener('click', setupProbeGrid);
    $('probe-trace-btn').addEventListener('click', traceOutline);
    // One handler: the button stops a trace, and starts a probe otherwise. A second handler
    // on the same button would fire alongside this one.
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

    const saveBtn = $('probe-save-btn');
    const loadBtn = $('probe-load-btn');
    const recoverBtn = $('probe-recover-btn');
    const discardBtn = $('probe-discard-btn');

    if (saveBtn) saveBtn.addEventListener('click', saveProbeData);
    if (loadBtn) loadBtn.addEventListener('click', () => showProbeFileBrowser('load'));
    if (recoverBtn) recoverBtn.addEventListener('click', recoverAutosave);
    if (discardBtn) discardBtn.addEventListener('click', discardProbeData);

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
            // Saving does not clear the probe data.
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
 * Holds the probe screen while a trace is running: the start button becomes the stop and
 * nothing else takes a tap. Returns true when it took the screen, so the caller stops.
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

    if (recoverBtn) recoverBtn.disabled = !hasUnsavedData;

    // Trace button: there has to be a grid to walk the outline of.
    if (traceBtn) traceBtn.disabled = probeState === PROBE_STATE_NONE;

    switch (probeState) {
        case PROBE_STATE_NONE:
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

export async function refreshProbeState() {
    try {
        const response = await fetch(API_PROBE_STATUS);
        const data = await response.json();
        if (data.state) {
            updateProbeButtonsFromState(data.state, data.hasUnsavedData);
            // The grid goes stale when something else discarded the data, such as zeroing X/Y.
            if (data.state === PROBE_STATE_NONE) {
                clearProbeGridUI();
            }
        }
    } catch (err) {
        console.error('Failed to fetch probe state:', err);
    }
}


