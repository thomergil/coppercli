// The sections picker: the board divided into equal columns and rows over a picture of where
// the file cuts. The - and + buttons and the arrow keys add and remove lines, and tapping a
// section chooses it or clears it. chooseSections resolves with the operator's choice when they
// press Done, and the pre-mill window sends it to the server.

import { $, showError, singleModalAnswer } from './helpers.js';
import {
    API_MILL_SECTIONS,
    SECTIONS_MAX_PER_AXIS,
    SECTIONS_PICTURE_CELL_PX,
    SECTIONS_BOARD_MAX_HEIGHT_VH,
    CLASS_HIDDEN,
    CLASS_SELECTED,
    CLASS_SECTIONS_CELL,
    KEY_ARROW_UP,
    KEY_ARROW_DOWN,
    KEY_ARROW_LEFT,
    KEY_ARROW_RIGHT,
    ERROR_BOARD_NOT_SHOWN
} from './constants.js';

// The division and choices the picker shows until Done or Cancel: { columns, rows, chosen: Set of
// sectionKey }, or null while closed.
let draft = null;

// Done answers with the operator's sections, and Cancel with null. If the picker opens again
// before it is answered, the earlier call gets null.
const pickerAnswer = singleModalAnswer(null);

// The columns and rows each arrow key adds to the division, negative to remove.
const ARROW_STEPS = {
    [KEY_ARROW_UP]: { columns: 0, rows: 1 },
    [KEY_ARROW_DOWN]: { columns: 0, rows: -1 },
    [KEY_ARROW_RIGHT]: { columns: 1, rows: 0 },
    [KEY_ARROW_LEFT]: { columns: -1, rows: 0 }
};

/**
 * Opens the picker on `current`, the sections as the server sends them ({ columns, rows,
 * chosen: [{ column, row }] }, or null for the whole board). Resolves with the operator's
 * sections in the same shape, or null when they cancel or the board cannot be shown.
 */
export async function chooseSections(current) {
    const picture = await fetchPicture();
    if (!picture) {
        return null;
    }

    draft = {
        columns: current?.columns ?? 1,
        rows: current?.rows ?? 1,
        chosen: new Set((current?.chosen ?? []).map(sectionKey))
    };
    drawPicture(picture);
    render();
    $('sections-modal').classList.remove(CLASS_HIDDEN);

    return pickerAnswer.ask();
}

export function initSectionsPicker() {
    $('sections-columns-minus').addEventListener('click', () => divide({ columns: -1, rows: 0 }));
    $('sections-columns-plus').addEventListener('click', () => divide({ columns: 1, rows: 0 }));
    $('sections-rows-minus').addEventListener('click', () => divide({ columns: 0, rows: -1 }));
    $('sections-rows-plus').addEventListener('click', () => divide({ columns: 0, rows: 1 }));
    $('sections-done-btn').addEventListener('click', () => finish({
        columns: draft.columns,
        rows: draft.rows,
        chosen: [...draft.chosen].map(parseSectionKey)
    }));
    $('sections-cancel-btn').addEventListener('click', () => finish(null));

    document.addEventListener('keydown', event => {
        const step = draft && ARROW_STEPS[event.key];
        if (!step) {
            return;
        }
        event.preventDefault();
        divide(step);
    });
}

// The picture of where the file cuts, or null once the operator has been told why there is none.
async function fetchPicture() {
    let response;
    try {
        response = await fetch(API_MILL_SECTIONS);
    } catch (err) {
        console.error('chooseSections: could not fetch the board', err);
        showError(ERROR_BOARD_NOT_SHOWN);
        return null;
    }

    const body = await response.json().catch(() => ({}));
    if (!response.ok) {
        showError(body.error || ERROR_BOARD_NOT_SHOWN);
        return null;
    }
    return body;
}

function sectionKey(section) {
    return `${section.column},${section.row}`;
}

function parseSectionKey(key) {
    const [column, row] = key.split(',').map(Number);
    return { column, row };
}

function finish(result) {
    $('sections-modal').classList.add(CLASS_HIDDEN);
    draft = null;
    pickerAnswer.give(result);
}

// A new division has new sections, so the ones chosen on the old one are cleared.
function divide(step) {
    const columns = withinLimits(draft.columns + step.columns);
    const rows = withinLimits(draft.rows + step.rows);
    if (columns === draft.columns && rows === draft.rows) {
        return;
    }
    draft = { columns, rows, chosen: new Set() };
    render();
}

function withinLimits(count) {
    return Math.min(Math.max(count, 1), SECTIONS_MAX_PER_AXIS);
}

function toggle(key) {
    if (!draft.chosen.delete(key)) {
        draft.chosen.add(key);
    }
    render();
}

function render() {
    $('sections-columns-value').textContent = String(draft.columns);
    $('sections-rows-value').textContent = String(draft.rows);
    $('sections-columns-minus').disabled = draft.columns <= 1;
    $('sections-columns-plus').disabled = draft.columns >= SECTIONS_MAX_PER_AXIS;
    $('sections-rows-minus').disabled = draft.rows <= 1;
    $('sections-rows-plus').disabled = draft.rows >= SECTIONS_MAX_PER_AXIS;

    const grid = $('sections-grid');
    grid.innerHTML = '';
    grid.style.gridTemplateColumns = `repeat(${draft.columns}, 1fr)`;
    grid.style.gridTemplateRows = `repeat(${draft.rows}, 1fr)`;

    // The grid lays out from the top, and row 0 is the bottom of the board.
    for (let row = draft.rows - 1; row >= 0; row--) {
        for (let column = 0; column < draft.columns; column++) {
            const key = sectionKey({ column, row });
            const cell = document.createElement('button');
            cell.classList.add(CLASS_SECTIONS_CELL);
            cell.classList.toggle(CLASS_SELECTED, draft.chosen.has(key));
            cell.addEventListener('click', () => toggle(key));
            grid.appendChild(cell);
        }
    }
}

// Draws where the file cuts: `picture` is { width, height, cut: [{ column, row }] }, row 0 at
// the bottom.
function drawPicture(picture) {
    const board = $('sections-board');
    board.style.aspectRatio = `${picture.width} / ${picture.height}`;
    board.style.maxWidth = `${SECTIONS_BOARD_MAX_HEIGHT_VH * picture.width / picture.height}vh`;

    const canvas = $('sections-picture');
    canvas.width = picture.width * SECTIONS_PICTURE_CELL_PX;
    canvas.height = picture.height * SECTIONS_PICTURE_CELL_PX;

    // The theme is in the stylesheet, so the colors are read from it rather than copied here.
    const style = getComputedStyle(document.documentElement);
    const ctx = canvas.getContext('2d');
    ctx.fillStyle = style.getPropertyValue('--bg-color').trim();
    ctx.fillRect(0, 0, canvas.width, canvas.height);

    ctx.fillStyle = style.getPropertyValue('--text-dim').trim();
    for (const { column, row } of picture.cut) {
        ctx.fillRect(
            column * SECTIONS_PICTURE_CELL_PX,
            (picture.height - 1 - row) * SECTIONS_PICTURE_CELL_PX,
            SECTIONS_PICTURE_CELL_PX,
            SECTIONS_PICTURE_CELL_PX);
    }
}
