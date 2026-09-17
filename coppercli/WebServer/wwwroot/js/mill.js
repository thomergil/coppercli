// coppercli Web UI Mill Screen

import { state } from './state.js';
import { $, showError, showConfirm, updatePauseButton, postJson, escapeMarkup, format } from './helpers.js';
import { showScreen } from './screens.js';
import {
    PROMPT_OPTION_CONTINUE,
    PROMPT_OPTION_ABORT,
    ERROR_START_NOT_SENT,
    ERROR_PAUSE_NOT_SENT,
    ERROR_RESUME_NOT_SENT,
    ERROR_STOP_NOT_SENT,
    ERROR_ABORT_NOT_SENT,
    ERROR_INPUT_NOT_SENT,
    ERROR_NOTHING_TO_ANSWER,
    ERROR_FEED_NOT_SENT,
    ERROR_DEPTH_NOT_SET,
    TEXT_LINE_COUNT,
    DEPTH_ACTION_INCREASE,
    DEPTH_ACTION_DECREASE,
    DEPTH_ACTION_RESET,
    MILL_GRID_PADDING_PX,
    MILL_GRID_CELL_GAP_PX,
    MILL_MARKER_CELL_FRACTION,
    PROMPT_SETTLE_MS,
    DOOR_RELEASE_PROMPT_ID,
    API_FILE_INFO,
    API_MILL_CAN_START,
    API_MILL_START,
    API_MILL_PAUSE,
    API_MILL_RESUME,
    API_MILL_STOP,
    API_MILL_TOOLCHANGE_ABORT,
    API_MILL_TOOLCHANGE_INPUT,
    API_MILL_DEPTH,
    API_MILL_GRID,
    API_FEED_INCREASE,
    API_FEED_DECREASE,
    API_FEED_RESET,
    SCREEN_DASHBOARD,
    SCREEN_MILL,
    CLASS_HIDDEN,
    CLASS_DOOR_MESSAGE,
    MSG_TYPE_MILL_STATE,
    MSG_TYPE_MILL_PROGRESS,
    MSG_TYPE_MILL_TOOLCHANGE,
    MSG_TYPE_MILL_ERROR,
    MSG_TYPE_TOOLCHANGE_STATE,
    MSG_TYPE_TOOLCHANGE_PROGRESS,
    MSG_TYPE_TOOLCHANGE_INPUT,
    MSG_TYPE_TOOLCHANGE_COMPLETE,
    MSG_TYPE_TOOLCHANGE_ERROR,
    CONTROLLER_STATE_RUNNING,
    CONTROLLER_STATE_PAUSED,
    CONTROLLER_STATE_WAITING_FOR_USER_INPUT,
    PHASE_MILLING,
    PHASE_WAITING_FOR_ZERO_Z,
    MILL_MIN_RANGE_THRESHOLD,
    API_DOOR_RELEASE,
    ERROR_DOOR_NOT_RELEASED,
    TEXT_NO_FILE_LOADED,
    TEXT_ABORT_MILLING_CONFIRM,
    TEXT_ABORT_MILLING_TITLE,
    TEXT_PROBE_REMOVED_CONFIRM,
    TEXT_START_MILLING_TITLE,
    TEXT_TOOL_CHANGE_FAILED
} from './constants.js';

// Pre-mill modal state
let premillResolve = null;  // Promise resolve for modal result

export async function startMill() {
    try {
        // === CAN THE JOB START ===
        const canStartResponse = await fetch(API_MILL_CAN_START);
        const canStart = await canStartResponse.json();

        // Check for blocking errors
        if (!canStart.canStart) {
            const errorMsg = canStart.errors.join('\n');
            showError(errorMsg);
            return;
        }

        // Get file info for display
        const fileResponse = await fetch(API_FILE_INFO);
        const fileInfo = await fileResponse.json();
        if (!fileInfo.name) {
            showError(TEXT_NO_FILE_LOADED);
            return;
        }

        // Reset grid state
        resetGridState();

        // === SHOW PRE-MILL MODAL ===
        const confirmed = await showPremillModal(fileInfo, canStart.warnings || []);
        if (!confirmed) {
            return;
        }

        // Show milling screen
        $('mill-filename').textContent = fileInfo.name;
        $('mill-phase').textContent = '';  // Clear stale phase from previous operation
        showScreen(SCREEN_MILL);

        // Start milling (server handles safety retract, G90/G17, etc.)
        const started = await postJson(API_MILL_START);
        if (!started.ok) {
            // The run never started, so go back rather than sit on a milling screen that
            // would later report a job complete.
            showError(started.error || ERROR_START_NOT_SENT);
            showScreen(SCREEN_DASHBOARD, true);
        }
    } catch (err) {
        console.error('startMilling failed', err);
        showError(ERROR_START_NOT_SENT);
    }
}

