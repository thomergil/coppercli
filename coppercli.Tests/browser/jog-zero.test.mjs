// What became of the height map decides whether the next cut is at the right depth, so the
// browser has to draw it in words and as a warning when the G-code no longer matches the
// origin. Nothing else exercises jog.js's zero response.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { installDom, load, lastToast } from './dom-stub.mjs';

const {
    HEIGHT_MAP_TEXT_BY_OUTCOME, ZEROED_MAP_NOT_REAPPLIED, ZEROED_FILE_LEFT_ALONE, TEXT_ZEROED_Z
} = await load('constants.js');

// Returns the page, with the zero endpoint answering `answer`.
async function pageZeroing(answer) {
    const dom = installDom();
    globalThis.fetch = async () => ({ ok: true, json: async () => answer });

    const jog = await load('jog.js');
    jog.initJogScreen();
    return dom;
}

test('an outcome that left the G-code wrong is drawn as a warning, in words', async () => {
    const dom = await pageZeroing(
        { success: true, heightMap: ZEROED_MAP_NOT_REAPPLIED, reloadTheFile: true });

    await dom.el('jog-zero-z-btn').fire('click');

    assert.match(lastToast().className, /error/,
        'an ordinary confirmation for a file that now holds corrections the origin does not match');
    assert.equal(lastToast().textContent,
        `${TEXT_ZEROED_Z} - ${HEIGHT_MAP_TEXT_BY_OUTCOME[ZEROED_MAP_NOT_REAPPLIED]}`);
});

test('an outcome the run kept is drawn as information, in words', async () => {
    const dom = await pageZeroing(
        { success: true, heightMap: ZEROED_FILE_LEFT_ALONE, reloadTheFile: false });

    await dom.el('jog-zero-z-btn').fire('click');

    assert.match(lastToast().className, /info/);
    assert.doesNotMatch(lastToast().textContent, /undefined/,
        'the server sent an outcome name constants.js does not have');
    assert.equal(lastToast().textContent,
        `${TEXT_ZEROED_Z} - ${HEIGHT_MAP_TEXT_BY_OUTCOME[ZEROED_FILE_LEFT_ALONE]}`);
});

test('a refused zero says so and claims nothing about the map', async () => {
    const dom = await pageZeroing({ success: false, error: 'Stop the job first.' });

    await dom.el('jog-zero-z-btn').fire('click');

    assert.match(lastToast().className, /error/);
    assert.equal(lastToast().textContent, 'Stop the job first.');
});
