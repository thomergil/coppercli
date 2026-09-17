// Trusting the work zero from a previous session sets where the next cut goes, so the modal
// stays open until the server replies and a refusal is shown to the operator.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { installDom, load, lastToast } from './dom-stub.mjs';

const { CLASS_HIDDEN } = await load('constants.js');

async function pageAnswering(answer) {
    const dom = installDom();
    globalThis.fetch = async () => answer();

    const trustZero = await load('trust-zero.js');
    trustZero.initTrustZeroModal();

    dom.el('trust-zero-modal').classList.remove(CLASS_HIDDEN);
    return dom;
}

test('a refused trust leaves the question on screen', async () => {
    const dom = await pageAnswering(() => ({
        ok: true,
        json: async () => ({ success: false, error: 'no stored work zero' })
    }));

    await dom.el('trust-zero-yes-btn').fire('click');

    assert.equal(dom.el('trust-zero-modal').classList.contains(CLASS_HIDDEN), false,
        'the window closed on a refusal, so the operator believes a work zero nobody set');
    assert.equal(lastToast()?.textContent, 'no stored work zero',
        'the refusal was not shown, so the operator has nothing to act on');
});

test('a trust the server took closes the question', async () => {
    const dom = await pageAnswering(() => ({
        ok: true,
        json: async () => ({ success: true })
    }));

    await dom.el('trust-zero-yes-btn').fire('click');

    assert.equal(dom.el('trust-zero-modal').classList.contains(CLASS_HIDDEN), true);
});

test('a trust that never reached coppercli leaves the question on screen', async () => {
    const dom = await pageAnswering(() => { throw new Error('no route to host'); });

    await dom.el('trust-zero-yes-btn').fire('click');

    assert.equal(dom.el('trust-zero-modal').classList.contains(CLASS_HIDDEN), false);
    assert.match(lastToast()?.className ?? '', /error/,
        'nothing told the operator the answer never reached coppercli');
});
