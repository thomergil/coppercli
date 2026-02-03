// coppercli Web UI Probe Screen

import { state } from './state.js';
import { $, setText, addClass, removeClass, showError, showInfo, showConfirm, FileBrowser, updatePauseButton } from './helpers.js';
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
    CLASS_PROBED,
    CLASS_SELECTED,
    PROBE_FILE_EXTENSION,
    PROBE_STATE_NONE,
    PROBE_STATE_READY,
    PROBE_STATE_PARTIAL,
    PROBE_STATE_COMPLETE,
    TEXT_UNKNOWN,
    TEXT_PROBING_COMPLETE,
    TEXT_PROBING_TITLE,
    TEXT_PROBING_DONE_TITLE,
    TEXT_PROBE_DATA_SAVED,
    TEXT_PROBE_DATA_LOADED,
    TEXT_PROBE_DATA_APPLIED,
    TEXT_PROBE_DATA_CLEARED,
    TEXT_NO_PROBE_DATA,
    TEXT_NO_FILES,
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
    TEXT_START_PROBING,
    TEXT_CONTINUE_PROBING,
    PROBE_POLL_INTERVAL_MS,
    PROBE_GRID_CELL_SIZE_PX,
    POSITION_DECIMALS_FULL,
    COLOR_GRADIENT_STEP,
    COLOR_MAX_VALUE,
    HEIGHT_RANGE_EPSILON
} from './constants.js';

export async function setupProbeGrid() {
    const margin = parseFloat(document.getElementById('probe-margin').value) || state.probeDefaults.margin;
    const gridSize = parseFloat(document.getElementById('probe-grid-size').value) || state.probeDefaults.gridSize;

    try {
        const response = await fetch(API_PROBE_SETUP, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ margin, gridSize })
        });

        const data = await response.json();

        if (data.success) {
            updateProbeInfoDisplay(data.sizeX, data.sizeY, data.totalPoints, 0);
            document.getElementById('probe-trace-btn').disabled = false;
            const startBtn = document.getElementById('probe-start-btn');
            startBtn.disabled = false;
            // Fresh grid always says "Start" (not "Continue" which is for interrupted probes)
            startBtn.textContent = TEXT_START_PROBING;
            // Note: Save/Clear buttons controlled by status updates based on probe progress
            renderProbeGrid(data.sizeX, data.sizeY);
        } else {
            showError('Setup failed: ' + (data.error || TEXT_UNKNOWN));
        }
    } catch (err) {
        showError('Setup failed: ' + err.message);
    }
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

// Track if trace is in progress (to prevent status handlers from interfering)
let isTracing = false;

export function getIsTracing() {
    return isTracing;
}

export async function traceOutline() {
    const startBtn = document.getElementById('probe-start-btn');
    const traceBtn = document.getElementById('probe-trace-btn');

    isTracing = true;

    // Transform Start Probing button into a red Stop button
    startBtn.textContent = 'STOP';
    startBtn.classList.remove('btn-success');
    startBtn.classList.add('btn-danger');
    startBtn.onclick = stopTrace;

    // Disable trace button during trace
    traceBtn.disabled = true;

    try {
        const response = await fetch(API_PROBE_TRACE, { method: 'POST' });
        const data = await response.json();
        if (!data.success) {
            showError('Trace failed: ' + (data.error || TEXT_UNKNOWN));
        }

        // Poll until trace is complete
        await pollTraceStatus();
    } catch (err) {
        showError('Trace failed: ' + err.message);
    } finally {
        isTracing = false;
        // Restore button to normal state
        startBtn.textContent = 'Start Probing';
        startBtn.classList.remove('btn-danger');
        startBtn.classList.add('btn-success');
        startBtn.onclick = startProbing;
        traceBtn.disabled = false;
    }
}

async function stopTrace() {
    // Prevent status updates from showing probe progress view after trace stops
    state.probeDataDisplayed = true;

    // Just send stop - don't touch probe UI since trace uses setup view
    await fetch(API_PROBE_STOP, { method: 'POST' });
    // The finally block in traceOutline will restore button state
}

async function pollTraceStatus() {
    while (true) {
        try {
            const response = await fetch(API_PROBE_STATUS);
            const data = await response.json();

            // Check if trace phase is done
            if (data.phase !== 'TracingOutline') {
                break;
            }

            await new Promise(resolve => setTimeout(resolve, PROBE_POLL_INTERVAL_MS));
        } catch (err) {
            console.error('Trace poll error:', err);
            break;
        }
    }
}

