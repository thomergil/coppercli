// Home and unlock go over HTTP so a refusal reaches the operator: while alarmed with the door
// open, GRBL refuses both, and over the WebSocket the reason would only be logged.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { installDom, load, lastToast } from './dom-stub.mjs';

const { API_HOME, API_UNLOCK, ERROR_HOME_NOT_SENT, ERROR_UNLOCK_NOT_SENT } = await load('constants.js');

async function pageAnswering(answer, posts) {
    const dom = installDom();
    globalThis.fetch = async (url) => {
        posts.push(url);
        return { ok: true, json: async () => answer };
    };

    const jog = await load('jog.js');
    jog.initJogScreen();
    return dom;
}

for (const [button, path] of [['jog-home-btn', API_HOME], ['jog-unlock-btn', API_UNLOCK]]) {
    test(`${button} shows the server's refusal`, async () => {
        const posts = [];
        const dom = await pageAnswering({ success: false, error: 'The door is open.' }, posts);

        await dom.el(button).fire('click');

        assert.deepEqual(posts, [path]);
        assert.match(lastToast().className, /error/);
        assert.equal(lastToast().textContent, 'The door is open.');
    });
}

for (const [button, notSent] of [['jog-home-btn', ERROR_HOME_NOT_SENT], ['jog-unlock-btn', ERROR_UNLOCK_NOT_SENT]]) {
    test(`${button} still says something about a refusal with no reason`, async () => {
        const dom = await pageAnswering({ success: false }, []);

        await dom.el(button).fire('click');

        assert.equal(lastToast().textContent, notSent);
    });
}
