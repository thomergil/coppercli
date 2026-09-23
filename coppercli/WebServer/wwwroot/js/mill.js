import { state } from './state.js';
import { $, showError, showConfirm, updatePauseButton, postJson, escapeMarkup, format, settleButtons } from './helpers.js';
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
    TEXT_PROBE_REMOVED_QUESTION,
    TEXT_TOOL_CHANGE_FAILED
} from './constants.js';

let premillResolve = null;

export async function startMill() {
    try {
        const canStartResponse = await fetch(API_MILL_CAN_START);
        const canStart = await canStartResponse.json();

        if (!canStart.canStart) {
            const errorMsg = canStart.errors.join('\n');
            showError(errorMsg);
            return;
        }

        const fileResponse = await fetch(API_FILE_INFO);
        const fileInfo = await fileResponse.json();
        if (!fileInfo.name) {
            showError(TEXT_NO_FILE_LOADED);
            return;
        }

        resetGridState();

        const confirmed = await showPremillModal(fileInfo, canStart.warnings || []);
        if (!confirmed) {
            return;
        }

        $('mill-filename').textContent = fileInfo.name;
        $('mill-phase').textContent = '';  // A stale phase from the last run would stay up
        showScreen(SCREEN_MILL);

        // The server does the safety retract and sets the modal G-codes.
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
 * Resolves true when the operator confirms and false when they cancel, from the modal's
 * own buttons.
 */
async function showPremillModal(fileInfo, warnings) {
    const modal = $('premill-modal');
    const fileEl = $('premill-file');
    const linesEl = $('premill-lines');
    const warningsEl = $('premill-warnings');

    fileEl.textContent = fileInfo.name;
    linesEl.textContent = format(TEXT_LINE_COUNT, fileInfo.lines);

    if (warnings.length > 0) {
        warningsEl.innerHTML = warnings.map(w => `<p>⚠️ ${escapeMarkup(w)}</p>`).join('');
        warningsEl.classList.remove(CLASS_HIDDEN);
    } else {
        warningsEl.classList.add(CLASS_HIDDEN);
    }

    // The server holds the depth, so it is reset there rather than here.
    await adjustPremillDepth(DEPTH_ACTION_RESET);

    // Unticked on every open, so the operator confirms for each run, as the terminal asks
    // before each run.
    $('premill-probe-removed-text').textContent = TEXT_PROBE_REMOVED_QUESTION;
    setProbeRemoved(false);

    modal.classList.remove(CLASS_HIDDEN);

    return new Promise((resolve) => {
        // One modal and one pair of buttons, so a second call overwrites the first's resolve.
        // The first is answered false here, or its promise never settles.
        if (premillResolve) {
            const previousResolve = premillResolve;
            premillResolve = null;
            previousResolve(false);
        }

        premillResolve = resolve;
    });
}

// Pressing Start is the operator's yes to the probe question, so Start stays disabled until
// the box is ticked.
function setProbeRemoved(ticked) {
    $('premill-probe-removed').checked = ticked;
    $('premill-start-btn').disabled = !ticked;
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

function formatDepthText(depth) {
    if (depth === 0) {
        return '0';
    }
    const sign = depth > 0 ? '+' : '';
    return `${sign}${depth.toFixed(2)}`;
}

// Wait for the server response before updating the pause button; resume can be refused.
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
            // Keep the mill screen visible when the server cannot confirm a stop.
            showError(result.error || ERROR_STOP_NOT_SENT);
            return;
        }
    } catch (err) {
        console.error('stopMill failed', err);
        showError(ERROR_STOP_NOT_SENT);
        return;
    }

    // The server confirmed cleanup; leave before the next status unlocks the screen.
    endMillRun();
    showScreen(SCREEN_DASHBOARD, true);
}


// Track the displayed prompt by id to distinguish a new prompt from an answered one.
// Send the id with the answer; PendingPrompt checks it.
let shownPrompt = null;

// Clear the displayed prompt when it is answered or the run ends.
function hideToolChangeOverlay() {
    shownPrompt = null;
    const overlay = $('toolchange-overlay');
    if (overlay) {
        overlay.classList.add(CLASS_HIDDEN);
    }
}

