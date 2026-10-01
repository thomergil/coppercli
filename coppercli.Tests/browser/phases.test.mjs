// The pre-mill window lists a job's phases when it has more than one, so the operator can skip
// one, for example to cut a board out without milling its traces again. The tests check that the
// list shows only when can-start offers it and shows the phases it sent. They check that clearing
// a box sends the phases still ticked with the version the window holds, and that Start waits for
// the reply and confirms the version that reply named. They also check that a refused change
// shows the reason and puts the boxes back as the server holds them.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { installDom, load, until, lastToast, HTTP_CONFLICT } from './dom-stub.mjs';

const {
    API_MILL_PHASES, API_MILL_CAN_START, API_FILE_INFO, API_MILL_START, API_MILL_DEPTH, CLASS_HIDDEN
} = await load('constants.js');

const OPENED_VERSION = 7;
const CHANGED_VERSION = 8;
const DEPTH_VERSION = 9;
const REFUSAL = 'Choose at least one phase to mill.';
const TWO_PHASES = [
    { number: 1, label: 'Phase 1 · 19m 12s', chosen: true },
    { number: 2, label: 'Phase 2 · 20m 16s', chosen: true }
];
const SECOND_ONLY = [{ ...TWO_PHASES[0], chosen: false }, TWO_PHASES[1]];

// Serves can-start with `phases`, offering a choice when there is more than one, answers each
// phases POST with `onPhases` and each depth POST with `onDepth`, and records the bodies posted.
function premillFetch({ phases, onPhases, onDepth, phasesBodies, depthBodies, startBodies }) {
    return async (url, options = {}) => {
        if (url === API_MILL_CAN_START) {
            return { ok: true, json: async () => ({
                canStart: true, warnings: [], version: OPENED_VERSION, depth: 0,
                offerPhases: phases.length > 1, jobPhases: phases }) };
        }
        if (url === API_FILE_INFO) {
            return { ok: true, json: async () => ({ name: 'board.ngc', lines: 42 }) };
        }
        if (url === API_MILL_PHASES) {
            phasesBodies.push(JSON.parse(options.body));
            return onPhases();
        }
        if (url === API_MILL_DEPTH) {
            depthBodies.push(JSON.parse(options.body));
            return onDepth();
        }
        if (url === API_MILL_START) {
            startBodies.push(JSON.parse(options.body));
            return { ok: true, json: async () => ({ success: true }) };
        }
        throw new Error(`phases.test.mjs: unexpected fetch ${url}`);
    };
}

const acceptSecondOnly = () => ({
    ok: true, json: async () => ({ success: true, version: CHANGED_VERSION, jobPhases: SECOND_ONLY })
});
const refuse = () => ({ ok: false, status: HTTP_CONFLICT, json: async () => ({ error: REFUSAL }) });
const acceptDepth = () => ({ ok: true, json: async () => ({ success: true, depth: 0, version: DEPTH_VERSION }) });

// A reply held until `release` is called.
function heldReply(reply) {
    let release;
    const released = new Promise(resolve => { release = resolve; });
    return { answer: async () => { await released; return reply(); }, release: () => release() };
}

async function openPremill(phases, onPhases = acceptSecondOnly, onDepth = acceptDepth) {
    const dom = installDom();
    dom.el('premill-modal').classList.add(CLASS_HIDDEN);
    const phasesBodies = [];
    const depthBodies = [];
    const startBodies = [];
    globalThis.fetch = premillFetch({ phases, onPhases, onDepth, phasesBodies, depthBodies, startBodies });
    const mill = await load('mill.js');
    mill.initMillScreen();
    const running = mill.startMill();
    await until(() => !dom.el('premill-modal').classList.contains(CLASS_HIDDEN), 'the pre-mill modal to open');
    return { dom, running, phasesBodies, depthBodies, startBodies };
}

// Each listed phase as [its label, whether its box is ticked].
function listed(dom) {
    return dom.el('premill-phases-list').children.map(label => [label.children[1].textContent, label.children[0].checked]);
}

function box(dom, index) {
    return dom.el('premill-phases-list').children[index].children[0];
}

async function start(dom, running) {
    dom.el('premill-probe-removed').checked = true;
    dom.el('premill-probe-removed').fire('change');
    dom.el('premill-start-btn').fire('click');
    await running;
}