/**
 * Show the pre-milling modal with depth adjustment.
 * Returns a promise that resolves to true if user confirms, false if cancelled.
 */
async function showPremillModal(fileInfo, warnings) {
    const modal = $('premill-modal');
    const fileEl = $('premill-file');
    const linesEl = $('premill-lines');
    const warningsEl = $('premill-warnings');

    // Populate modal
    fileEl.textContent = fileInfo.name;
    linesEl.textContent = format(TEXT_LINE_COUNT, fileInfo.lines);

    // Show warnings if any
    if (warnings.length > 0) {
        warningsEl.innerHTML = warnings.map(w => `<p>⚠️ ${escapeMarkup(w)}</p>`).join('');
        warningsEl.classList.remove(CLASS_HIDDEN);
    } else {
        warningsEl.classList.add(CLASS_HIDDEN);
    }

    // Reset depth to 0 via API (single source of truth)
    await adjustPremillDepth(DEPTH_ACTION_RESET);

    // Show modal
    modal.classList.remove(CLASS_HIDDEN);

    // Return promise that resolves when user confirms or cancels
    return new Promise((resolve) => {
        // One modal, one pair of buttons, so a second call overwrites the first's resolve.
        // Answered false first, or the first promise never settles.
        if (premillResolve) {
            const stranded = premillResolve;
            premillResolve = null;
            stranded(false);
        }

        premillResolve = resolve;
    });
}

function hidePremillModal() {
    $('premill-modal').classList.add(CLASS_HIDDEN);
}

async function adjustPremillDepth(action) {
    const result = await adjustDepth(action);
    if (result !== null) {
        updateDepthDisplay(result);
    }
}

// Format depth value for display: 0, +0.25, -0.10, etc.
function formatDepthText(depth) {
    if (depth === 0) {
        return '0';
    }
    const sign = depth > 0 ? '+' : '';
    return `${sign}${depth.toFixed(2)}`;
}

// Server may refuse a resume (e.g. a tool change is active) or fail to confirm a stop in
// time - either way the button must reflect what the server actually did, not what the
// operator asked for, so this awaits the response instead of assuming success.
async function togglePause() {
    const btn = $('mill-pause-btn');
    const wasPaused = btn.dataset.paused === 'true';
    try {
        const result = await postJson(wasPaused ? API_MILL_RESUME : API_MILL_PAUSE);
        if (!result.ok) {
            showError(result.error || (wasPaused ? ERROR_RESUME_NOT_SENT : ERROR_PAUSE_NOT_SENT));
            return;
        }
        updatePauseButton(btn, !wasPaused);
    } catch (err) {
        console.error('togglePause failed', err);
        showError(ERROR_PAUSE_NOT_SENT);
    }
}

async function stopMill() {
    try {
        const result = await postJson(API_MILL_STOP);
        if (!result.ok) {
            // The server could not confirm the machine stopped - leave the mill screen
            // up and the milling state alone rather than telling the operator it did.
            showError(result.error || ERROR_STOP_NOT_SENT);
            return;
        }
    } catch (err) {
        console.error('stopMill failed', err);
        showError(ERROR_STOP_NOT_SENT);
        return;
    }

    // The server confirmed the run is torn down, so the next status will unlock the screen
    // and re-enable the back button. Leave now rather than waiting for it.
    endMillRun();
    showScreen(SCREEN_DASHBOARD, true);
}

// --- Tool Change Handling ---

// The prompt this client is showing, or null. Its id tells an already-answered prompt from
// a new one in a status snapshot, and is sent with the answer so a tap meant for this prompt
// cannot answer the next one (see PendingPrompt.cs). Null is "nothing outstanding": a second
// flag beside it was the same fact twice, and the two could disagree.
let shownPrompt = null;