export async function startProbing() {
    // Don't start probing if trace is active (button click fires both handlers)
    if (isTracing) {
        return;
    }

    document.getElementById('probe-setup').classList.add(CLASS_HIDDEN);
    document.getElementById('probe-progress').classList.remove(CLASS_HIDDEN);
    document.getElementById('probe-back-btn').disabled = true;
    state.probeDataDisplayed = false;  // Reset for new probing session

    await fetch(API_PROBE_START, { method: 'POST' });
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
    document.getElementById('probe-back-btn').disabled = false;
    // Reset pause button to default state
    updateProbePauseButton(false);
}

export async function stopProbing() {
    await fetch(API_PROBE_STOP, { method: 'POST' });
    resetProbeUI();
}

export async function toggleProbePause() {
    const pauseBtn = $('probe-pause-btn');
    if (!pauseBtn) return;

    const isPaused = pauseBtn.dataset.paused === 'true';

    if (isPaused) {
        await fetch(API_PROBE_RESUME, { method: 'POST' });
    } else {
        await fetch(API_PROBE_PAUSE, { method: 'POST' });
    }
    // Button state will be updated by status poll
}

// Update pause button based on probe status
export function updateProbePauseButton(isPaused) {
    updatePauseButton($('probe-pause-btn'), isPaused, 'btn-warning', 'btn-success');
}

export async function showProbeComplete() {
    // Show completion state - keep grid visible, swap Pause/Stop for Done button
    setText('probe-progress-title', TEXT_PROBING_DONE_TITLE);
    addClass('probe-pause-btn', CLASS_HIDDEN);
    addClass('probe-stop-btn', CLASS_HIDDEN);
    removeClass('probe-done-btn', CLASS_HIDDEN);
    state.probeDataDisplayed = true;  // Mark as displayed to prevent loops

    // Auto-apply probe data to G-code (matches TUI default behavior)
    try {
        const response = await fetch(API_PROBE_APPLY, { method: 'POST' });
        const data = await response.json();
        if (data.success) {
            showInfo('Probe data applied to G-code');
        }
    } catch (e) {
        showError('Failed to apply probe data');
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

    if (data.minHeight !== 0 || data.maxHeight !== 0) {
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
        updateProbeGridDisplay(data.points, data.minHeight, data.maxHeight);
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
        while (state.isProbing) {
            try {
                const response = await fetch(API_PROBE_STATUS);
                const data = await response.json();

                if (!data.active) {
                    break; // Server says probing stopped
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

function updateProbeGridDisplay(points, minHeight, maxHeight) {
    const cells = document.querySelectorAll('.probe-cell');
    const hasRange = maxHeight > minHeight && (maxHeight - minHeight) > HEIGHT_RANGE_EPSILON;

    cells.forEach(cell => {
        const x = parseInt(cell.dataset.x);
        const y = parseInt(cell.dataset.y);
        if (points[x] && points[x][y] !== null) {
            cell.classList.add(CLASS_PROBED);
            // Apply height-based color
            const height = points[x][y];
            const color = heightToColor(height, minHeight, maxHeight, hasRange);
            cell.style.backgroundColor = color;
        }
    });
}

// Maps height to color gradient: blue -> cyan -> green -> yellow -> red
// Matches the TUI's HeightToColor function
function heightToColor(height, minHeight, maxHeight, hasRange) {
    if (!hasRange) {
        return `rgb(0, ${COLOR_MAX_VALUE}, 0)`; // Default green if no range
    }

    const t = Math.max(0, Math.min(1, (height - minHeight) / (maxHeight - minHeight)));

    let r, g, b;
    if (t < COLOR_GRADIENT_STEP) {
        // Blue to Cyan
        const s = t / COLOR_GRADIENT_STEP;
        r = 0;
        g = Math.round(COLOR_MAX_VALUE * s);
        b = COLOR_MAX_VALUE;
    } else if (t < COLOR_GRADIENT_STEP * 2) {
        // Cyan to Green
        const s = (t - COLOR_GRADIENT_STEP) / COLOR_GRADIENT_STEP;
        r = 0;
        g = COLOR_MAX_VALUE;
        b = Math.round(COLOR_MAX_VALUE * (1 - s));
    } else if (t < COLOR_GRADIENT_STEP * 3) {
        // Green to Yellow
        const s = (t - COLOR_GRADIENT_STEP * 2) / COLOR_GRADIENT_STEP;
        r = Math.round(COLOR_MAX_VALUE * s);
        g = COLOR_MAX_VALUE;
        b = 0;
    } else {
        // Yellow to Red
        const s = (t - COLOR_GRADIENT_STEP * 3) / COLOR_GRADIENT_STEP;
        r = COLOR_MAX_VALUE;
        g = Math.round(COLOR_MAX_VALUE * (1 - s));
        b = 0;
    }

    return `rgb(${r}, ${g}, ${b})`;
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
                showProbeComplete();
            } else {
                // Partial probe: show setup view with grid and Continue button
                document.getElementById('probe-setup').classList.remove(CLASS_HIDDEN);
                document.getElementById('probe-progress').classList.add(CLASS_HIDDEN);
                updateProbeInfoDisplay(data.sizeX, data.sizeY, data.total, data.progress);
                renderProbeGrid(data.sizeX, data.sizeY);
                // Update grid cells with existing probe data
                if (data.points) {
                    updateProbeGridDisplay(data.points, data.minHeight, data.maxHeight);
                }
                // State machine will enable Continue button via status updates
                updateProbeButtonsFromState(data.state, data.hasUnsavedData);
            }

            // Warn if the source G-Code file is missing
            if (data.sourceGCodeMissing) {
                showError('Original G-Code file is missing. Load the file to continue probing.');
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
    const response = await fetch(API_PROBE_SAVE, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ path })
    });
    const data = await response.json();
    if (data.success) {
        showInfo(TEXT_PROBE_DATA_SAVED);
        return true;
    } else {
        showError(data.error || TEXT_UNKNOWN);
        return false;
    }
}

export async function discardProbeData() {
    if (!await showConfirm('Discard all probe data?', 'Discard Probe Data')) return;

    try {
        await fetch(API_PROBE_DISCARD, { method: 'POST' });
        showInfo(TEXT_PROBE_DATA_CLEARED);
        // Reset UI
        removeClass('probe-setup', CLASS_HIDDEN);
        addClass('probe-progress', CLASS_HIDDEN);
        $('probe-grid').innerHTML = '';
        $('probe-info').textContent = '';
        $('probe-start-btn').disabled = true;
        // Reset display flag so reconnect can show new data
        state.probeDataDisplayed = false;
        // Update buttons based on new state (should be 'none')
        await refreshProbeState();
    } catch (err) {
        showError('Discard failed: ' + err.message);
    }
}

export async function recoverAutosave() {
    try {
        const response = await fetch(API_PROBE_RECOVER_AUTOSAVE, { method: 'POST' });
        const data = await response.json();

        if (data.success) {
            state.probeDataDisplayed = true;
            showInfo(TEXT_PROBE_RECOVERED.replace('{0}', data.progress).replace('{1}', data.total));
            // Refresh display to show the recovered data
            await fetchAndDisplayProbeData();
        } else {
            showError(data.error || TEXT_RECOVERY_FAILED);
        }
    } catch (err) {
        showError(TEXT_RECOVERY_FAILED + ': ' + err.message);
    }
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
        const response = await fetch(API_PROBE_LOAD, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ path: selectedFile })
        });
        const data = await response.json();

        if (data.success) {
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
            showError(data.error || TEXT_UNKNOWN);
        }
    } catch (err) {
        showError(`${TEXT_LOAD} failed: ${err.message}`);
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
    $('probe-start-btn').addEventListener('click', startProbing);
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
        showError(`${TEXT_SAVE} failed: ${err.message}`);
    } finally {
        btn.disabled = false;
        btn.textContent = TEXT_SAVE;
    }
}

