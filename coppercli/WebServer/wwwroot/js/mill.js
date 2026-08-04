// coppercli Web UI Mill Screen

import { state } from './state.js';
import { $, showError, showInfo, showConfirm, updatePauseButton } from './helpers.js';
import { showScreen } from './screens.js';
import {
    MILL_PHASE_WAITING_FOR_OPERATOR,
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
    TEXT_MILLING_COMPLETE,
    CONTROLLER_STATE_IDLE,
    CONTROLLER_STATE_INITIALIZING,
    CONTROLLER_STATE_RUNNING,
    CONTROLLER_STATE_PAUSED,
    CONTROLLER_STATE_COMPLETING,
    CONTROLLER_STATE_COMPLETED,
    CONTROLLER_STATE_FAILED,
    CONTROLLER_STATE_CANCELLED,
    MILL_MIN_RANGE_THRESHOLD
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
            showError('No file loaded');
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
        await fetch(API_MILL_START, { method: 'POST' });
        state.isMilling = true;
    } catch (err) {
        showError('Failed to start milling: ' + err.message);
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
        showError(`Failed to ${wasPaused ? 'resume' : 'pause'}: ${err.message}`);
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
        showError('Failed to stop: ' + err.message);
        return;
    }

    // Set isMilling false immediately to prevent "Milling complete!" toast
    // when status update arrives (since STOP is not completion)
    state.isMilling = false;
    state.lockedScreen = null;  // Clear screen lock so navigation works
    state.userStoppedMilling = true;  // Prevent re-locking on next status update
    hideToolChangeOverlay();  // Server confirmed the stop - any pending prompt is moot
    $('mill-back-btn').disabled = false;
    resetGridState();  // Clear local grid state
    showScreen(SCREEN_DASHBOARD);
}

// --- Tool Change Handling ---

// Tool change phase constants (must match ToolChangePhase enum)
const PHASE_WAITING_FOR_TOOL_CHANGE = 'WaitingForToolChange';
const PHASE_WAITING_FOR_ZERO_Z = 'WaitingForZeroZ';


// True from the moment a toolchange:input prompt is shown (tool change or a bare
// M0/M1) until it is answered or the run ends. A bare M0/M1 has no ToolChangeController
// workflow behind it, so status.toolChange is null for the whole time it is pending -
// without this flag, the next status poll would read that null as "nothing going on"
// and tear the overlay down before the operator can answer it.
let pendingUserInputPrompt = false;

// Id of the prompt this client last showed, from either path (live handleToolChangeInput
// or the status-recovery branch below). A status snapshot built on the server before the
// operator's answer, but delivered after the toolchange:complete that answer triggered,
// would otherwise read as "a prompt is pending" and resurrect an overlay already
// dismissed - comparing ids catches that even though both prompts look identical
// otherwise, and still lets a genuinely new prompt with a different id through.
let lastPromptId = null;

// Single place that tears the overlay/flag back down, shared by every path that can
// end a pending prompt: answered (handleToolChangeComplete), or the run ending on its
// own (handleStateChange, stopMill) - a prompt parked via WaitingForUserInput can be
// cancelled straight to Failed/Cancelled without ever being answered.
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
 * UI behavior is 1:1 with phase:
 *   - WaitingForToolChange → mill screen shows overlay with Continue/Abort
 *   - WaitingForOperator → mill screen shows overlay for a bare M0/M1 prompt
 *   - WaitingForZeroZ → jog screen shows "Continue Milling" button
 *   - Other phases → spindle moving, no user action needed
 *   - null → no tool change in progress
 */
export function updateToolChangeDisplay(toolChange) {
    const overlay = $('toolchange-overlay');
    if (!overlay) return;

    // A prompt is up and unanswered - leave it alone regardless of what this poll
    // saw. hideToolChangeOverlay() takes it down instead, once it is actually
    // resolved: answered (handleToolChangeComplete) or the run ending
    // (handleStateChange, stopMill).
    if (pendingUserInputPrompt) {
        return;
    }

    // A bare M0/M1 prompt has no ToolChangeController phase of its own to recover
    // from, so GetStatus() folds it into this same field - this is the only path a
    // client that reloaded or reconnected mid-prompt has, since the toolchange:input
    // broadcast that announced it live is one-shot and already missed. The id check
    // makes this idempotent once shown, the same way pendingUserInputPrompt does for
    // the live path above.
    if (toolChange && toolChange.phase === MILL_PHASE_WAITING_FOR_OPERATOR) {
        if (toolChange.id !== lastPromptId) {
            lastPromptId = toolChange.id;
            pendingUserInputPrompt = true;
            renderUserInputPrompt(toolChange);
        }
        return;
    }

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
        infoEl.textContent = 'Tool Change';
    }
    if (messageEl) {
        messageEl.textContent = toolDesc;
    }

    // Always show both buttons in WaitingForToolChange phase
    // (both Continue and Abort are always valid options)
    if (continueBtn) {
        continueBtn.style.display = '';
        continueBtn.onclick = () => sendToolChangeInput('Continue');
    }
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
    return toolChange && toolChange.phase === PHASE_WAITING_FOR_ZERO_Z;
}