// The one place that hides the overlay and forgets the prompt. Two paths end a prompt: it is
// answered (handleToolChangeComplete), or the run ends (endMillRun, called from the stop
// request and from the status that reports the run over). A prompt parked in
// WaitingForUserInput can be cancelled without being answered.
function hideToolChangeOverlay() {
    shownPrompt = null;
    const overlay = $('toolchange-overlay');
    if (overlay) {
        overlay.classList.add(CLASS_HIDDEN);
    }
}

/**
 * Draw whatever prompt the run is waiting on, from the status. Called from screens.js on
 * every status message, and it is the only route for a client that reloaded or reconnected
 * mid-prompt: the toolchange:input broadcast that announced it live is one-shot.
 *
 * The status is the authority on whether a prompt is still outstanding. The run's own phase
 * is not: between raising prompts the tool-change controller sits in WaitingForToolChange
 * with nothing to answer, so a display built from that phase would show a stale question
 * with live buttons under it.
 */
export function updateToolChangeDisplay(toolChange) {
    const overlay = $('toolchange-overlay');
    if (!overlay) return;

    const prompt = toolChange?.id ? toolChange : null;

    // The prompt on screen is the one the run is still waiting on, so leave it alone.
    if (shownPrompt && prompt && prompt.id === shownPrompt.id) {
        return;
    }

    shownPrompt = prompt;

    // Nothing outstanding. A run that ended without its prompt being answered - stopped, or
    // aborted from another browser - broadcasts nothing, so without this the overlay would
    // stand for ever with a button that answers a run that is gone.
    if (!prompt) {
        overlay.classList.add(CLASS_HIDDEN);
        return;
    }

    renderUserInputPrompt(prompt);
}

/**
 * Check if tool change is waiting for Z zero (for jog screen).
 * Returns true if jog screen should show "Continue Milling" button.
 */
export function isWaitingForZeroZ(toolChange) {
    return toolChange?.phase === PHASE_WAITING_FOR_ZERO_Z;
}

async function abortToolChange() {
    console.log('abortToolChange: showing confirm dialog');
    if (!await showConfirm(TEXT_ABORT_MILLING_CONFIRM, TEXT_ABORT_MILLING_TITLE)) {
        console.log('abortToolChange: user cancelled');
        return;
    }
    console.log('abortToolChange: user confirmed, sending abort request');
    try {
        hideToolChangeOverlay();  // Aborting - no answer will be taken from a pending prompt.

        const result = await postJson(API_MILL_TOOLCHANGE_ABORT);
        if (!result.ok) {
            // The server could not confirm both controllers stopped - report it and
            // leave navigation alone rather than telling the operator it is safe to walk
            // away from the machine.
            showError(result.error || ERROR_ABORT_NOT_SENT);
            return;
        }
        console.log('abortToolChange: abort request complete, navigating to dashboard');
        endMillRun();
        showScreen(SCREEN_DASHBOARD, true);
    } catch (err) {
        console.error('abortToolChange failed', err);
        showError(ERROR_ABORT_NOT_SENT);
    }
}

// --- Depth Adjustment ---

/**
 * Update the depth adjustment display. Written from the status stream and from the reply
 * to a +/- press, so an optimistic update corrects itself.
 */
export function updateDepthDisplay(depth) {
    const depthEl = $('premill-depth-value');
    if (depthEl && depth !== undefined) {
        depthEl.textContent = formatDepthText(depth);
    }
}

async function adjustDepth(action) {
    const result = await postJson(API_MILL_DEPTH, { action });
    if (!result.ok) {
        showError(result.error || ERROR_DEPTH_NOT_SET);
        return null;
    }

    updateDepthDisplay(result.data.depth);
    return result.data.depth;
}

// Grid visualization state
let gridState = {
    visitedCells: new Set(),  // Set of "x,y" strings for visited grid cells (from server)
    minX: 0, maxX: 0,
    minY: 0, maxY: 0,
    currentX: 0, currentY: 0,
    initialized: false,
    // How many cutting-path points the cells above were drawn from, so a status with no
    // new points skips the fetch.
    fetchedCount: 0
};

/**
 * Reset grid state for a new milling operation.
 */
export function resetGridState() {
    gridState.visitedCells.clear();
    gridState.fetchedCount = 0;
    gridState.initialized = false;
}

/**
 * Fetch visited grid cells from server with current grid dimensions.
 */