/**
 * Draw the current prompt from status so a reloaded client can recover a missed
 * toolchange:input broadcast. Use the prompt id because WaitingForToolChange can
 * also have no prompt pending.
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

    // Hide an unanswered prompt when a stop or remote abort ends the run.
    if (!prompt) {
        overlay.classList.add(CLASS_HIDDEN);
        return;
    }

    renderUserInputPrompt(prompt);
}

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

let gridState = {
    visitedCells: new Set(),  // "x,y" keys, as the server sends them
    minX: 0, maxX: 0,
    minY: 0, maxY: 0,
    currentX: 0, currentY: 0,
    initialized: false,
    // How many cutting-path points the cells above were drawn from, so a status with no
    // new points skips the fetch.
    fetchedCount: 0
};

export function resetGridState() {
    gridState.visitedCells.clear();
    gridState.fetchedCount = 0;
    gridState.initialized = false;
}

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

function mapToGrid(value, min, range, gridSize) {
    if (range < MILL_MIN_RANGE_THRESHOLD) {
        return 0;
    }
    const index = Math.floor((value - min) / range * (gridSize - 1));
    return Math.max(0, Math.min(gridSize - 1, index));
}

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

export function updateMillGrid(status) {
    const canvas = $('mill-grid');
    if (!canvas) return;

    const file = status.file;
    if (!file || file.minX === undefined) return;

    gridState.minX = file.minX;
    gridState.maxX = file.maxX;
    gridState.minY = file.minY;
    gridState.maxY = file.maxY;
    gridState.initialized = true;

    if (status.workPos) {
        gridState.currentX = status.workPos.x;
        gridState.currentY = status.workPos.y;
    }

    const serverCount = status.cuttingPathCount || 0;
    if (serverCount > gridState.fetchedCount) {
        fetchGridCells();  // Not awaited; the next status draws the result
    } else if (serverCount < gridState.fetchedCount) {
        // A smaller count means the server started a new run.
        gridState.visitedCells.clear();
        gridState.fetchedCount = 0;
    }

    drawMillGrid(canvas);
}

function drawMillGrid(canvas) {
    const ctx = canvas.getContext('2d');
    const width = canvas.width;
    const height = canvas.height;

    // The theme is in the stylesheet, so the colors are read from it rather than copied here.
    const style = getComputedStyle(document.documentElement);
    const bgColor = style.getPropertyValue('--bg-color').trim();
    const surfaceColor = style.getPropertyValue('--surface-color').trim();
    const successColor = style.getPropertyValue('--success-color').trim();
    const warningColor = style.getPropertyValue('--warning-color').trim();

    ctx.fillStyle = bgColor;
    ctx.fillRect(0, 0, width, height);

    const { rangeX, rangeY, gridWidth, gridHeight } = calculateGridDimensions();

    const padding = MILL_GRID_PADDING_PX;
    const availableWidth = width - padding * 2;
    const availableHeight = height - padding * 2;
    const cellWidth = availableWidth / gridWidth;
    const cellHeight = availableHeight / gridHeight;
    const cellSize = Math.min(cellWidth, cellHeight);

    const gridPixelWidth = gridWidth * cellSize;
    const gridPixelHeight = gridHeight * cellSize;
    const offsetX = (width - gridPixelWidth) / 2;
    const offsetY = padding;

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

    updateFeedControls(false);

    // The continue button's handler is set when a prompt is drawn, so only abort is wired here.
    const abortBtn = $('toolchange-abort-btn');
    if (abortBtn) abortBtn.addEventListener('click', abortToolChange);

    $('premill-depth-minus').addEventListener('click', () => adjustPremillDepth(DEPTH_ACTION_DECREASE));
    $('premill-depth-plus').addEventListener('click', () => adjustPremillDepth(DEPTH_ACTION_INCREASE));
    $('premill-depth-reset').addEventListener('click', () => adjustPremillDepth(DEPTH_ACTION_RESET));
    $('premill-probe-removed').addEventListener('change',
        () => setProbeRemoved($('premill-probe-removed').checked));
    $('premill-start-btn').addEventListener('click', () => {
        if (!$('premill-probe-removed').checked) {
            return;
        }
        hidePremillModal();
        if (premillResolve) {
            premillResolve(true);
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

function updateFeedControls(enabled) {
    const feedMinus = $('feed-minus');
    const feedPlus = $('feed-plus');
    const feedReset = $('feed-reset');
    if (feedMinus) feedMinus.disabled = !enabled;
    if (feedPlus) feedPlus.disabled = !enabled;
    if (feedReset) feedReset.disabled = !enabled;
}

/**
 * Sets the mill screen's controls from the controller state, from the mill:state broadcast
 * and from each status, so a reloaded page shows the right button. Feed override does
 * nothing while homing, so it stays disabled until the run is cutting.
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

// Whether the door overlay is showing the enclosure sentence. Read from the overlay itself,
// because a flag held beside it could disagree with what is on screen.
function doorSentenceOnScreen() {
    const overlay = $('door-overlay');
    return overlay != null && !overlay.classList.contains(CLASS_HIDDEN);
}

function handleProgressUpdate(progress) {
    const phaseEl = $('mill-phase');

    if (phaseEl) {
        // Hidden during the milling phase, where it would duplicate progress-lines.
        const showMessage = progress.phase !== PHASE_MILLING && !doorSentenceOnScreen();
        phaseEl.textContent = showMessage ? (progress.message || progress.phase || '') : '';
    }

}

function handleToolChangeEvent(data) {
    // Informational only: the server starts the tool-change controller, whose own broadcasts
    // drive the overlay through updateToolChangeDisplay.
    console.log('Tool change event:', data);
}

function handleMillError(data) {
    console.error('Mill error:', data.message);
    showError(data.message);
}


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
 * and probing as well as milling. A run's own enclosure prompt is drawn here rather than in
 * the tool-change overlay, which sits inside the mill screen and is off the page while the
 * operator is on the probe or jog screen, where it would leave them with a held machine,
 * nothing on screen and no way to answer.
 */
