// Loading a board can drop the height map already probed. The terminal prints the reason;
// without this the browser removes it from the probe panel and prints nothing.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { installDom, load, lastToast } from './dom-stub.mjs';

const { TEXT_HEIGHT_MAP_DROPPED, TEXT_FILE_LOADED } = await load('constants.js');
const { format } = await load('helpers.js');
const { state } = await load('state.js');

async function pageAfterLoad(reply) {
    const dom = installDom();
    globalThis.fetch = async () => ({ ok: true, json: async () => reply });

    const file = await load('file.js');
    state.selectedFile = '/boards/b.ngc';
    await file.loadFile();

    return dom;
}

test('a load that dropped the height map says why', async () => {
    await pageAfterLoad({
        success: true,
        name: 'b.ngc',
        lines: 9,
        droppedMap: 'it was measured for a.ngc'
    });

    assert.equal(
        lastToast().textContent,
        format(TEXT_HEIGHT_MAP_DROPPED, 'it was measured for a.ngc'),
        'the map was dropped with no reason on screen');
});

test('a load that dropped nothing says nothing about a map', async () => {
    await pageAfterLoad({ success: true, name: 'b.ngc', lines: 9 });

    assert.equal(lastToast().textContent, format(TEXT_FILE_LOADED, 'b.ngc', 9));
});