async function fetchGridCells() {
    try {
        const url = `${API_MILL_GRID}?width=${state.millGrid.maxWidth}&height=${state.millGrid.maxHeight}`;
        const response = await fetch(url);
        const data = await response.json();
        if (data.cells) {
            gridState.visitedCells = new Set(data.cells);
            gridState.fetchedCount = data.count || 0;
        }
    } catch (err) {
        console.error('Failed to fetch grid cells:', err);
    }
}

/**
 * Map a coordinate to grid index (for current position marker).
 */
function mapToGrid(value, min, range, gridSize) {
    if (range < MILL_MIN_RANGE_THRESHOLD) {
        return 0;
    }
    const index = Math.floor((value - min) / range * (gridSize - 1));
    return Math.max(0, Math.min(gridSize - 1, index));
}

/**
 * Calculate grid dimensions based on work area aspect ratio.
 */
function calculateGridDimensions() {
    const rangeX = Math.max(gridState.maxX - gridState.minX, MILL_MIN_RANGE_THRESHOLD);
    const rangeY = Math.max(gridState.maxY - gridState.minY, MILL_MIN_RANGE_THRESHOLD);
    const aspectRatio = rangeX / rangeY;

    const maxWidth = state.millGrid.maxWidth;
    const maxHeight = state.millGrid.maxHeight;
    let gridWidth, gridHeight;
    if (aspectRatio > 1) {
        gridWidth = Math.min(maxWidth, Math.ceil(maxHeight * aspectRatio));
        gridHeight = maxHeight;
    } else {
        gridWidth = maxWidth;
        gridHeight = Math.min(maxHeight, Math.ceil(maxWidth / aspectRatio));
    }

    return { rangeX, rangeY, gridWidth, gridHeight };
}

/**
 * Update grid state from status and redraw.
 * Called from screens.js when status is received.
 */
export function updateMillGrid(status) {
    const canvas = $('mill-grid');
    if (!canvas) return;

    // Get file bounds from status
    const file = status.file;
    if (!file || file.minX === undefined) return;

    // Update bounds
    gridState.minX = file.minX;
    gridState.maxX = file.maxX;
    gridState.minY = file.minY;
    gridState.maxY = file.maxY;
    gridState.initialized = true;

    // Update current position
    if (status.workPos) {
        gridState.currentX = status.workPos.x;
        gridState.currentY = status.workPos.y;
    }

    // Fetch updated grid cells if cutting path has new points
    const serverCount = status.cuttingPathCount || 0;
    if (serverCount > gridState.fetchedCount) {
        fetchGridCells();  // Fire and forget - will update on next status
    } else if (serverCount < gridState.fetchedCount) {
        // Server reset (new milling operation) - clear local state
        gridState.visitedCells.clear();
        gridState.fetchedCount = 0;
    }

    drawMillGrid(canvas);
}

/**
 * Draw the mill grid visualization on canvas.
 */
function drawMillGrid(canvas) {
    const ctx = canvas.getContext('2d');
    const width = canvas.width;
    const height = canvas.height;

    // The stylesheet owns the theme, so the colours are read from it rather than copied.
    const style = getComputedStyle(document.documentElement);
    const bgColor = style.getPropertyValue('--bg-color').trim();
    const surfaceColor = style.getPropertyValue('--surface-color').trim();
    const successColor = style.getPropertyValue('--success-color').trim();
    const warningColor = style.getPropertyValue('--warning-color').trim();

    // Clear canvas
    ctx.fillStyle = bgColor;
    ctx.fillRect(0, 0, width, height);

    // Calculate grid dimensions
    const { rangeX, rangeY, gridWidth, gridHeight } = calculateGridDimensions();

    // Calculate cell size to fit canvas with padding
    const padding = MILL_GRID_PADDING_PX;
    const availableWidth = width - padding * 2;
    const availableHeight = height - padding * 2;
    const cellWidth = availableWidth / gridWidth;
    const cellHeight = availableHeight / gridHeight;
    const cellSize = Math.min(cellWidth, cellHeight);

    // Center the grid
    const gridPixelWidth = gridWidth * cellSize;
    const gridPixelHeight = gridHeight * cellSize;
    const offsetX = (width - gridPixelWidth) / 2;
    const offsetY = padding;

    // Draw grid cells
    for (let y = 0; y < gridHeight; y++) {
        for (let x = 0; x < gridWidth; x++) {
            const px = offsetX + x * cellSize;
            // Flip Y so 0 is at bottom (matches TUI)
            const py = offsetY + (gridHeight - 1 - y) * cellSize;

            const cellKey = `${x},${y}`;
            if (gridState.visitedCells.has(cellKey)) {
                ctx.fillStyle = successColor;
            } else {
                ctx.fillStyle = surfaceColor;
            }
            ctx.fillRect(
                px + MILL_GRID_CELL_GAP_PX,
                py + MILL_GRID_CELL_GAP_PX,
                cellSize - MILL_GRID_CELL_GAP_PX * 2,
                cellSize - MILL_GRID_CELL_GAP_PX * 2);
        }
    }

    // Draw current position marker
    const currentGridX = mapToGrid(gridState.currentX, gridState.minX, rangeX, gridWidth);
    const currentGridY = mapToGrid(gridState.currentY, gridState.minY, rangeY, gridHeight);
    const markerX = offsetX + currentGridX * cellSize + cellSize / 2;
    const markerY = offsetY + (gridHeight - 1 - currentGridY) * cellSize + cellSize / 2;

    ctx.fillStyle = warningColor;
    ctx.beginPath();
    ctx.arc(markerX, markerY, cellSize / MILL_MARKER_CELL_FRACTION, 0, Math.PI * 2);
    ctx.fill();
}