export function updateDoorOverlay(status) {
    const overlay = $('door-overlay');
    if (!overlay) return;

    // A run holding at the door published a prompt; its Continue answers that prompt.
    // Without a run, the door text comes from the status and Continue releases the hold.
    const prompt = doorPrompt();

    if (shownPrompt && !prompt) {
        // The run is asking about something else, and its own prompt is what is on screen.
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

    // Which buttons the overlay has is decided in Core: with a run, by the options its prompt
    // offers, and without one, by whether the server would take a release straight from here.
    // The server refuses a release while a run is handling the door itself, so offering it
    // anyway put the question back on screen after it had been answered.
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

    // The overlay blocks the page even with no button: GRBL holds the machine while the
    // door is open, and opening it again during a resume holds it again, so the door is
    // the stop.
    const hasButton = canAbort || canContinue;

    // A release with no run behind it has no earlier prompt a tap could belong to, so it
    // is answerable at once. Either way this is the only place the buttons are enabled.
    if (hasButton) {
        acceptAnswersAfterSettling(
            prompt ? prompt.id : DOOR_RELEASE_PROMPT_ID,
            prompt ? PROMPT_SETTLE_MS : 0);
    }

    overlay.classList.remove(CLASS_HIDDEN);
}

// Shows or hides one of the door overlay's buttons and sets what it answers. Enabling belongs
// to acceptAnswersAfterSettling; setting it here as well re-enabled the button on the next
// status tick, sooner than the settle allows.
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

    const continueBtn = $('toolchange-continue-btn');
    const abortBtn = $('toolchange-abort-btn');

    if (continueBtn && abortBtn && data.options) {
        // Matched against the options the server published, exactly: a near miss would draw a
        // button that cannot answer anything.
        continueBtn.style.display = data.options.includes(PROMPT_OPTION_CONTINUE) ? '' : 'none';
        abortBtn.style.display = data.options.includes(PROMPT_OPTION_ABORT) ? '' : 'none';

        continueBtn.onclick = () => answerPrompt(data);
        // The abort button's handler is set in initMillScreen, which confirms first.
    }

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

    clearTimeout(promptSettleTimer);
    promptSettleTimer = settleButtons(answerButtons(), settleMs);
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

    renderUserInputPrompt(data);
}

function handleToolChangeComplete(data) {
    console.log('Tool change complete:', data);
    // The prompt (if any) has been answered - let status polling drive the
    // overlay again.
    hideToolChangeOverlay();

    if (!data.success && !data.aborted) {
        showError(TEXT_TOOL_CHANGE_FAILED);
    }
}

function handleToolChangeError(data) {
    console.error('Tool change error:', data.message);
    showError(data.message);
}
