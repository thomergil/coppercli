// The sections picker lets the operator mill part of the board: the board is divided into
// columns and rows over a picture of where the file cuts, and the chosen sections go to the
// server through the pre-mill window. These tests guard that the picker shows what it was given
// (division, chosen cells, row 0 at the bottom), that every way of changing the division keeps
// to its limits and clears the choice, that Done and Cancel answer as labelled, and that the
// pre-mill window sends the choice with the version it confirms and keeps its old text and
// version when the server refuses.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { installDom, load, until, dispatch, lastToast, HTTP_CONFLICT } from './dom-stub.mjs';

const {
    API_MILL_SECTIONS, API_MILL_CAN_START, API_FILE_INFO, API_MILL_START,
    SECTIONS_MAX_PER_AXIS, SECTIONS_PICTURE_CELL_PX, CLASS_SECTIONS_CELL, CLASS_SELECTED, CLASS_HIDDEN,
    KEY_ARROW_UP, KEY_ARROW_DOWN, KEY_ARROW_LEFT, KEY_ARROW_RIGHT,
    ERROR_BOARD_NOT_SHOWN, ERROR_SECTIONS_NOT_SET
} = await load('constants.js');

const PICTURE_WIDTH = 4;
const PICTURE_HEIGHT = 3;
const PICTURE = { width: PICTURE_WIDTH, height: PICTURE_HEIGHT, cut: [{ column: 0, row: 0 }, { column: 2, row: 1 }] };
const SERVER_ERROR = 'the board is not loaded';

const OPENED_VERSION = 7;
const CHANGED_VERSION = 8;
const OPENED_TEXT = '1 of 4';
const CHANGED_TEXT = '1 of 4 sections';
const OPENED_SECTIONS = { columns: 2, rows: 2, chosen: [{ column: 1, row: 0 }] };
const PICKED = { columns: 2, rows: 2, chosen: [{ column: 0, row: 1 }] };
const REFUSAL = 'the job changed';

function pictureFetch(picture = PICTURE) {
    return async () => ({ ok: true, json: async () => picture });
}

async function setup(current, fetchStub = pictureFetch()) {
    const dom = installDom();
    dom.el('sections-modal').classList.add(CLASS_HIDDEN);
    globalThis.fetch = fetchStub;
    const sections = await load('sections.js');
    sections.initSectionsPicker();
    const answer = sections.chooseSections(current);
    await until(() => !dom.el('sections-modal').classList.contains(CLASS_HIDDEN), 'the picker to open');
    return { dom, answer };
}

const cells = dom => dom.el('sections-grid').children;
const selectedCells = dom => cells(dom).filter(c => c.classList.contains(CLASS_SELECTED));
const shown = dom => [dom.el('sections-columns-value').textContent, dom.el('sections-rows-value').textContent];
const pressKey = key => {
    const event = { key, prevented: false, preventDefault() { this.prevented = true; } };
    return dispatch('keydown', event);
};

test('a picker opened on nothing shows one column, one row and one cell', async () => {
    const { dom, answer } = await setup(null);

    assert.deepEqual(shown(dom), ['1', '1'], 'the picker did not start on the whole board');
    assert.equal(cells(dom).filter(c => c.classList.contains(CLASS_SECTIONS_CELL)).length, 1,
        'the whole board was not one section');

    await dom.el('sections-cancel-btn').fire('click');
    await answer;
});

test('a picker opened on a division lays its cells out top row first and selects the chosen ones', async () => {
    const { dom, answer } = await setup({
        columns: 3, rows: 2, chosen: [{ column: 0, row: 1 }, { column: 2, row: 0 }] });

    assert.deepEqual(shown(dom), ['3', '2'], 'the division shown is not the one the server sent');
    assert.equal(cells(dom).length, 6, 'the grid does not have columns x rows cells');
    const selected = cells(dom).map((c, i) => c.classList.contains(CLASS_SELECTED) ? i : -1).filter(i => i >= 0);
    assert.deepEqual(selected, [0, 5],
        'the chosen sections are drawn in the wrong places: row 0 must be the bottom row of the grid');

    await dom.el('sections-cancel-btn').fire('click');
    await answer;
});