async function sendFeedOverride(url) {
    const result = await postJson(url);
    if (!result.ok) {
        showError(result.error || ERROR_FEED_NOT_SENT);
    }
}

export function initMillScreen() {
    $('mill-pause-btn').addEventListener('click', togglePause);
    $('mill-stop-btn').addEventListener('click', stopMill);
    // Through postJson: these are refused on a machine that has dropped off the link.
    $('feed-minus').addEventListener('click', () => sendFeedOverride(API_FEED_DECREASE));
    $('feed-plus').addEventListener('click', () => sendFeedOverride(API_FEED_INCREASE));
    $('feed-reset').addEventListener('click', () => sendFeedOverride(API_FEED_RESET));

    // Feed controls start disabled until milling is actually running
    updateFeedControls(false);

    // Tool change abort button (Continue is set dynamically by handleToolChangeInput)
    const abortBtn = $('toolchange-abort-btn');
    if (abortBtn) abortBtn.addEventListener('click', abortToolChange);

    // Pre-mill modal buttons (depth adjustment before starting)
    $('premill-depth-minus').addEventListener('click', () => adjustPremillDepth(DEPTH_ACTION_DECREASE));
    $('premill-depth-plus').addEventListener('click', () => adjustPremillDepth(DEPTH_ACTION_INCREASE));
    $('premill-depth-reset').addEventListener('click', () => adjustPremillDepth(DEPTH_ACTION_RESET));
    $('premill-start-btn').addEventListener('click', async () => {
        hidePremillModal();
        // Confirm probe hardware removal before starting
        const confirmed = await showConfirm(TEXT_PROBE_REMOVED_CONFIRM, TEXT_START_MILLING_TITLE, { danger: true });
        if (premillResolve) {
            premillResolve(confirmed);
            premillResolve = null;
        }
    });
    $('premill-cancel-btn').addEventListener('click', () => {
        hidePremillModal();
        if (premillResolve) {
            premillResolve(false);
            premillResolve = null;
        }
    });
}

// --- Controller Event Handling ---

/**
 * Handle controller events from WebSocket.
 * Called from websocket.js when mill:* messages are received.
 */
export function handleMillControllerEvent(type, data) {
    switch (type) {
        case MSG_TYPE_MILL_STATE:
            handleStateChange(data.state);
            break;
        case MSG_TYPE_MILL_PROGRESS:
            handleProgressUpdate(data);
            break;
        case MSG_TYPE_MILL_TOOLCHANGE:
            handleToolChangeEvent(data);
            break;
        case MSG_TYPE_MILL_ERROR:
            handleMillError(data);
            break;
    }
}

/**
 * Update feed override controls enabled state.
 * Feed controls should only be enabled when actually milling (running/paused).
 */
function updateFeedControls(enabled) {
    const feedMinus = $('feed-minus');
    const feedPlus = $('feed-plus');
    const feedReset = $('feed-reset');
    if (feedMinus) feedMinus.disabled = !enabled;
    if (feedPlus) feedPlus.disabled = !enabled;
    if (feedReset) feedReset.disabled = !enabled;
}