async function abortToolChange() {
    console.log('abortToolChange: showing confirm dialog');
    if (!await showConfirm('Abort milling?', 'Abort')) {
        console.log('abortToolChange: user cancelled');
        return;
    }
    console.log('abortToolChange: user confirmed, sending abort request');
    try {
        // Set state first to prevent race conditions with incoming status updates
        state.isMilling = false;
        state.lockedScreen = null;
        state.userStoppedMilling = true;  // Prevent re-locking on next status update
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
        showScreen(SCREEN_DASHBOARD);
    } catch (err) {
        showError('Failed to abort: ' + err.message);
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
    initialized: false
};

/**
 * Reset grid state for a new milling operation.
 */
export function resetGridState() {
    gridState.visitedCells.clear();
    gridState.lastFetchedCount = 0;
    gridState.initialized = false;
}

// Track last fetched cutting path count to avoid redundant fetches
let lastFetchedCount = 0;

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
            lastFetchedCount = data.count || 0;
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
    if (serverCount > lastFetchedCount) {
        fetchGridCells();  // Fire and forget - will update on next status
    } else if (serverCount < lastFetchedCount) {
        // Server reset (new milling operation) - clear local state
        gridState.visitedCells.clear();
        lastFetchedCount = 0;
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
        const confirmed = await showConfirm('Probing equipment removed?', 'Start Milling', { danger: true });
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

function handleStateChange(controllerState) {
    console.log('Mill controller state:', controllerState);

    const btn = $('mill-pause-btn');

    // Feed controls enabled only when running or paused (not during init/homing)
    const feedEnabled = controllerState === CONTROLLER_STATE_RUNNING ||
                        controllerState === CONTROLLER_STATE_PAUSED;
    updateFeedControls(feedEnabled);

    switch (controllerState) {
        case CONTROLLER_STATE_IDLE:
            state.isMilling = false;
            break;
        case CONTROLLER_STATE_INITIALIZING:
        case CONTROLLER_STATE_RUNNING:
            state.isMilling = true;
            updatePauseButton(btn, false);
            break;
        case CONTROLLER_STATE_PAUSED:
            state.isMilling = true;
            updatePauseButton(btn, true);
            break;
        case CONTROLLER_STATE_COMPLETING:
        case CONTROLLER_STATE_COMPLETED:
            state.isMilling = false;
            hideToolChangeOverlay();  // Run is over - any pending prompt is moot
            resetGridState();  // Clear local grid state on completion
            showInfo(TEXT_MILLING_COMPLETE);
            showScreen(SCREEN_DASHBOARD);
            break;
        case CONTROLLER_STATE_FAILED:
        case CONTROLLER_STATE_CANCELLED:
            state.isMilling = false;
            // Run is over - a prompt parked in WaitingForUserInput can be cancelled
            // straight to here without ever being answered, so drop it too.
            hideToolChangeOverlay();
            resetGridState();  // Clear local grid state on cancel/failure
            showScreen(SCREEN_DASHBOARD);
            break;
    }
}

function handleProgressUpdate(progress) {
    const phaseEl = $('mill-phase');
    const progressLinesEl = $('progress-lines');

    // Update phase display with message (but not during Milling phase - line count shown below progress bar)
    if (phaseEl) {
        // During Milling phase, don't show the message (it duplicates progress-lines)
        const showMessage = progress.phase !== 'Milling';
        phaseEl.textContent = showMessage ? (progress.message || progress.phase || '') : '';
    }

    // Update progress lines (use != null to check for both null and undefined)
    if (progressLinesEl && progress.currentStep != null && progress.totalSteps != null) {
        progressLinesEl.textContent = `${progress.currentStep} / ${progress.totalSteps}`;
    }
}

function handleToolChangeEvent(data) {
    console.log('Tool change event:', data);

    // Tool change detected - server auto-starts ToolChangeController.
    // This event is informational. The controller broadcasts its phases
    // via toolchange:state and toolchange:progress events.
    // UI reacts to controller phase via status polling (updateToolChangeDisplay).
    state.isMilling = true;
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
        continueBtn.onclick = () => sendToolChangeInput(data.options.find(opt => opt.toLowerCase().includes('continue')) || 'Continue');
        // Note: abort handler is set via addEventListener in setupMillEventListeners()
        // which shows a confirmation dialog before aborting
    }

    // Now show the overlay - user input is needed
    overlay.classList.remove(CLASS_HIDDEN);
}

function handleToolChangeInput(data) {
    console.log('Tool change input required:', data);

    // Server validates state before sending toolchange:input.
    // Don't check state.isMilling - server is source of truth.
    // Ensure isMilling is set since server confirmed we're in a tool change.
    state.isMilling = true;
    pendingUserInputPrompt = true;
    lastPromptId = data.id;

    // Show user input dialog - this is when user action is actually needed
    // data contains: title, message, options[], id
    renderUserInputPrompt(data);
}

async function sendToolChangeInput(response) {
    try {
        const result = await fetch(API_MILL_TOOLCHANGE_INPUT, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ response })
        });
        const json = await result.json();
        if (!json.success) {
            showError(json.error || 'Failed to send input');
        }
    } catch (err) {
        showError('Failed to send input: ' + err.message);
    }
}

function handleToolChangeComplete(data) {
    console.log('Tool change complete:', data);
    // The prompt (if any) has been answered - let status polling drive the
    // overlay again.
    hideToolChangeOverlay();

    if (!data.success && !data.aborted) {
        // Only show error for actual failures, not user aborts
        showError('Tool change failed');
    }
}

function handleToolChangeError(data) {
    console.error('Tool change error:', data.message);
    showError(data.message);
}
