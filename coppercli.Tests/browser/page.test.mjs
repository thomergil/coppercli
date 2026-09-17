// Nothing else compares index.html against the code that writes to it. An id in one and not
// the other is a write that does nothing.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { installDom } from './dom-stub.mjs';

const JS_DIR = new URL('../../coppercli/WebServer/wwwroot/js/', import.meta.url);
const HTML = new URL('../../coppercli/WebServer/wwwroot/index.html', import.meta.url);

const modules = readdirSync(JS_DIR).filter(f => f.endsWith('.js')).sort();
const pageIds = new Set(
    [...readFileSync(HTML, 'utf8').matchAll(/id="([A-Za-z0-9_-]+)"/g)].map(m => m[1]));

// Every test below asserts inside a loop over these two lists. Empty, the loops pass having
// checked nothing, which is what a moved directory or a renamed page would do here.
assert.ok(modules.length > 0, 'no JavaScript modules found; every check below would be empty');
assert.ok(pageIds.size > 0, 'no element ids found on index.html; every check below would be empty');

// Every way this codebase names an element: getElementById and its $ shorthand, the helpers
// that take an id, and the arrays of ids jog.js iterates.
const NAMES_AN_ID =
    /(?:getElementById|\$|setText|addClass|removeClass|toggleClass|updateButtonState|setDoorButton)\(\s*'([a-z][a-z0-9-]*)'/g;
const ID_ARRAY = /const \w*[Bb]uttons\w* = \[([^\]]*)\]/g;
const ID_PROPERTY = /\bid: *'([a-z][a-z0-9-]*)'/g;

// An id assembled from parts appears in no file as a string, so the check below cannot
// compare it against the page. A variable is fine: it holds a literal from one of the tables
// this file already reads.
const ASSEMBLED_ID = /(?:getElementById|\$)\(\s*`/g;
const CSS_ID = /querySelector(?:All)?\(\s*['"]#([A-Za-z0-9_-]+)/g;

test('no element id is assembled at run time, and CSS id selectors exist on the page', () => {
    for (const file of modules) {
        const src = readFileSync(new URL(file, JS_DIR), 'utf8');
        assert.equal([...src.matchAll(ASSEMBLED_ID)].length, 0,
            `${file} assembles an element id from parts; nothing can compare it against the page`);
        for (const [, id] of src.matchAll(CSS_ID)) {
            assert.ok(pageIds.has(id), `${file} selects #${id}, which index.html does not have`);
        }
    }
});

test('every id the browser writes to exists on the page', () => {
    for (const file of modules) {
        const src = readFileSync(new URL(file, JS_DIR), 'utf8');
        for (const [, id] of src.matchAll(NAMES_AN_ID)) {
            assert.ok(pageIds.has(id), `${file} writes to #${id}, which index.html does not have`);
        }
        for (const [, body] of src.matchAll(ID_ARRAY)) {
            for (const [, id] of body.matchAll(/'([a-z][a-z0-9-]*)'/g)) {
                assert.ok(pageIds.has(id), `${file} lists #${id}, which index.html does not have`);
            }
        }
        for (const [, id] of src.matchAll(ID_PROPERTY)) {
            assert.ok(pageIds.has(id), `${file} names #${id} in a table, but index.html does not have it`);
        }
    }
});

test('every module the page loads can be loaded', async () => {
    installDom();
    globalThis.WebSocket = class { constructor() { } send() { } close() { } };

    for (const file of modules) {
        await import(new URL(file, JS_DIR));
    }
});
