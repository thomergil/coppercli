// A file whose work zero is not at the job's lower-left corner drove a trace into the frame.
// Its warning has to be put to the operator before a trace or a probe moves the machine, as
// the terminal does, and a No must leave the machine where it is.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { installDom, load, until } from './dom-stub.mjs';

const {
    API_FILE_INFO, API_PROBE_START, API_PROBE_TRACE, API_PROBE_STATUS, CLASS_HIDDEN, TEXT_FILE_WARNINGS_TITLE
} = await load('constants.js');

const WARNING = 'DANGER: the job is not where work zero is';

function stubFetch(posts, warningsToConfirm) {
    return async (url, options = {}) => {
        if (url === API_FILE_INFO) {
            return { ok: true, json: async () => ({ name: 'board.ngc', lines: 42, warningsToConfirm }) };
        }
        if (url === API_PROBE_STATUS) {
            return { ok: true, json: async () => ({ phase: 'Idle' }) };
        }
        if (options.method === 'POST') {
            posts.push(url);
            return { ok: true, json: async () => ({ success: true }) };
        }
        throw new Error(`probe-file-warnings.test.mjs: unexpected fetch ${url}`);
    };
}

function asked(dom) {
    return !dom.el('confirm-modal').classList.contains(CLASS_HIDDEN)
        && dom.el('confirm-title').textContent === TEXT_FILE_WARNINGS_TITLE;
}

async function probeScreen(warningsToConfirm) {
    const dom = installDom();
    dom.el('confirm-modal').classList.add(CLASS_HIDDEN);
    const posts = [];
    globalThis.fetch = stubFetch(posts, warningsToConfirm);
    const probe = await load('probe.js');
    return { dom, posts, probe };
}

for (const [name, start, api] of [['probe', 'startProbing', API_PROBE_START], ['trace', 'traceOutline', API_PROBE_TRACE]]) {
    test(`a No to the file's warnings sends no ${name}`, async () => {
        const { dom, posts, probe } = await probeScreen([WARNING]);

        const running = probe[start]();
        await until(() => asked(dom), 'the file warnings question');
        assert.match(dom.el('confirm-message').innerHTML, /the job is not where work zero is/);
        await dom.el('confirm-no-btn').fire('click');
        await running;

        assert.ok(!posts.includes(api), `the ${name} started after the operator said No`);
    });

    test(`a Yes to the file's warnings starts the ${name}`, async () => {
        const { dom, posts, probe } = await probeScreen([WARNING]);

        const running = probe[start]();
        await until(() => asked(dom), 'the file warnings question');
        await dom.el('confirm-yes-btn').fire('click');
        await running;

        assert.ok(posts.includes(api), `the ${name} did not start after the operator said Yes`);
    });

    test(`a file with nothing to confirm starts the ${name} without asking`, async () => {
        const { dom, posts, probe } = await probeScreen([]);

        await probe[start]();

        assert.ok(!asked(dom), 'the operator was asked about a file with no warnings');
        assert.ok(posts.includes(api));
    });
}