test('plus and minus add and remove one line, stay within 1 and the maximum, and clear the choice', async () => {
    const { dom, answer } = await setup({ columns: 2, rows: 1, chosen: [{ column: 0, row: 0 }] });

    await dom.el('sections-columns-plus').fire('click');
    assert.deepEqual(shown(dom), ['3', '1'], 'plus did not add a column');
    assert.equal(selectedCells(dom).length, 0, 'a new division kept the sections chosen on the old one');

    await dom.el('sections-rows-plus').fire('click');
    await dom.el('sections-rows-minus').fire('click');
    assert.deepEqual(shown(dom), ['3', '1'], 'plus then minus did not leave the rows where they were');

    for (let i = 0; i < SECTIONS_MAX_PER_AXIS; i++) {
        await dom.el('sections-columns-plus').fire('click');
        await dom.el('sections-rows-plus').fire('click');
    }
    assert.deepEqual(shown(dom), [String(SECTIONS_MAX_PER_AXIS), String(SECTIONS_MAX_PER_AXIS)],
        'plus went past the maximum');
    assert.equal(dom.el('sections-columns-plus').disabled, true, 'plus stayed live at the maximum columns');
    assert.equal(dom.el('sections-rows-plus').disabled, true, 'plus stayed live at the maximum rows');
    assert.equal(dom.el('sections-columns-minus').disabled, false, 'minus was dead above 1 column');

    for (let i = 0; i < SECTIONS_MAX_PER_AXIS; i++) {
        await dom.el('sections-columns-minus').fire('click');
        await dom.el('sections-rows-minus').fire('click');
    }
    assert.deepEqual(shown(dom), ['1', '1'], 'minus went below 1');
    assert.equal(dom.el('sections-columns-minus').disabled, true, 'minus stayed live at 1 column');
    assert.equal(dom.el('sections-rows-minus').disabled, true, 'minus stayed live at 1 row');

    await dom.el('sections-cancel-btn').fire('click');
    await answer;
});

test('the arrow keys change the division while the picker is open, and stop the page scrolling', async () => {
    const { dom, answer } = await setup(null);

    const steps = [
        [KEY_ARROW_UP, ['1', '2']],
        [KEY_ARROW_RIGHT, ['2', '2']],
        [KEY_ARROW_DOWN, ['2', '1']],
        [KEY_ARROW_LEFT, ['1', '1']]
    ];
    for (const [key, expected] of steps) {
        const event = pressKey(key);
        assert.deepEqual(shown(dom), expected, `${key} did not change the division as it should`);
        assert.equal(event.prevented, true, `${key} was not stopped, so the page scrolls under the picker`);
    }

    await dom.el('sections-cancel-btn').fire('click');
    await answer;
});

test('the arrow keys do nothing while the picker is closed', async () => {
    const { dom, answer } = await setup(null);
    await dom.el('sections-cancel-btn').fire('click');
    await answer;

    const event = pressKey(KEY_ARROW_UP);

    assert.equal(event.prevented, false, 'an arrow key was swallowed while the picker was closed');
    assert.equal(cells(dom).length, 1, 'an arrow key redrew a closed picker');
});

test('clicking a cell toggles its selection', async () => {
    const { dom, answer } = await setup({ columns: 2, rows: 1, chosen: [] });

    await cells(dom)[1].fire('click');
    assert.equal(cells(dom)[1].classList.contains(CLASS_SELECTED), true, 'a click did not choose the section');

    await cells(dom)[1].fire('click');
    assert.equal(cells(dom)[1].classList.contains(CLASS_SELECTED), false, 'a second click did not unchoose it');

    await dom.el('sections-cancel-btn').fire('click');
    await answer;
});