/**
 * Set the mill screen's controls from the controller state. Called from the mill:state
 * broadcast and from each status, so a reloaded page shows the right button. Feed override
 * does nothing while homing, so it stays disabled until the run is cutting.
 */
export function applyMillControllerState(controllerState) {
    const adjustable = controllerState === CONTROLLER_STATE_RUNNING ||
                       controllerState === CONTROLLER_STATE_PAUSED ||
                       controllerState === CONTROLLER_STATE_WAITING_FOR_USER_INPUT;
    updateFeedControls(adjustable);
    updatePauseButton($('mill-pause-btn'), controllerState === CONTROLLER_STATE_PAUSED);
}

/**
 * Clear everything this screen held for a run that is over. Safe to call twice: whichever
 * of the stop request and the next status arrives first does the work.
 */
export function endMillRun() {
    hideToolChangeOverlay();
    resetGridState();

    // A new run inherits nothing from the last one's prompts, or its first prompt
    // would be answerable the moment it is drawn.
    settledPromptId = null;
}

function handleStateChange(controllerState) {
    console.log('Mill controller state:', controllerState);
    applyMillControllerState(controllerState);
}

// Whether the door overlay is showing the enclosure sentence. Read from the overlay itself:
// held as a flag beside it, the two could disagree about what is on screen.
function doorSentenceOnScreen() {
    const overlay = $('door-overlay');
    return overlay != null && !overlay.classList.contains(CLASS_HIDDEN);
}

function handleProgressUpdate(progress) {
    const phaseEl = $('mill-phase');

    // Update phase display with message (but not during Milling phase - line count shown below progress bar)
    if (phaseEl) {
        // During Milling phase, don't show the message (it duplicates progress-lines)
        const showMessage = progress.phase !== PHASE_MILLING && !doorSentenceOnScreen();
        phaseEl.textContent = showMessage ? (progress.message || progress.phase || '') : '';
    }

}

function handleToolChangeEvent(data) {
    // Informational: the server starts the tool change controller, which broadcasts its
    // own phases, and the overlay follows those (updateToolChangeDisplay).
    console.log('Tool change event:', data);
}

function handleMillError(data) {
    console.error('Mill error:', data.message);
    showError(data.message);
}

// --- Tool Change Controller Event Handling ---

/**
 * Handle tool change controller events from WebSocket.
 * Called from websocket.js when toolchange:* messages are received.
 */
export function handleToolChangeControllerEvent(type, data) {
    switch (type) {
        case MSG_TYPE_TOOLCHANGE_STATE:
            handleToolChangeState(data.state);
            break;
        case MSG_TYPE_TOOLCHANGE_PROGRESS:
            handleToolChangeProgress(data);
            break;
        case MSG_TYPE_TOOLCHANGE_INPUT:
            handleToolChangeInput(data);
            break;
        case MSG_TYPE_TOOLCHANGE_COMPLETE:
            handleToolChangeComplete(data);
            break;
        case MSG_TYPE_TOOLCHANGE_ERROR:
            handleToolChangeError(data);
            break;
    }
}

function handleToolChangeState(state) {
    // handleToolChangeProgress writes the phase the operator reads into mill-phase. This
    // carries the controller state, which no screen shows.
    console.log('Tool change controller state:', state);
}

function handleToolChangeProgress(progress) {
    console.log('Tool change progress:', progress);
    // Update progress display in both mill-phase (always visible) and toolchange-message (in overlay)
    const message = progress.message || '';
    const phaseEl = $('mill-phase');
    if (phaseEl) {
        phaseEl.textContent = message;
    }
    const messageEl = $('toolchange-message');
    if (messageEl) {
        messageEl.textContent = message;
    }
}

// The overlay's heading, hidden when the prompt has no title.
function setPromptHeading(title) {
    const heading = $('toolchange-info');
    if (!heading) return;

    heading.textContent = title || '';
    heading.classList.toggle(CLASS_HIDDEN, !title);
}

/**
 * Everything about the enclosure, over whichever screen is open: an open door stops jogging
 * and probing as well as milling.
 *
 * A run's own enclosure prompt is drawn here too, not in the tool-change overlay. That
 * overlay lives inside the mill screen, which is not on the page while the operator is on
 * the probe or jog screen, so a prompt rendered there would leave them with a held machine,
 * nothing on screen and no way to answer.
 */