// Update probe buttons based on probe state.
// This is the single source of truth for button states.
//
// 4-state model (based on in-memory grid progress):
// ┌─────────────────────────────────────────────────────────────────────────┐
// │  STATE     │ MEANING              │ START BUTTON   │ SAVE/DISCARD      │
// ├────────────┼──────────────────────┼────────────────┼───────────────────┤
// │  none      │ no grid              │ disabled       │ disabled          │
// │  ready     │ grid, progress=0     │ [Start]        │ disabled          │
// │  partial   │ 0 < progress < total │ [Continue]     │ [Discard]*        │
// │  complete  │ progress = total     │ disabled       │ [Save]* / [Clear] │
// └─────────────────────────────────────────────────────────────────────────┘
// * Only if hasUnsavedData (autosave exists)
//
export function updateProbeButtonsFromState(probeState, hasUnsavedData = false) {
    const setupBtn = $('probe-setup-btn');
    const startBtn = $('probe-start-btn');
    const saveBtn = $('probe-save-btn');
    const recoverBtn = $('probe-recover-btn');
    const discardBtn = $('probe-discard-btn');
    const loadBtn = $('probe-load-btn');

    // Recover button: enabled when autosave exists
    if (recoverBtn) recoverBtn.disabled = !hasUnsavedData;

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
    try {
        await fetch(API_PROBE_DISCARD, { method: 'POST' });
        showInfo(TEXT_PROBE_DATA_CLEARED);
        hideProbeSaveModal();
    } catch (err) {
        showError('Discard failed: ' + err.message);
    }
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

        // Don't show recovery/save modals if probing is actively running
        if (data.active) {
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
    try {
        await fetch(API_PROBE_DISCARD, { method: 'POST' });
        hideProbeRecoveryModal();
        showScreen(SCREEN_DASHBOARD);
    } catch (err) {
        showError('Discard failed: ' + err.message);
    }
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