test('Done answers with what is shown and hides the picker', async () => {
    const { dom, answer } = await setup({ columns: 2, rows: 2, chosen: [{ column: 1, row: 0 }] });
    await cells(dom)[0].fire('click');

    await dom.el('sections-done-btn').fire('click');
    const result = await answer;

    assert.deepEqual(result, { columns: 2, rows: 2, chosen: [{ column: 1, row: 0 }, { column: 0, row: 1 }] },
        'Done did not answer with the division and sections on screen');
    assert.equal(dom.el('sections-modal').classList.contains(CLASS_HIDDEN), true, 'Done left the picker open');
});

test('Cancel answers null and hides the picker', async () => {
    const { dom, answer } = await setup({ columns: 2, rows: 2, chosen: [{ column: 1, row: 0 }] });

    await dom.el('sections-cancel-btn').fire('click');

    assert.equal(await answer, null, 'Cancel answered with sections');
    assert.equal(dom.el('sections-modal').classList.contains(CLASS_HIDDEN), true, 'Cancel left the picker open');
});

test('a refused board shows the server reason, answers null and never opens the picker', async () => {
    const dom = installDom();
    dom.el('sections-modal').classList.add(CLASS_HIDDEN);
    globalThis.fetch = async () => ({ ok: false, json: async () => ({ error: SERVER_ERROR }) });
    const sections = await load('sections.js');
    sections.initSectionsPicker();

    const result = await sections.chooseSections(null);

    assert.equal(result, null, 'a refused board answered with sections');
    assert.equal(lastToast().textContent, SERVER_ERROR, 'the server\'s reason was not shown');
    assert.equal(dom.el('sections-modal').classList.contains(CLASS_HIDDEN), true,
        'the picker opened over a board it could not draw');
});

test('a board request that throws shows that the board could not be shown', async () => {
    const dom = installDom();
    dom.el('sections-modal').classList.add(CLASS_HIDDEN);
    globalThis.fetch = async () => { throw new Error('offline'); };
    const sections = await load('sections.js');
    sections.initSectionsPicker();
    const originalError = console.error;
    console.error = () => { };

    let result;
    try {
        result = await sections.chooseSections(null);
    } finally {
        console.error = originalError;
    }

    assert.equal(result, null, 'a failed request answered with sections');
    assert.equal(lastToast().textContent, ERROR_BOARD_NOT_SHOWN, 'the failure was not shown to the operator');
    assert.equal(dom.el('sections-modal').classList.contains(CLASS_HIDDEN), true, 'the picker opened with no board');
});

// A server that answers but not in JSON, a proxy's error page for example, is still an answer:
// the operator is told the board could not be shown, and nothing throws past the picker.
test('a refused board request whose body is not JSON shows that the board could not be shown', async () => {
    const dom = installDom();
    dom.el('sections-modal').classList.add(CLASS_HIDDEN);
    globalThis.fetch = async () => ({ ok: false, json: async () => { throw new SyntaxError('not JSON'); } });
    const sections = await load('sections.js');
    sections.initSectionsPicker();

    const result = await sections.chooseSections(null);

    assert.equal(result, null, 'a refused request answered with sections');
    assert.equal(lastToast().textContent, ERROR_BOARD_NOT_SHOWN, 'the refusal was not shown to the operator');
    assert.equal(dom.el('sections-modal').classList.contains(CLASS_HIDDEN), true, 'the picker opened with no board');
});

test('the picture is one background fill and one square per cut cell, row 0 at the bottom', async () => {
    const { dom, answer } = await setup(null);

    const px = SECTIONS_PICTURE_CELL_PX;
    const rects = dom.el('sections-picture').getContext('2d').fillRects;
    assert.deepEqual(rects, [
        [0, 0, PICTURE_WIDTH * px, PICTURE_HEIGHT * px],
        [0, (PICTURE_HEIGHT - 1 - 0) * px, px, px],
        [2 * px, (PICTURE_HEIGHT - 1 - 1) * px, px, px]
    ], 'the picture did not draw the background plus each cut cell with row 0 at the bottom');

    await dom.el('sections-cancel-btn').fire('click');
    await answer;
});