export function updateDoorOverlay(status) {
    const overlay = $('door-overlay');
    if (!overlay) return;

    // A run holding at the door published a prompt; its Continue answers that prompt.
    // Without a run, the door text comes from the status and Continue releases the hold.
    const prompt = doorPrompt();

    if (shownPrompt && !prompt) {
        // The run is asking about something else, and it has said what to show.
        overlay.classList.add(CLASS_HIDDEN);
        return;
    }

    // A run parked on its prompt cannot notice the enclosure reopening, so the status, not
    // the prompt, decides whether a cycle start still applies and what the operator is told.
    const canRelease = status?.canReleaseDoor === true;
    const text = canRelease && prompt ? prompt.message : status?.doorMessage || (prompt && prompt.message);

    if (!text) {
        overlay.classList.add(CLASS_HIDDEN);
        return;
    }

    $('door-message').textContent = text;

    // Which buttons the overlay has is decided in Core. A run says so by the options it
    // offers, still gated on the door state because a run parked on its prompt cannot
    // notice the enclosure reopening. Without a run, the server says whether a release
    // straight from here would be taken - it refuses one while a run is handling the door
    // itself, and offering it anyway put the question back on screen after it was answered.
    const options = prompt ? (prompt.options || []) : [];
    const canContinue = prompt
        ? canRelease && options.includes(PROMPT_OPTION_CONTINUE)
        : status?.buttons?.doorRelease?.enabled === true;

    setDoorButton('door-continue-btn',
        canContinue,
        prompt ? () => answerPrompt(prompt) : releaseDoorHold);

    // Abort stays offered while the enclosure is open, because the overlay is the only
    // thing on screen and an operator who wants to abandon the job needs a way to say so.
    const canAbort = options.includes(PROMPT_OPTION_ABORT);
    setDoorButton('door-abort-btn', canAbort, () => answerPrompt(prompt, PROMPT_OPTION_ABORT));

    // With no button there is nothing to answer, so the overlay becomes a banner rather
    // than a layer over the page: the operator still has to reach Stop underneath.
    const hasButton = canAbort || canContinue;
    overlay.classList.toggle(CLASS_DOOR_MESSAGE, !hasButton);

    // A release with no run behind it has no earlier prompt a tap could belong to, so it
    // is answerable at once. Either way this is the only place the buttons are enabled.
    if (hasButton) {
        acceptAnswersAfterSettling(
            prompt ? prompt.id : DOOR_RELEASE_PROMPT_ID,
            prompt ? PROMPT_SETTLE_MS : 0);
    }

    overlay.classList.remove(CLASS_HIDDEN);
}

// Show or hide one of the door overlay's buttons, and say what it answers. Whether it is
// enabled belongs to acceptAnswersAfterSettling: writing it here as well re-enabled the
// button on the next status tick, sooner than the settle allows.
function setDoorButton(id, shown, onclick) {
    const btn = $(id);
    if (!btn) return;

    btn.style.display = shown ? '' : 'none';
    btn.onclick = onclick;
}

// The enclosure prompt a run is waiting on, or null. Read from the same shownPrompt the
// tool-change display uses, so the two overlays cannot disagree about which prompt is up.
function doorPrompt() {
    return shownPrompt?.isDoorPrompt ? shownPrompt : null;
}

async function releaseDoorHold() {
    // Disabled while the POST is in flight, as answerPrompt does: a second tap would send a
    // second cycle start.
    const buttons = answerButtons();
    buttons.forEach(btn => { btn.disabled = true; });

    try {
        const result = await postJson(API_DOOR_RELEASE);
        if (!result.ok) {
            showError(result.error || ERROR_DOOR_NOT_RELEASED);
        }
    } finally {
        buttons.forEach(btn => { btn.disabled = false; });
    }
}

