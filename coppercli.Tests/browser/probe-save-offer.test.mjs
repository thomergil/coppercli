// The terminal offers to save a finished map as soon as the grid completes (ProbeMenu's
// PromptSaveProbeData). The browser has to do the same from the status stream, and only from
// the moment a run of its own finishes - not every time the dashboard reconnects onto a
// complete map, which would open the save screen over whatever the operator was doing.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { installDom, load, until, payload } from './dom-stub.mjs';

const {
    API_PROBE_STATUS, API_PROBE_APPLY, API_PROBE_FILES, API_PROBE_SAVE,
    PROBE_STATE_COMPLETE, PROBE_STATE_PARTIAL, SCREEN_PROBE, SCREEN_PROBE_FILES, SCREEN_DASHBOARD,
    TEXT_PROBING_DONE_TITLE, CLASS_HIDDEN
} = await load('constants.js');

function probeStatusResponse(overrides = {}) {
    return {
        active: false,
        hasUnsavedData: false,
        paused: false,
        progress: 1,
        total: 1,
        sizeX: 1,
        sizeY: 1,
        hasHeights: true,
        minHeight: -0.1,
        maxHeight: -0.1,
        points: [[-0.1]],
        colors: [['#000000']],
        phase: 'Idle',
        suggestedFileName: 'board.pgrid',
        state: PROBE_STATE_COMPLETE,
        ...overrides
    };
}

function stubFetch(posts, statusOverrides = {}) {
    return async (url) => {
        if (url === API_PROBE_STATUS) {
            return { ok: true, json: async () => probeStatusResponse(statusOverrides) };
        }
        if (url === API_PROBE_APPLY) {
            posts.push(url);
            return { ok: true, json: async () => ({ success: true }) };
        }
        if (url.startsWith(API_PROBE_FILES)) {
            return { ok: true, json: async () => ({ currentPath: '/tmp', entries: [] }) };
        }
        throw new Error(`probe-save-offer.test.mjs: unexpected fetch ${url}`);
    };
}

test('a probe run that just finished opens the save screen with the server\'s suggested name', async () => {
    const dom = installDom();
    const posts = [];
    globalThis.fetch = stubFetch(posts);

    const screens = await load('screens.js');
    const { state } = await load('state.js');
    state.isProbing = true;
    state.probeDataDisplayed = false;
    state.currentScreen = SCREEN_PROBE;

    screens.updateStatus(payload({ probing: false, probe: { state: PROBE_STATE_COMPLETE } }));

    await until(() => state.currentScreen === SCREEN_PROBE_FILES, 'the save screen to open');

    assert.equal(dom.el('probe-save-input').value, 'board.pgrid',
        'the save screen did not offer the name the server suggested');
    assert.ok(posts.includes(API_PROBE_APPLY),
        'the finished map was not applied to the G-code, as the terminal does by default');
});

test('a run ending on an incomplete map does not open the save screen', async () => {
    const dom = installDom();
    const posts = [];
    globalThis.fetch = stubFetch(posts, { state: PROBE_STATE_PARTIAL });

    const screens = await load('screens.js');
    const { state } = await load('state.js');
    state.isProbing = true;
    state.probeDataDisplayed = false;
    state.currentScreen = SCREEN_PROBE;

    screens.updateStatus(payload({ probing: false, probe: { state: PROBE_STATE_PARTIAL } }));

    await until(() => state.currentScreen === SCREEN_DASHBOARD, 'the run to end on the dashboard');

    assert.notEqual(state.currentScreen, SCREEN_PROBE_FILES,
        'a run that ended without a complete map opened the save screen');
    assert.equal(dom.el('probe-save-input').value, '',
        'the save screen was primed for a map the run did not finish');
    assert.ok(!posts.includes(API_PROBE_APPLY),
        'an incomplete map was applied to the G-code');
});

test('a complete map reported after a reconnect does not open the save screen on its own', async () => {
    const dom = installDom();
    const posts = [];
    globalThis.fetch = stubFetch(posts);

    const screens = await load('screens.js');
    const { state } = await load('state.js');
    state.isProbing = false;
    state.probeDataDisplayed = false;
    state.currentScreen = SCREEN_DASHBOARD;

    screens.updateStatus(payload({ probe: { state: PROBE_STATE_COMPLETE } }));

    await until(() => dom.el('probe-progress-title').textContent === TEXT_PROBING_DONE_TITLE,
        'the reconnect read of the map to finish');

    assert.equal(state.currentScreen, SCREEN_DASHBOARD,
        'a map the page reconnected onto opened the save screen without being asked');
    assert.equal(dom.el('probe-save-input').value, '',
        'the save screen was primed for a map nobody just finished measuring');
});

