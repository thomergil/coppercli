// The pre-mill modal is the operator's last chance to say the probing equipment is off the
// board before the tool moves. startMill must not reach mill/start until that box is ticked,
// and every fresh open must ask again rather than remember the last run's answer.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { installDom, load, until } from './dom-stub.mjs';

const {
    API_MILL_CAN_START, API_FILE_INFO, API_MILL_START, API_MILL_DEPTH,
    CLASS_HIDDEN, TEXT_PROBE_REMOVED_QUESTION
} = await load('constants.js');

function stubFetch(posts) {
    return async (url, options = {}) => {
        if (url === API_MILL_CAN_START) {
            return { ok: true, json: async () => ({ canStart: true, warnings: [] }) };
        }
        if (url === API_FILE_INFO) {
            return { ok: true, json: async () => ({ name: 'board.ngc', lines: 42 }) };
        }
        if (url === API_MILL_DEPTH) {
            posts.push(url);
            return { ok: true, json: async () => ({ success: true, depth: 0 }) };
        }
        if (url === API_MILL_START) {
            posts.push(url);
            return { ok: true, json: async () => ({ success: true }) };
        }
        throw new Error(`premill.test.mjs: unexpected fetch ${url}`);
    };
}

function modalOpen(dom) {
    return !dom.el('premill-modal').classList.contains(CLASS_HIDDEN);
}

test('startMill opens the pre-mill modal with the probe-removed box unticked and Start disabled', async () => {
    const dom = installDom();
    dom.el('premill-modal').classList.add(CLASS_HIDDEN);
    const posts = [];
    globalThis.fetch = stubFetch(posts);

    const mill = await load('mill.js');
    mill.initMillScreen();

    const running = mill.startMill();
    await until(() => modalOpen(dom), 'the pre-mill modal to open');

    assert.equal(dom.el('premill-probe-removed').checked, false,
        'the box came up ticked, so Start needed no confirmation from the operator');
    assert.equal(dom.el('premill-start-btn').disabled, true,
        'Start was live before the operator confirmed the probe is off the board');
    assert.equal(dom.el('premill-probe-removed-text').textContent, TEXT_PROBE_REMOVED_QUESTION);

    await dom.el('premill-cancel-btn').fire('click');
    await running;
});

test('ticking the probe-removed box enables Start, and unticking it disables Start again', async () => {
    const dom = installDom();
    dom.el('premill-modal').classList.add(CLASS_HIDDEN);
    const posts = [];
    globalThis.fetch = stubFetch(posts);

    const mill = await load('mill.js');
    mill.initMillScreen();

    const running = mill.startMill();
    await until(() => modalOpen(dom), 'the pre-mill modal to open');

    const checkbox = dom.el('premill-probe-removed');
    checkbox.checked = true;
    checkbox.fire('change');
    assert.equal(dom.el('premill-start-btn').disabled, false,
        'ticking the box left Start dead, so the operator has no way to start the job');

    checkbox.checked = false;
    checkbox.fire('change');
    assert.equal(dom.el('premill-start-btn').disabled, true,
        'unticking the box after it was ticked left Start live with the box empty again');

    await dom.el('premill-cancel-btn').fire('click');
    await running;
});

test('clicking Start while the box is unticked does not start the job and leaves the modal open', async () => {
    const dom = installDom();
    dom.el('premill-modal').classList.add(CLASS_HIDDEN);
    const posts = [];
    globalThis.fetch = stubFetch(posts);

    const mill = await load('mill.js');
    mill.initMillScreen();

    const running = mill.startMill();
    await until(() => modalOpen(dom), 'the pre-mill modal to open');

    dom.el('premill-start-btn').fire('click');

    assert.equal(modalOpen(dom), true, 'Start with the box unticked closed the pre-mill modal');

    // Checked once the run has ended, so a start the unticked click set off has been sent.
    await dom.el('premill-cancel-btn').fire('click');
    await running;
    assert.ok(!posts.includes(API_MILL_START), 'Start with the box unticked reached the server');
});

test('Start with the box ticked hides the modal and posts to mill/start, raising no confirm modal', async () => {
    const dom = installDom();
    dom.el('premill-modal').classList.add(CLASS_HIDDEN);
    dom.el('confirm-modal').classList.add(CLASS_HIDDEN);
    const posts = [];
    globalThis.fetch = stubFetch(posts);

    const mill = await load('mill.js');
    mill.initMillScreen();

    const running = mill.startMill();
    await until(() => modalOpen(dom), 'the pre-mill modal to open');

    dom.el('premill-probe-removed').checked = true;
    dom.el('premill-probe-removed').fire('change');
    dom.el('premill-start-btn').fire('click');
    await running;

    assert.equal(modalOpen(dom), false, 'the pre-mill modal stayed open after Start was pressed');
    assert.ok(posts.includes(API_MILL_START), 'Start did not reach the server');
    assert.equal(dom.el('confirm-modal').classList.contains(CLASS_HIDDEN), true,
        'a second confirmation popped up for an answer the pre-mill modal already took');
});

test('Cancel closes the modal and never reaches mill/start', async () => {
    const dom = installDom();
    dom.el('premill-modal').classList.add(CLASS_HIDDEN);
    const posts = [];
    globalThis.fetch = stubFetch(posts);

    const mill = await load('mill.js');
    mill.initMillScreen();

    const running = mill.startMill();
    await until(() => modalOpen(dom), 'the pre-mill modal to open');

    dom.el('premill-cancel-btn').fire('click');
    await running;

    assert.equal(modalOpen(dom), false, 'Cancel left the pre-mill modal open');
    assert.ok(!posts.includes(API_MILL_START), 'Cancel started the job it was meant to call off');
});

test('opening the modal again resets the probe-removed box and Start, even right after a ticked run', async () => {
    const dom = installDom();
    dom.el('premill-modal').classList.add(CLASS_HIDDEN);
    const posts = [];
    globalThis.fetch = stubFetch(posts);

    const mill = await load('mill.js');
    mill.initMillScreen();

    const first = mill.startMill();
    await until(() => modalOpen(dom), 'the first pre-mill modal to open');
    dom.el('premill-probe-removed').checked = true;
    dom.el('premill-probe-removed').fire('change');
    dom.el('premill-start-btn').fire('click');
    await first;

    const second = mill.startMill();
    await until(() => modalOpen(dom), 'the second pre-mill modal to open');

    assert.equal(dom.el('premill-probe-removed').checked, false,
        'the second run remembered the first run had answered yes');
    assert.equal(dom.el('premill-start-btn').disabled, true,
        'the second run left Start enabled from the answer the first run gave');

    await dom.el('premill-cancel-btn').fire('click');
    await second;
});
