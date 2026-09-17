// A button that shows progress carries an icon beside its label. Writing textContent removes
// the icon, and putting only the word back leaves it gone until the page is reloaded.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { installDom, load } from './dom-stub.mjs';

const { whileBusy } = await load('helpers.js');

test('a busy button comes back with its icon', () => {
    const dom = installDom();
    const btn = dom.el('load-file-btn');
    btn.innerHTML = '<svg></svg> Load';

    const done = whileBusy(btn, 'Loading...');

    assert.equal(btn.disabled, true);
    assert.equal(btn.textContent, 'Loading...');

    done();

    assert.equal(btn.disabled, false);
    assert.equal(btn.innerHTML, '<svg></svg> Load');
});

test('no button is not an error', () => {
    installDom();

    whileBusy(null, 'Loading...')();
});