// The pre-mill window. Serves can-start with sections and their text, the board picture, and
// the sections POST through `onSections`; records the body of each start.
function premillFetch({ onSections, startBodies, sectionsBodies }) {
    return async (url, options = {}) => {
        if (url === API_MILL_CAN_START) {
            return { ok: true, json: async () => ({
                canStart: true, warnings: [], version: OPENED_VERSION, depth: 0,
                sections: OPENED_SECTIONS, sectionsText: OPENED_TEXT, jobPhases: [] }) };
        }
        if (url === API_FILE_INFO) {
            return { ok: true, json: async () => ({ name: 'board.ngc', lines: 42 }) };
        }
        if (url === API_MILL_SECTIONS && options.method === 'POST') {
            sectionsBodies.push(JSON.parse(options.body));
            return onSections();
        }
        if (url === API_MILL_SECTIONS) {
            return { ok: true, json: async () => PICTURE };
        }
        if (url === API_MILL_START) {
            startBodies.push(JSON.parse(options.body));
            return { ok: true, json: async () => ({ success: true }) };
        }
        throw new Error(`sections.test.mjs: unexpected fetch ${url}`);
    };
}

const acceptSections = () => ({
    ok: true,
    json: async () => ({ success: true, version: CHANGED_VERSION, sections: PICKED, sectionsText: CHANGED_TEXT })
});
const refuseSections = () => ({ ok: false, status: HTTP_CONFLICT, json: async () => ({ error: REFUSAL }) });

async function openPremill(onSections) {
    const dom = installDom();
    dom.el('premill-modal').classList.add(CLASS_HIDDEN);
    dom.el('sections-modal').classList.add(CLASS_HIDDEN);
    const startBodies = [];
    const sectionsBodies = [];
    globalThis.fetch = premillFetch({ onSections, startBodies, sectionsBodies });
    const mill = await load('mill.js');
    mill.initMillScreen();
    const running = mill.startMill();
    await until(() => !dom.el('premill-modal').classList.contains(CLASS_HIDDEN), 'the pre-mill modal to open');
    return { dom, running, startBodies, sectionsBodies };
}

async function pickPremillSections(dom, sectionsBodies, expectedPosts) {
    const picking = dom.el('premill-sections-btn').fire('click');
    await until(() => !dom.el('sections-modal').classList.contains(CLASS_HIDDEN), 'the picker to open');
    await cells(dom)[0].fire('click'); // (column 0, row 1) of 2x2, the top left cell
    await dom.el('sections-done-btn').fire('click');
    await picking;
    await until(() => sectionsBodies.length === expectedPosts, 'the sections to be posted');
}

async function startAndFinish(dom, running) {
    dom.el('premill-probe-removed').checked = true;
    dom.el('premill-probe-removed').fire('change');
    dom.el('premill-start-btn').fire('click');
    await running;
}

test('the pre-mill window shows the sections text can-start sent', async () => {
    const { dom, running } = await openPremill(acceptSections);

    assert.equal(dom.el('premill-sections-value').textContent, OPENED_TEXT,
        'the window did not say which sections the job will cut');

    await dom.el('premill-cancel-btn').fire('click');
    await running;
});

test('the sections button opens the picker on the sections can-start sent', async () => {
    const { dom, running } = await openPremill(acceptSections);

    const picking = dom.el('premill-sections-btn').fire('click');
    await until(() => !dom.el('sections-modal').classList.contains(CLASS_HIDDEN), 'the picker to open');

    assert.deepEqual(shown(dom), ['2', '2'], 'the picker opened on a division other than the job\'s');
    const selected = cells(dom).map((c, i) => c.classList.contains(CLASS_SELECTED) ? i : -1).filter(i => i >= 0);
    assert.deepEqual(selected, [3], 'the picker did not select the section the job already has (column 1, row 0)');

    await dom.el('sections-cancel-btn').fire('click');
    await picking;
    await dom.el('premill-cancel-btn').fire('click');
    await running;
});

