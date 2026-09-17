// The take-over modal is the only way to reclaim a machine another browser holds. Which
// error means that is the server's answer, not something read out of its wording.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { installDom, load } from './dom-stub.mjs';

const { MSG_TYPE_CONNECTION_ERROR, CLASS_HIDDEN, TITLE_FORCE_DISCONNECT } = await load('constants.js');

// Returns the page after it has handled a connection:error carrying `data`.
async function pageAfterError(data) {
    const dom = installDom();

    let socket = null;
    globalThis.WebSocket = class {
        static OPEN = 1;
        constructor() { socket = this; this.readyState = 1; }
        send() { }
        close() { }
    };
    globalThis.fetch = async () => ({ ok: true, json: async () => ({ success: true }) });
    globalThis.window = { location: { protocol: 'http:', host: 'mill' } };
    globalThis.document.cookie = '';

    const websocket = await load('websocket.js');
    websocket.connectWebSocket();
    dom.el('confirm-modal').classList.add(CLASS_HIDDEN);

    socket.onmessage({ data: JSON.stringify({ type: MSG_TYPE_CONNECTION_ERROR, data }) });
    await new Promise(resolve => setTimeout(resolve, 0));
    return dom;
}

test('a rejection the server marks as another client offers the take-over', async () => {
    const dom = await pageAfterError({ error: 'anything at all', otherClientConnected: true });

    assert.equal(dom.el('confirm-modal').classList.contains(CLASS_HIDDEN), false);
    assert.equal(dom.el('confirm-title').textContent, TITLE_FORCE_DISCONNECT);
});

test('a rejection for any other reason does not, whatever it says', async () => {
    const dom = await pageAfterError({
        error: 'Connection rejected: another client is already connected.',
        otherClientConnected: false
    });

    assert.equal(dom.el('confirm-modal').classList.contains(CLASS_HIDDEN), true,
        'the take-over was offered for a failure the operator cannot take over');
});
