// View Map shows the height map again after the probe screen has been closed, as the
// terminal's Probe menu does. It must only show the map: applying it is the operator's choice
// elsewhere, and a run that starts while the map is up must get its Stop button back.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { installDom, load, until, payload, lastToast } from './dom-stub.mjs';

const {
    API_PROBE_STATUS, API_PROBE_APPLY,
    PROBE_STATE_COMPLETE, PROBE_STATE_PARTIAL,
    TEXT_HEIGHT_MAP_TITLE, TEXT_PROBING_TITLE, TEXT_NO_HEIGHTS, CLASS_HIDDEN
} = await load('constants.js');

const MEASURED_COLOR = '#123456';

function probeStatusResponse(overrides = {}) {
    return {
        active: false,
        hasUnsavedData: false,
        paused: false,
        progress: 2,
        total: 2,
        sizeX: 2,
        sizeY: 1,
        hasHeights: true,
        minHeight: -0.1,
        maxHeight: 0.1,
        points: [[-0.1], [0.1]],
        colors: [[MEASURED_COLOR], [MEASURED_COLOR]],
        phase: 'Idle',
        suggestedFileName: 'board.pgrid',
        state: PROBE_STATE_COMPLETE,
        ...overrides
    };
}

// Answers the probe status with the given fields and records every request to apply.
function stubFetch(applies, statusOverrides = {}) {
    return async (url) => {
        if (url === API_PROBE_STATUS) {
            return { ok: true, json: async () => probeStatusResponse(statusOverrides) };
        }
        if (url === API_PROBE_APPLY) {
            applies.push(url);
            return { ok: true, json: async () => ({ success: true }) };
        }
        throw new Error(`probe-view-map.test.mjs: unexpected fetch ${url}`);
    };
}

function hidden(dom, id) {
    return dom.el(id).classList.contains(CLASS_HIDDEN);
}

// The stub page starts with no classes, so the probe screen is put in the state index.html
// loads it in: the setup up, the progress view and its finished-run buttons hidden.
function probeScreen() {
    const dom = installDom();
    for (const id of ['probe-progress', 'probe-done-btn', 'probe-view-close-btn']) {
        dom.el(id).classList.add(CLASS_HIDDEN);
    }
    return dom;
}

test('View Map is live only when the server reports a measured height', async () => {
    const dom = probeScreen();
    globalThis.fetch = stubFetch([]);
    const probe = await load('probe.js');

    probe.updateProbeButtonsFromState(PROBE_STATE_PARTIAL, false, false);
    assert.equal(dom.el('probe-view-btn').disabled, true, 'View Map was live with no height to show');

    probe.updateProbeButtonsFromState(PROBE_STATE_COMPLETE, false, true);
    assert.equal(dom.el('probe-view-btn').disabled, false, 'View Map stayed dead with a map to show');
});

test('View Map shows the map with Close in place of the run\'s buttons, and applies nothing', async () => {
    const dom = probeScreen();
    const applies = [];
    globalThis.fetch = stubFetch(applies);
    const probe = await load('probe.js');
    const { state } = await load('state.js');
    probe.initProbeScreen();

    await dom.el('probe-view-btn').fire('click');
    await until(() => !hidden(dom, 'probe-progress'), 'the map to be shown');

    assert.equal(dom.el('probe-progress-title').textContent, TEXT_HEIGHT_MAP_TITLE);
    assert.equal(hidden(dom, 'probe-setup'), true);
    assert.equal(hidden(dom, 'probe-view-close-btn'), false, 'no way back from the map');
    for (const id of ['probe-pause-btn', 'probe-stop-btn', 'probe-done-btn']) {
        assert.equal(hidden(dom, id), true, `${id} was offered for a map with no run behind it`);
    }

    assert.equal(dom.el('probe-grid').children.length, 2, 'the grid was not drawn cell for cell');
    assert.match(dom.el('probe-height-range').textContent, /-0\.100 to 0\.100/,
        'the Z range of the map was not shown');

    assert.deepEqual(applies, [], 'viewing the map applied it to the G-code');
    assert.equal(state.probeDataDisplayed, true,
        'the status handler could take the open map for a finished run and apply it');

    dom.el('probe-view-close-btn').fire('click');
    assert.equal(hidden(dom, 'probe-setup'), false, 'Close did not return to the probe setup');
    assert.equal(hidden(dom, 'probe-progress'), true);
});

test('a map with no measured height is refused with the reason, not drawn', async () => {
    const dom = probeScreen();
    globalThis.fetch = stubFetch([], { hasHeights: false, state: PROBE_STATE_PARTIAL });
    const probe = await load('probe.js');

    await probe.showHeightMap();

    assert.equal(hidden(dom, 'probe-progress'), true, 'an empty map was put up');
    assert.equal(lastToast()?.textContent, TEXT_NO_HEIGHTS);
});

test('a run that starts while the map is up gets Pause and Stop, not Close', async () => {
    const dom = probeScreen();
    globalThis.fetch = stubFetch([]);
    const probe = await load('probe.js');
    const screens = await load('screens.js');
    const { state } = await load('state.js');
    state.isProbing = false;

    await probe.showHeightMap();
    screens.updateStatus(payload({ probing: true, probe: { state: PROBE_STATE_PARTIAL } }));

    assert.equal(dom.el('probe-progress-title').textContent, TEXT_PROBING_TITLE);
    assert.equal(hidden(dom, 'probe-stop-btn'), false, 'a running probe had no Stop button');
    assert.equal(hidden(dom, 'probe-pause-btn'), false);
    assert.equal(hidden(dom, 'probe-view-close-btn'), true, 'Close stayed up over a running probe');

    state.isProbing = false;
});