test('reading the probe data on its own does not open the save screen', async () => {
    const dom = installDom();
    const posts = [];
    globalThis.fetch = stubFetch(posts);

    const probe = await load('probe.js');
    const { state } = await load('state.js');
    state.currentScreen = SCREEN_DASHBOARD;

    await probe.fetchAndDisplayProbeData();

    assert.equal(state.currentScreen, SCREEN_DASHBOARD,
        'fetchAndDisplayProbeData opened the save screen with nothing having just finished');
    assert.equal(dom.el('probe-save-input').value, '');
});

/**
 * Stubs API_PROBE_SAVE: the first save for a path answers 409 with fileExists, naming
 * question as the error; a save carrying overwrite: true answers success. Every request is
 * recorded in posts.
 */
function stubFetchForSaving(posts, question) {
    return async (url, options = {}) => {
        if (url === API_PROBE_FILES || url.startsWith(API_PROBE_FILES)) {
            return { ok: true, json: async () => ({ currentPath: '/tmp', entries: [] }) };
        }
        if (url === API_PROBE_STATUS) {
            return { ok: true, json: async () => probeStatusResponse() };
        }
        if (url === API_PROBE_SAVE) {
            const body = JSON.parse(options.body);
            posts.push(body);
            if (!body.overwrite) {
                return { ok: false, json: async () => ({ success: false, error: question, fileExists: true }) };
            }
            return { ok: true, json: async () => ({ success: true, path: '/tmp/board.pgrid' }) };
        }
        throw new Error(`probe-save-offer.test.mjs: unexpected fetch ${url}`);
    };
}

async function openSaveScreenWithFilename(filename) {
    const dom = installDom();
    dom.el('confirm-modal').classList.add(CLASS_HIDDEN);

    const probe = await load('probe.js');
    probe.initProbeFilesScreen();
    await probe.showProbeFileBrowser('save');
    dom.el('probe-save-input').value = filename;

    return { dom, probe };
}

test('saving onto an existing file shows the confirm with the server\'s question, and resends with overwrite on yes', async () => {
    const question = 'Overwrite board.pgrid?';
    const posts = [];
    globalThis.fetch = stubFetchForSaving(posts, question);

    const { dom } = await openSaveScreenWithFilename('board.pgrid');

    dom.el('probe-file-action-btn').fire('click');
    await until(() => !dom.el('confirm-modal').classList.contains(CLASS_HIDDEN),
        'the overwrite confirmation to open');
    assert.equal(dom.el('confirm-message').textContent, question,
        'the confirm did not show the question the server sent');

    await until(() => !dom.el('confirm-yes-btn').disabled, 'the confirmation to settle');
    await dom.el('confirm-yes-btn').fire('click');

    await until(() => posts.length === 2, 'the resend with overwrite set');
    assert.equal(posts[0].overwrite, undefined, 'the first save already carried overwrite');
    assert.equal(posts[1].overwrite, true, 'the resend after yes did not carry overwrite: true');
});

test('declining the overwrite confirmation sends nothing more', async () => {
    const question = 'Overwrite board.pgrid?';
    const posts = [];
    globalThis.fetch = stubFetchForSaving(posts, question);

    const { dom } = await openSaveScreenWithFilename('board.pgrid');

    dom.el('probe-file-action-btn').fire('click');
    await until(() => !dom.el('confirm-modal').classList.contains(CLASS_HIDDEN),
        'the overwrite confirmation to open');

    await until(() => !dom.el('confirm-no-btn').disabled, 'the confirmation to settle');
    await dom.el('confirm-no-btn').fire('click');

    // saveProbeToFile re-enables the action button in a finally block once the save chain it
    // started resolves, so waiting for that is waiting for a decline with nothing further to
    // send to have actually run, not just for the click that started it.
    await until(() => !dom.el('probe-file-action-btn').disabled, 'the save action to finish declining');
    assert.equal(posts.length, 1, 'a decline sent a request beyond the one the server refused');
});