test('a job that offers no choice of phases hides them', async () => {
    const { dom, running } = await openPremill([TWO_PHASES[0]]);

    assert.equal(dom.el('premill-phases-group').classList.contains(CLASS_HIDDEN), true,
        'a job with one phase offered a choice of phases');

    await dom.el('premill-cancel-btn').fire('click');
    await running;
});

test('a job with two phases lists each with its label, ticked as can-start sent', async () => {
    const { dom, running } = await openPremill(SECOND_ONLY);

    assert.equal(dom.el('premill-phases-group').classList.contains(CLASS_HIDDEN), false,
        'the phases of a job with two tools were not shown');
    assert.deepEqual(listed(dom), SECOND_ONLY.map(phase => [phase.label, phase.chosen]));

    await dom.el('premill-cancel-btn').fire('click');
    await running;
});

test('clearing a phase sends the phases still ticked, and Start waits for the reply and confirms its version', async () => {
    let release;
    const replied = new Promise(resolve => { release = resolve; });
    const { dom, running, phasesBodies, startBodies } = await openPremill(TWO_PHASES, async () => {
        await replied;
        return acceptSecondOnly();
    });

    box(dom, 0).checked = false;
    box(dom, 0).fire('change');
    await until(() => phasesBodies.length === 1, 'the phases to be posted');
    assert.deepEqual(phasesBodies[0], { chosen: [2], version: OPENED_VERSION });

    // Start is pressed before the reply arrives.
    const started = start(dom, running);
    release();
    await started;

    assert.equal(startBodies[0].version, CHANGED_VERSION,
        'the start did not confirm the version the phases change made');
    assert.deepEqual(listed(dom), SECOND_ONLY.map(phase => [phase.label, phase.chosen]));
});

test('a refused change shows the reason, puts the box back, and Start keeps the old version', async () => {
    const { dom, running, phasesBodies, startBodies } = await openPremill(TWO_PHASES, refuse);

    box(dom, 1).checked = false;
    box(dom, 1).fire('change');
    await until(() => phasesBodies.length === 1, 'the phases to be posted');
    await until(() => box(dom, 1).checked, 'the box to be ticked again');

    assert.equal(lastToast().textContent, REFUSAL, 'the server\'s reason was not shown');
    assert.deepEqual(listed(dom), TWO_PHASES.map(phase => [phase.label, phase.chosen]));

    await start(dom, running);
    assert.equal(startBodies[0].version, OPENED_VERSION, 'a refused change moved the version Start confirms');
});

test('a change the server refuses after Start was pressed stops the start', async () => {
    const held = heldReply(refuse);
    const { dom, running, phasesBodies, startBodies } = await openPremill(TWO_PHASES, held.answer);

    box(dom, 0).checked = false;
    box(dom, 0).fire('change');
    await until(() => phasesBodies.length === 1, 'the phases to be posted');

    const started = start(dom, running);
    held.release();
    await started;

    assert.deepEqual(startBodies, [], 'the job started though the change the operator made was refused');
    assert.equal(lastToast().textContent, REFUSAL, 'the server\'s reason was not shown');
});

test('the boxes stay disabled until the reply draws the list again', async () => {
    const held = heldReply(acceptSecondOnly);
    const { dom, running, phasesBodies } = await openPremill(TWO_PHASES, held.answer);

    box(dom, 0).checked = false;
    box(dom, 0).fire('change');
    await until(() => phasesBodies.length === 1, 'the phases to be posted');

    assert.equal(box(dom, 1).disabled, true, 'a box could be changed while the list was about to be replaced');
    held.release();
    await until(() => !box(dom, 1).disabled, 'the reply to draw the list again');

    await dom.el('premill-cancel-btn').fire('click');
    await running;
});

test('a phases change waits for a depth change still pending, and names the version its reply made', async () => {
    const held = heldReply(acceptDepth);
    const { dom, running, phasesBodies, depthBodies } = await openPremill(TWO_PHASES, acceptSecondOnly, held.answer);

    dom.el('premill-depth-plus').fire('click');
    await until(() => depthBodies.length === 1, 'the depth change to be posted');
    box(dom, 0).checked = false;
    box(dom, 0).fire('change');
    await new Promise(resolve => setTimeout(resolve, 0));

    assert.equal(phasesBodies.length, 0, 'the phases change was sent before the depth change was answered');
    held.release();
    await until(() => phasesBodies.length === 1, 'the phases to be posted');
    assert.equal(phasesBodies[0].version, DEPTH_VERSION, 'the phases change did not name the version the depth reply made');

    await dom.el('premill-cancel-btn').fire('click');
    await running;
});
