// The pre-mill modal is the operator's last chance to say the probing equipment is off the
// board before the tool moves. startMill must not reach mill/start until the probe-removed box
// is ticked, and each time the modal opens it must ask again rather than remember the last
// run's answer. The start confirms the version of the job the window was opened on, or the one
// its own depth change made, so the server can refuse a job that changed after the operator
// looked at it.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { installDom, load, until } from './dom-stub.mjs';

const {
    API_MILL_CAN_START, API_FILE_INFO, API_MILL_START, API_MILL_DEPTH,
    CLASS_HIDDEN, TEXT_PROBE_REMOVED_QUESTION, DEPTH_ACTION_RESET, HOME_FIRST_BY_DEFAULT
} = await load('constants.js');

const OPENED_VERSION = 7;

function stubFetch(posts) {
    return async (url, options = {}) => {
        if (url === API_MILL_CAN_START) {
            return { ok: true, json: async () => ({ canStart: true, warnings: [], version: OPENED_VERSION, depth: 0, jobPhases: [] }) };
        }
        if (url === API_FILE_INFO) {
            return { ok: true, json: async () => ({ name: 'board.ngc', lines: 42 }) };
        }
        if (url === API_MILL_DEPTH) {
            posts.push(url);
            return { ok: true, json: async () => ({ success: true, depth: 0, version: OPENED_VERSION + 1 }) };
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

test('opening the modal does not post a depth change, and the Reset button still does', async () => {
    const dom = installDom();
    dom.el('premill-modal').classList.add(CLASS_HIDDEN);
    const depthPosts = [];
    const fallback = stubFetch([]);
    globalThis.fetch = async (url, options = {}) => {
        if (url === API_MILL_DEPTH) {
            depthPosts.push(options.body);
            return { ok: true, json: async () => ({ success: true, depth: 0, version: OPENED_VERSION + 1 }) };
        }
        return fallback(url, options);
    };

    const mill = await load('mill.js');
    mill.initMillScreen();

    const running = mill.startMill();
    await until(() => modalOpen(dom), 'the pre-mill modal to open');

    assert.deepEqual(depthPosts, [],
        'opening the modal changed the depth, so the last run\'s depth was lost before the operator saw it');

    await dom.el('premill-depth-reset').fire('click');
    await until(() => depthPosts.length === 1, 'the Reset button to post a depth change');
    assert.equal(JSON.parse(depthPosts[0]).action, DEPTH_ACTION_RESET);

    await dom.el('premill-cancel-btn').fire('click');
    await running;
});

const SHOWN_DEPTH = -0.2;
const CHANGED_VERSION = 9;

const OPENED_DEPTH = -0.1;

// Serves can-start with or without the home-first offer at OPENED_DEPTH, records the bodies
// posted to mill/start and to mill/depth, and answers the nth depth change with SHOWN_DEPTH
// and CHANGED_VERSION + n once release(n) is called, or at once without a release list.
function stubFetchOffering(offerHomeFirst, startBodies, depthBodies = [], releases = null) {
    return async (url, options = {}) => {
        if (url === API_MILL_CAN_START) {
            return { ok: true, json: async () => ({
                canStart: true, warnings: [], offerHomeFirst, version: OPENED_VERSION, depth: OPENED_DEPTH, jobPhases: [] }) };
        }
        if (url === API_MILL_DEPTH) {
            const n = depthBodies.push(JSON.parse(options.body)) - 1;
            if (releases) {
                await new Promise(resolve => { releases[n] = resolve; });
            }
            return { ok: true, json: async () => ({ success: true, depth: SHOWN_DEPTH, version: CHANGED_VERSION + n }) };
        }
        if (url === API_MILL_START) {
            startBodies.push(JSON.parse(options.body));
            return { ok: true, json: async () => ({ success: true }) };
        }
        return stubFetch([])(url, options);
    };
}

async function openThenStart(dom, mill, beforeStart = async () => {}) {
    const running = mill.startMill();
    await until(() => modalOpen(dom), 'the pre-mill modal to open');
    await beforeStart();
    dom.el('premill-probe-removed').checked = true;
    dom.el('premill-probe-removed').fire('change');
    dom.el('premill-start-btn').fire('click');
    await running;
}

test('a can-start that offers home-first shows the row ticked, and the start body carries the answer and the version the depth change made', async () => {
    const dom = installDom();
    dom.el('premill-modal').classList.add(CLASS_HIDDEN);
    const bodies = [];
    globalThis.fetch = stubFetchOffering(true, bodies);

    const mill = await load('mill.js');
    mill.initMillScreen();

    await openThenStart(dom, mill, async () => {
        assert.equal(dom.el('premill-home-first-row').classList.contains(CLASS_HIDDEN), false,
            'the home-first question was not shown after a stop while cutting');
        assert.equal(dom.el('premill-home-first').checked, HOME_FIRST_BY_DEFAULT,
            'the home-first box did not start at the answer the terminal starts at');
        await dom.el('premill-depth-plus').fire('click');
        await until(() => dom.el('premill-depth-value').textContent === SHOWN_DEPTH.toFixed(2),
            'the depth reply to be shown');
    });

    assert.equal(bodies.length, 1);
    assert.equal(bodies[0].version, CHANGED_VERSION, 'the start did not confirm the version its own depth change made');
    assert.equal(bodies[0].homeFirst, true, 'the ticked home-first answer was not sent');
});

test('an unticked home-first box is sent as false', async () => {
    const dom = installDom();
    dom.el('premill-modal').classList.add(CLASS_HIDDEN);
    const bodies = [];
    globalThis.fetch = stubFetchOffering(true, bodies);

    const mill = await load('mill.js');
    mill.initMillScreen();

    await openThenStart(dom, mill, async () => {
        dom.el('premill-home-first').checked = false;
    });

    assert.equal(bodies[0].homeFirst, false, 'declining to home first was not sent, so the server homed anyway');
});

test('a can-start that does not offer home-first hides the row and sends no homeFirst', async () => {
    const dom = installDom();
    dom.el('premill-modal').classList.add(CLASS_HIDDEN);
    const bodies = [];
    globalThis.fetch = stubFetchOffering(false, bodies);

    const mill = await load('mill.js');
    mill.initMillScreen();

    await openThenStart(dom, mill, async () => {
        assert.equal(dom.el('premill-home-first-row').classList.contains(CLASS_HIDDEN), true,
            'the home-first question was shown when it was not offered');
    });

    assert.equal(bodies.length, 1);
    assert.equal('homeFirst' in bodies[0], false, 'homeFirst was sent though the question was never asked');
    assert.equal(bodies[0].version, OPENED_VERSION, 'the start did not confirm the version the window opened on');
});

test('the window shows the depth can-start reports, the depth of the version it confirms', async () => {
    const dom = installDom();
    dom.el('premill-modal').classList.add(CLASS_HIDDEN);
    const bodies = [];
    globalThis.fetch = stubFetchOffering(false, bodies);

    const mill = await load('mill.js');
    mill.initMillScreen();

    await openThenStart(dom, mill, async () => {
        assert.equal(dom.el('premill-depth-value').textContent, OPENED_DEPTH.toFixed(2),
            'the window showed a depth other than the one its version holds');
    });
});

test('depth changes go one at a time, each confirming the version the last reply named', async () => {
    const dom = installDom();
    dom.el('premill-modal').classList.add(CLASS_HIDDEN);
    const bodies = [];
    const depthBodies = [];
    const releases = [];
    globalThis.fetch = stubFetchOffering(false, bodies, depthBodies, releases);

    const mill = await load('mill.js');
    mill.initMillScreen();

    await openThenStart(dom, mill, async () => {
        dom.el('premill-depth-plus').fire('click');
        dom.el('premill-depth-plus').fire('click');
        await until(() => releases[0], 'the first depth change to reach the server');
        assert.equal(depthBodies.length, 1, 'the second change was sent before the first was answered');

        releases[0]();
        await until(() => releases[1], 'the second depth change to reach the server');
        releases[1]();
        await until(() => dom.el('premill-depth-value').textContent === SHOWN_DEPTH.toFixed(2),
            'the second reply to be shown');
    });

    assert.deepEqual(depthBodies.map(b => b.version), [OPENED_VERSION, CHANGED_VERSION],
        'a depth change did not confirm the version the window held');
    assert.equal(bodies[0].version, CHANGED_VERSION + 1, 'the start did not confirm the last reply\'s version');
});

test('reopening the modal after an unticked home-first answer shows the box ticked again', async () => {
    const dom = installDom();
    dom.el('premill-modal').classList.add(CLASS_HIDDEN);
    const bodies = [];
    globalThis.fetch = stubFetchOffering(true, bodies);

    const mill = await load('mill.js');
    mill.initMillScreen();

    await openThenStart(dom, mill, async () => {
        dom.el('premill-home-first').checked = false;
    });
    await openThenStart(dom, mill);

    assert.equal(bodies[1].homeFirst, HOME_FIRST_BY_DEFAULT, 'the last answer carried over instead of the default');
});