// Renders a { title, message, options } prompt into the tool-change overlay and shows it.
// Shared by the live path (handleToolChangeInput) and the status-recovery path
// (updateToolChangeDisplay), so the two can never render the prompt differently.
function renderUserInputPrompt(data) {
    // The enclosure prompt is drawn by updateDoorOverlay, in the page-level overlay that
    // is on screen whichever screen the operator is on.
    if (data.isDoorPrompt) {
        return;
    }

    const overlay = $('toolchange-overlay');
    if (!overlay) return;

    setPromptHeading(data.title);
    $('toolchange-message').textContent = data.message || '';

    // Update buttons based on options
    const continueBtn = $('toolchange-continue-btn');
    const abortBtn = $('toolchange-abort-btn');

    if (continueBtn && abortBtn && data.options) {
        // Compared against the options the server publishes, not matched by substring: an
        // answer has to be one of them exactly, so a near miss draws a button that cannot
        // answer anything.
        continueBtn.style.display = data.options.includes(PROMPT_OPTION_CONTINUE) ? '' : 'none';
        abortBtn.style.display = data.options.includes(PROMPT_OPTION_ABORT) ? '' : 'none';

        // Update click handler for continue button
        continueBtn.onclick = () => answerPrompt(data);
        // Note: abort handler is set via addEventListener in setupMillEventListeners()
        // which shows a confirmation dialog before aborting
    }

    // Now show the overlay - user input is needed
    overlay.classList.remove(CLASS_HIDDEN);
    acceptAnswersAfterSettling(data.id);
}

// The buttons that can answer a prompt. Both are disabled while an answer is in flight and
// while a newly drawn prompt is still settling, so a second tap cannot answer a prompt the
// operator has not read.
function answerButtons() {
    return [
        $('toolchange-continue-btn'),
        $('jog-continue-milling-btn'),
        $('door-continue-btn'),
        $('door-abort-btn')
    ].filter(Boolean);
}

let promptSettleTimer = null;
let settledPromptId = null;

/**
 * Enable the answer buttons once the prompt has been on screen long enough that a tap
 * cannot belong to the one that answered the previous prompt. Each prompt settles once,
 * because the status-recovery path redraws the same one on every poll.
 */
function acceptAnswersAfterSettling(promptId, settleMs = PROMPT_SETTLE_MS) {
    if (promptId != null && promptId === settledPromptId) {
        return;
    }
    settledPromptId = promptId ?? null;

    if (promptSettleTimer) {
        clearTimeout(promptSettleTimer);
        promptSettleTimer = null;
    }

    if (settleMs <= 0) {
        answerButtons().forEach(btn => { btn.disabled = false; });
        return;
    }

    answerButtons().forEach(btn => { btn.disabled = true; });
    promptSettleTimer = setTimeout(() => {
        promptSettleTimer = null;
        answerButtons().forEach(btn => { btn.disabled = false; });
    }, settleMs);
}

/**
 * Answer `prompt` with `wanted`, if it offered that option. Takes the prompt rather than
 * reading whichever is current, so a button drawn for one prompt can only answer that one.
 */
async function answerPrompt(prompt, wanted = PROMPT_OPTION_CONTINUE) {
    const option = (prompt?.options || []).find(opt => opt === wanted);
    if (!option) {
        showError(ERROR_NOTHING_TO_ANSWER);
        return false;
    }

    const buttons = answerButtons();
    buttons.forEach(btn => { btn.disabled = true; });

    try {
        const result = await postJson(
            API_MILL_TOOLCHANGE_INPUT, { id: prompt.id, response: option });
        if (!result.ok) {
            showError(result.error || ERROR_INPUT_NOT_SENT);
            buttons.forEach(btn => { btn.disabled = false; });
            return false;
        }
        return true;
    } catch (err) {
        console.error('answerPrompt failed', err);
        showError(ERROR_INPUT_NOT_SENT);
        // The prompt is still waiting, so re-enable the button that answers it.
        buttons.forEach(btn => { btn.disabled = false; });
        return false;
    }
}

/**
 * Answer the prompt the run is on now. For the jog screen's Continue Milling button, shown
 * only while the run is waiting for Z0 to be set.
 */
export function continueLastPrompt() {
    return answerPrompt(shownPrompt);
}

function handleToolChangeInput(data) {
    console.log('Tool change input required:', data);

    shownPrompt = data;

    // Show user input dialog - this is when user action is actually needed
    // data contains: title, message, options[], id
    renderUserInputPrompt(data);
}

function handleToolChangeComplete(data) {
    console.log('Tool change complete:', data);
    // The prompt (if any) has been answered - let status polling drive the
    // overlay again.
    hideToolChangeOverlay();

    if (!data.success && !data.aborted) {
        // Only show error for actual failures, not user aborts
        showError(TEXT_TOOL_CHANGE_FAILED);
    }
}

function handleToolChangeError(data) {
    console.error('Tool change error:', data.message);
    showError(data.message);
}