test('Done posts the choice with the can-start version, and the reply\'s text and version replace the old ones', async () => {
    const { dom, running, startBodies, sectionsBodies } = await openPremill(acceptSections);

    await pickPremillSections(dom, sectionsBodies, 1);

    assert.deepEqual(sectionsBodies[0], {
        columns: PICKED.columns, rows: PICKED.rows, chosen: [{ column: 1, row: 0 }, { column: 0, row: 1 }],
        version: OPENED_VERSION },
        'the choice was not posted with the version the window opened on');
    await until(() => dom.el('premill-sections-value').textContent === CHANGED_TEXT,
        'the reply\'s sections text to be shown');

    await startAndFinish(dom, running);
    assert.equal(startBodies[0].version, CHANGED_VERSION,
        'the start did not confirm the version the sections change made');
});

// The window keeps the sections the reply named, so opening the picker again starts from the
// choice the server made rather than the one the window first opened on.
test('after a choice the picker opens again on the sections the reply named', async () => {
    const { dom, running, sectionsBodies } = await openPremill(acceptSections);
    await pickPremillSections(dom, sectionsBodies, 1);
    await until(() => dom.el('premill-sections-value').textContent === CHANGED_TEXT, 'the reply to be shown');

    const picking = dom.el('premill-sections-btn').fire('click');
    await until(() => !dom.el('sections-modal').classList.contains(CLASS_HIDDEN), 'the picker to open again');

    const selected = cells(dom).map((c, i) => c.classList.contains(CLASS_SELECTED) ? i : -1).filter(i => i >= 0);
    assert.deepEqual(selected, [0],
        'the picker opened on the sections can-start sent, not the ones the reply named (column 0, row 1)');

    await dom.el('sections-cancel-btn').fire('click');
    await picking;
    await dom.el('premill-cancel-btn').fire('click');
    await running;
});

test('a refused choice shows the reason and keeps the old text and the old version', async () => {
    const { dom, running, startBodies, sectionsBodies } = await openPremill(refuseSections);

    await pickPremillSections(dom, sectionsBodies, 1);
    await until(() => lastToast()?.textContent === REFUSAL, 'the refusal to be shown');

    assert.equal(dom.el('premill-sections-value').textContent, OPENED_TEXT,
        'a refused choice replaced the text the window still holds');
    await startAndFinish(dom, running);
    assert.equal(startBodies[0].version, OPENED_VERSION,
        'a refused choice moved the version the start confirms');
});

test('a sections change that the server does not answer with a reason shows the not-set message', async () => {
    const { dom, running, sectionsBodies } = await openPremill(
        () => ({ ok: false, json: async () => ({}) }));

    await pickPremillSections(dom, sectionsBodies, 1);
    await until(() => lastToast()?.textContent === ERROR_SECTIONS_NOT_SET, 'the not-set message to be shown');

    await dom.el('premill-cancel-btn').fire('click');
    await running;
});

test('Cancel in the picker posts nothing', async () => {
    const { dom, running, sectionsBodies } = await openPremill(acceptSections);

    const picking = dom.el('premill-sections-btn').fire('click');
    await until(() => !dom.el('sections-modal').classList.contains(CLASS_HIDDEN), 'the picker to open');
    await dom.el('sections-cancel-btn').fire('click');
    await picking;

    assert.deepEqual(sectionsBodies, [], 'a canceled picker still posted a sections change');
    assert.equal(dom.el('premill-sections-value').textContent, OPENED_TEXT, 'Cancel changed the sections text');

    await dom.el('premill-cancel-btn').fire('click');
    await running;
});
