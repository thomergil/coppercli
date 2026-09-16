// coppercli Web UI Mill Screen

import { state } from './state.js';
import { $, showError, showConfirm, updatePauseButton } from './helpers.js';
import { showScreen } from './screens.js';
import {
    PROMPT_KIND_OPERATOR_PAUSE,
    PROMPT_OPTION_CONTINUE,
    ERROR_START_NOT_SENT,
    ERROR_PAUSE_NOT_SENT,
    ERROR_STOP_NOT_SENT,
    ERROR_ABORT_NOT_SENT,
    ERROR_INPUT_NOT_SENT,
    ERROR_NOTHING_TO_ANSWER,
    PROMPT_SETTLE_MS,
    API_FILE_INFO,
    API_MILL_PREFLIGHT,
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
    PHASE_WAITING_FOR_TOOL_CHANGE,
    PHASE_WAITING_FOR_ZERO_Z,
    MILL_MIN_RANGE_THRESHOLD,
    TEXT_TOOL_CHANGE,
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
        // === PREFLIGHT CHECKS ===
        const preflightResponse = await fetch(API_MILL_PREFLIGHT);
        const preflight = await preflightResponse.json();

        // Check for blocking errors
        if (!preflight.canStart) {
            const errorMsg = preflight.errors.join('\n');
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
        const confirmed = await showPremillModal(fileInfo, preflight.warnings || []);
        if (!confirmed) {
            return;
        }

        // Show milling screen
        $('mill-filename').textContent = fileInfo.name;
        $('mill-phase').textContent = '';  // Clear stale phase from previous operation
        $('progress-lines').textContent = `0 / ${fileInfo.lines}`;
        showScreen(SCREEN_MILL);

        // Start milling (server handles safety retract, G90/G17, etc.)
        const startResponse = await fetch(API_MILL_START, { method: 'POST' });
        const started = await startResponse.json();
        if (!started.success) {
            // The run never began. Go back rather than sit on a milling screen that will
            // then announce a job nobody is cutting as complete.
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
    const depthEl = $('premill-depth-value');

    // Populate modal
    fileEl.textContent = fileInfo.name;
    linesEl.textContent = `${fileInfo.lines} lines`;

    // Show warnings if any
    if (warnings.length > 0) {
        warningsEl.innerHTML = warnings.map(w => `<p>⚠️ ${w}</p>`).join('');
        warningsEl.classList.remove(CLASS_HIDDEN);
    } else {
        warningsEl.classList.add(CLASS_HIDDEN);
    }

    // Reset depth to 0 via API (single source of truth)
    await adjustPremillDepth('reset');
    depthEl.textContent = '0';

    // Show modal
    modal.classList.remove(CLASS_HIDDEN);

    // Return promise that resolves when user confirms or cancels
    return new Promise((resolve) => {
        premillResolve = resolve;
    });
}

function hidePremillModal() {
    $('premill-modal').classList.add(CLASS_HIDDEN);
}

async function adjustPremillDepth(action) {
    const result = await adjustDepth(action);
    if (result !== null) {
        updatePremillDepthDisplay(result);
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

function updatePremillDepthDisplay(depth) {
    const depthEl = $('premill-depth-value');
    if (depthEl && depth !== undefined) {
        depthEl.textContent = formatDepthText(depth);
    }
}

// Server may refuse a resume (e.g. a tool change is active) or fail to confirm a stop in
// time - either way the button must reflect what the server actually did, not what the
// operator asked for, so this awaits the response instead of assuming success.
async function togglePause() {
    const btn = $('mill-pause-btn');
    const wasPaused = btn.dataset.paused === 'true';
    try {
        const response = await fetch(wasPaused ? API_MILL_RESUME : API_MILL_PAUSE, { method: 'POST' });
        const json = await response.json();
        if (!json.success) {
            showError(json.error || `Failed to ${wasPaused ? 'resume' : 'pause'}`);
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
        const response = await fetch(API_MILL_STOP, { method: 'POST' });
        const json = await response.json();
        if (!json.success) {
            // The server could not confirm the machine stopped - leave the mill screen
            // up and the milling state alone rather than telling the operator it did.
            showError(json.error || 'Failed to stop');
            return;
        }
    } catch (err) {
        console.error('stopMill failed', err);
        showError(ERROR_STOP_NOT_SENT);
        return;
    }

    // The server confirmed the run is torn down, so the next status will say so, unlock the
    // screen and re-enable the back button. Leave now rather than waiting the poll out.
    endMillRun();
    showScreen(SCREEN_DASHBOARD, true);
}

// --- Tool Change Handling ---

// True from the moment a toolchange:input prompt is shown (tool change or a bare M0/M1)
// until it is answered or the run ends. It holds the overlay up between the broadcast that
// announced the prompt and the first status snapshot that knows about it.
let pendingUserInputPrompt = false;

// The prompt this client last showed. Its id tells an already-answered prompt from a new one
// in a status snapshot, and travels with the answer so a tap meant for this question cannot
// release the next one (see PendingPrompt.cs).
let lastPrompt = null;

// Single place that hides the overlay and clears pendingUserInputPrompt. Two paths end a
// pending prompt: it is answered (handleToolChangeComplete), or the run ends on its own
// (endMillRun, called from the stop request and from the status that says the run is over).
// A prompt parked in WaitingForUserInput can be cancelled without ever being answered.
function hideToolChangeOverlay() {
    pendingUserInputPrompt = false;
    const overlay = $('toolchange-overlay');
    if (overlay) {
        overlay.classList.add(CLASS_HIDDEN);
    }
}

/**
 * Update tool change display based on controller phase (FSM state).
 * Called from screens.js when status is received.
 *
 * Which phase puts what on screen is defined once, on ToolChangePhase in
 * coppercli.Core/Controllers/ToolChangePhase.cs. The prompt kind that marks a bare program
 * stop rather than a tool change is PROMPT_KIND_OPERATOR_PAUSE. A null means no tool
 * change is under way.
 */
export function updateToolChangeDisplay(toolChange) {
    const overlay = $('toolchange-overlay');
    if (!overlay) return;

    // A prompt is up and unanswered, so leave it alone whatever this poll saw.
    // hideToolChangeOverlay takes it down once it is resolved.
    if (pendingUserInputPrompt) {
        return;
    }

    // A bare M0/M1 has no tool-change phase of its own, so GetStatus folds it into this
    // field. It is the only path a client that reloaded mid-prompt has, and the id check
    // keeps it from redrawing one already shown.
    if (toolChange && toolChange.phase === PROMPT_KIND_OPERATOR_PAUSE) {
        if (toolChange.id !== lastPrompt?.id) {
            lastPrompt = toolChange;
            pendingUserInputPrompt = true;
            renderUserInputPrompt(toolChange);
        }
        return;
    }

    // The jog screen's Continue Milling button answers from here, and it is shown on a
    // phase this overlay does not draw (WaitingForZeroZ).
    lastPrompt = toolChange?.id ? toolChange : null;

    // No tool change or not waiting for tool change → hide overlay
    if (!toolChange || toolChange.phase !== PHASE_WAITING_FOR_TOOL_CHANGE) {
        overlay.classList.add(CLASS_HIDDEN);
        return;
    }

    // Log bug condition: phase is WaitingForToolChange but toolNumber is null
    if (toolChange.toolNumber == null) {
        console.error('BUG: toolChange has phase WaitingForToolChange but toolNumber is null:', toolChange);
    }

    // WaitingForToolChange phase - show overlay with both buttons
    const infoEl = $('toolchange-info');
    const messageEl = $('toolchange-message');
    const continueBtn = $('toolchange-continue-btn');
    const abortBtn = $('toolchange-abort-btn');

    const toolDesc = toolChange.toolName
        ? `Change to T${toolChange.toolNumber} (${toolChange.toolName})`
        : `Change to T${toolChange.toolNumber}`;

    if (infoEl) {
        infoEl.textContent = TEXT_TOOL_CHANGE;
    }
    if (messageEl) {
        messageEl.textContent = toolDesc;
    }

    // Always show both buttons in WaitingForToolChange phase
    // (both Continue and Abort are always valid options)
    if (continueBtn) {
        continueBtn.style.display = '';
        continueBtn.onclick = () => answerPrompt(toolChange);
    }
    acceptAnswersAfterSettling(toolChange.id);
    if (abortBtn) {
        abortBtn.style.display = '';
        // Note: abort handler is set via addEventListener in setupMillEventListeners()
        // which shows a confirmation dialog before aborting
    }

    overlay.classList.remove(CLASS_HIDDEN);
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
        hideToolChangeOverlay();  // Aborting - any pending prompt is moot

        const response = await fetch(API_MILL_TOOLCHANGE_ABORT, { method: 'POST' });
        const json = await response.json();
        if (!json.success) {
            // The server could not confirm both controllers stopped - surface it and
            // leave navigation alone rather than telling the operator it's safe to walk
            // away from the machine.
            showError(json.error || 'Failed to abort');
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
 * Update depth adjustment display.
 * Called from screens.js when status is received.
 */
export function updateDepthDisplay(depth) {
    const depthEl = $('depth-value');
    if (depthEl && depth !== undefined) {
        depthEl.textContent = formatDepthText(depth);
    }
}

async function adjustDepth(action) {
    try {
        const response = await fetch(API_MILL_DEPTH, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ action })
        });
        const result = await response.json();
        if (result.success) {
            updateDepthDisplay(result.depth);
            return result.depth;
        }
    } catch (err) {
        console.error('Failed to adjust depth:', err);
    }
    return null;
}

// Grid visualization state
let gridState = {
    visitedCells: new Set(),  // Set of "x,y" strings for visited grid cells (from server)
    minX: 0, maxX: 0,
    minY: 0, maxY: 0,
    currentX: 0, currentY: 0,
    initialized: false,
    // How many cutting-path points the cells above were drawn from, so a status carrying
    // no new points costs no fetch.
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

    // Get CSS variables for colors
    const style = getComputedStyle(document.documentElement);
    const bgColor = style.getPropertyValue('--bg-color').trim() || '#1a1a2e';
    const surfaceColor = style.getPropertyValue('--surface-color').trim() || '#16213e';
    const primaryColor = style.getPropertyValue('--primary-color').trim() || '#0f3460';
    const successColor = style.getPropertyValue('--success-color').trim() || '#00bf63';
    const warningColor = style.getPropertyValue('--warning-color').trim() || '#ffc107';
    const textDim = style.getPropertyValue('--text-dim').trim() || '#888';

    // Clear canvas
    ctx.fillStyle = bgColor;
    ctx.fillRect(0, 0, width, height);

    // Calculate grid dimensions
    const { rangeX, rangeY, gridWidth, gridHeight } = calculateGridDimensions();

    // Calculate cell size to fit canvas with padding
    const padding = 10;
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
            ctx.fillRect(px + 1, py + 1, cellSize - 2, cellSize - 2);
        }
    }

    // Draw current position marker
    const currentGridX = mapToGrid(gridState.currentX, gridState.minX, rangeX, gridWidth);
    const currentGridY = mapToGrid(gridState.currentY, gridState.minY, rangeY, gridHeight);
    const markerX = offsetX + currentGridX * cellSize + cellSize / 2;
    const markerY = offsetY + (gridHeight - 1 - currentGridY) * cellSize + cellSize / 2;

    ctx.fillStyle = warningColor;
    ctx.beginPath();
    ctx.arc(markerX, markerY, cellSize / 3, 0, Math.PI * 2);
    ctx.fill();
}

export function initMillScreen() {
    $('mill-pause-btn').addEventListener('click', togglePause);
    $('mill-stop-btn').addEventListener('click', stopMill);
    $('feed-minus').addEventListener('click', () => fetch(API_FEED_DECREASE, { method: 'POST' }));
    $('feed-plus').addEventListener('click', () => fetch(API_FEED_INCREASE, { method: 'POST' }));
    $('feed-reset').addEventListener('click', () => fetch(API_FEED_RESET, { method: 'POST' }));

    // Feed controls start disabled until milling is actually running
    updateFeedControls(false);

    // Tool change abort button (Continue is set dynamically by handleToolChangeInput)
    const abortBtn = $('toolchange-abort-btn');
    if (abortBtn) abortBtn.addEventListener('click', abortToolChange);

    // Pre-mill modal buttons (depth adjustment before starting)
    $('premill-depth-minus').addEventListener('click', () => adjustPremillDepth('decrease'));
    $('premill-depth-plus').addEventListener('click', () => adjustPremillDepth('increase'));
    $('premill-depth-reset').addEventListener('click', () => adjustPremillDepth('reset'));
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
 * Put the mill screen's controls where the controller's state says they belong. Called from
 * the mill:state broadcast and from each status, so a reloaded page shows the right button.
 * Feed means nothing while the machine is still homing, so it is off until it cuts.
 */
export function applyMillControllerState(controllerState) {
    const adjustable = controllerState === CONTROLLER_STATE_RUNNING ||
                       controllerState === CONTROLLER_STATE_PAUSED ||
                       controllerState === CONTROLLER_STATE_WAITING_FOR_USER_INPUT;
    updateFeedControls(adjustable);
    updatePauseButton($('mill-pause-btn'), controllerState === CONTROLLER_STATE_PAUSED);
}

/**
 * Drop everything this screen was holding for a run that is over. Safe to call twice:
 * whichever of the stop request and the next status gets here first does the work.
 */
export function endMillRun() {
    hideToolChangeOverlay();
    resetGridState();
}

function handleStateChange(controllerState) {
    console.log('Mill controller state:', controllerState);
    applyMillControllerState(controllerState);
}

function handleProgressUpdate(progress) {
    const phaseEl = $('mill-phase');
    const progressLinesEl = $('progress-lines');

    // Update phase display with message (but not during Milling phase - line count shown below progress bar)
    if (phaseEl) {
        // During Milling phase, don't show the message (it duplicates progress-lines)
        const showMessage = progress.phase !== PHASE_MILLING;
        phaseEl.textContent = showMessage ? (progress.message || progress.phase || '') : '';
    }

    // Update progress lines (use != null to check for both null and undefined)
    if (progressLinesEl && progress.currentStep != null && progress.totalSteps != null) {
        progressLinesEl.textContent = `${progress.currentStep} / ${progress.totalSteps}`;
    }
}

function handleToolChangeEvent(data) {
    // Informational: the server auto-starts the tool change controller, which broadcasts
    // its own phases. The overlay follows those (updateToolChangeDisplay).
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
    console.log('Tool change controller state:', state);
    // Update UI to show current phase
    const phaseEl = $('toolchange-phase');
    if (phaseEl) {
        phaseEl.textContent = state;
    }
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

// Renders a { title, message, options } prompt into the overlay and shows it. Shared
// by the live path (handleToolChangeInput) and the status-recovery path
// (updateToolChangeDisplay), so the two can never render the prompt differently.
function renderUserInputPrompt(data) {
    const overlay = $('toolchange-overlay');
    if (!overlay) return;

    // Update overlay content - title and message from controller
    $('toolchange-info').textContent = data.title || 'Tool Change';
    $('toolchange-message').textContent = data.message || '';

    // Update buttons based on options
    const continueBtn = $('toolchange-continue-btn');
    const abortBtn = $('toolchange-abort-btn');

    if (continueBtn && abortBtn && data.options) {
        // Find matching options
        const hasContinue = data.options.some(opt => opt.toLowerCase().includes('continue'));
        const hasAbort = data.options.some(opt => opt.toLowerCase().includes('abort'));

        continueBtn.style.display = hasContinue ? '' : 'none';
        abortBtn.style.display = hasAbort ? '' : 'none';

        // Update click handler for continue button
        continueBtn.onclick = () => answerPrompt(data);
        // Note: abort handler is set via addEventListener in setupMillEventListeners()
        // which shows a confirmation dialog before aborting
    }

    // Now show the overlay - user input is needed
    overlay.classList.remove(CLASS_HIDDEN);
    acceptAnswersAfterSettling(data.id);
}

// The buttons that can answer a prompt. Both go dead while an answer is in flight and while
// a freshly drawn question is still settling, so a second tap cannot reach a question the
// operator has not read.
function continueButtons() {
    return [$('toolchange-continue-btn'), $('jog-continue-milling-btn')].filter(Boolean);
}

let promptSettleTimer = null;
let settledPromptId = null;

/**
 * Allow the question now on screen to be answered, once it has been there long enough that
 * a tap cannot belong to the gesture that answered the previous one. Each question settles
 * once: the status-recovery path redraws the same one on every poll.
 */
function acceptAnswersAfterSettling(promptId) {
    if (promptId != null && promptId === settledPromptId) {
        return;
    }
    settledPromptId = promptId ?? null;

    continueButtons().forEach(btn => { btn.disabled = true; });
    if (promptSettleTimer) {
        clearTimeout(promptSettleTimer);
    }
    promptSettleTimer = setTimeout(() => {
        promptSettleTimer = null;
        continueButtons().forEach(btn => { btn.disabled = false; });
    }, PROMPT_SETTLE_MS);
}

/**
 * Answers `prompt` with its Continue option. Takes the prompt rather than reading whichever
 * is current, so a button drawn for one question can only answer that one.
 */
async function answerPrompt(prompt) {
    const option = (prompt?.options || []).find(opt => opt === PROMPT_OPTION_CONTINUE);
    if (!option) {
        showError(ERROR_NOTHING_TO_ANSWER);
        return false;
    }

    const buttons = continueButtons();
    buttons.forEach(btn => { btn.disabled = true; });

    try {
        const result = await fetch(API_MILL_TOOLCHANGE_INPUT, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ id: prompt.id, response: option })
        });
        const json = await result.json();
        if (!json.success) {
            showError(json.error || ERROR_INPUT_NOT_SENT);
            buttons.forEach(btn => { btn.disabled = false; });
            return false;
        }
        return true;
    } catch (err) {
        console.error('answerPrompt failed', err);
        showError(ERROR_INPUT_NOT_SENT);
        // The question is still waiting, so the only button that can answer it comes back.
        buttons.forEach(btn => { btn.disabled = false; });
        return false;
    }
}

/**
 * Answers the question the run is on now. For the jog screen's Continue Milling button,
 * which is shown only while the run is waiting for Z0 to be set.
 */
export function continueLastPrompt() {
    return answerPrompt(lastPrompt);
}

function handleToolChangeInput(data) {
    console.log('Tool change input required:', data);

    pendingUserInputPrompt = true;
    lastPrompt = data;

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
