// The server decides which session questions apply and what each answer does. The browser asks
// them one at a time, answers the question it showed, and never sends an unanswered question as
// "no", which deletes an unsaved or unfinished height map.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { installDom, load, lastToast, until } from './dom-stub.mjs';

const { API_SESSION_RESTORE, CLASS_HIDDEN } = await load('constants.js');
const { showConfirm } = await load('helpers.js');
const { askPendingQuestions } = await load('session-restore.js');
const { state } = await load('state.js');

const RELOAD = { topic: 'ReloadFile', question: 'Reload the file you had open?', detail: 'b.ngc' };
const MAP = { topic: 'SavedHeightMap', question: 'Apply the height map you saved?', detail: 'b.map' };

/**
 * A fake server holding `pending`: an answer removes its question, as SessionRestore does,
 * unless it is for `refuse.topic`, which is refused with `refuse.reason`. `afterRead` runs
 * after each read with the count so far, to change what is pending between reads. Returns
 * what the browser posted and a count of its reads.
 */
function serverWith(pending, { refuse = null, afterRead = () => { } } = {}) {
    const posted = [];
    let reads = 0;

    globalThis.fetch = async (url, options = {}) => {
        if (url === API_SESSION_RESTORE && options.method !== 'POST') {
            reads++;
            const steps = [...pending];
            afterRead(reads);
            return { ok: true, json: async () => ({ steps }) };
        }
        if (url === API_SESSION_RESTORE) {
            const answer = JSON.parse(options.body);
            posted.push(answer);
            if (answer.topic === refuse?.topic) {
                return { ok: false, json: async () => ({ success: false, error: refuse.reason }) };
            }
            pending.splice(pending.findIndex(step => step.topic === answer.topic), 1);
            return { ok: true, json: async () => ({ success: true }) };
        }
        return { ok: true, json: async () => ({ success: true, name: 'b.ngc', lines: 3 }) };
    };

    return { posted, reads: () => reads };
}

function questionShown(dom, question) {
    return !dom.el('confirm-modal').classList.contains(CLASS_HIDDEN)
        && dom.el('confirm-title').textContent === question;
}

test('each pending question is asked in turn and answered by its topic', async () => {
    const dom = installDom();
    dom.el('confirm-modal').classList.add(CLASS_HIDDEN);
    const server = serverWith([{ ...RELOAD }, { ...MAP }]);

    const pass = askPendingQuestions();

    await until(() => questionShown(dom, RELOAD.question), 'the first question');
    assert.equal(dom.el('confirm-message').textContent, RELOAD.detail);
    await dom.el('confirm-yes-btn').fire('click');

    await until(() => questionShown(dom, MAP.question), 'the second question');
    assert.equal(dom.el('confirm-yes-btn').disabled, true,
        'the second question took answers at once, so a double-tap on the first answers it too');
    await until(() => !dom.el('confirm-no-btn').disabled, 'the second question to settle');
    await dom.el('confirm-no-btn').fire('click');

    await pass;
    assert.deepEqual(server.posted, [
        { topic: RELOAD.topic, detail: RELOAD.detail, yes: true },
        { topic: MAP.topic, detail: MAP.detail, yes: false }
    ]);
    assert.equal(dom.el('confirm-modal').classList.contains(CLASS_HIDDEN), true);
});

test('a refused answer is shown, and the pass goes on to the other questions', async () => {
    const dom = installDom();
    dom.el('confirm-modal').classList.add(CLASS_HIDDEN);
    const server = serverWith([{ ...RELOAD }, { ...MAP }],
        { refuse: { topic: RELOAD.topic, reason: 'The file could not be loaded.' } });

    const pass = askPendingQuestions();
    await until(() => questionShown(dom, RELOAD.question), 'the first question');
    await dom.el('confirm-yes-btn').fire('click');

    await until(() => questionShown(dom, MAP.question), 'the question after the refused one');
    assert.equal(lastToast()?.textContent, 'The file could not be loaded.');
    await until(() => !dom.el('confirm-yes-btn').disabled, 'the question to settle');
    await dom.el('confirm-yes-btn').fire('click');
    await pass;

    assert.deepEqual(server.posted.map(answer => answer.topic), [RELOAD.topic, MAP.topic],
        'the refused question was asked again in the same pass, or held back the next one');
});

test('a trigger during a pass runs the pass again when it ends', async () => {
    const dom = installDom();
    dom.el('confirm-modal').classList.add(CLASS_HIDDEN);
    const pending = [{ ...RELOAD }];

    // A load makes the saved map pending just after the pass's last read.
    const server = serverWith(pending, {
        afterRead: reads => { if (reads === 2) { pending.push({ ...MAP }); } }
    });

    const pass = askPendingQuestions();
    await until(() => questionShown(dom, RELOAD.question), 'the first question');
    askPendingQuestions();
    await dom.el('confirm-yes-btn').fire('click');
    await pass;

    await until(() => questionShown(dom, MAP.question), 'the question the trigger made pending');
    await until(() => !dom.el('confirm-yes-btn').disabled, 'the question to settle');
    await dom.el('confirm-yes-btn').fire('click');
    await until(() => server.posted.length === 2, 'the second answer');
});

test('a replaced question sends no answer', async () => {
    const dom = installDom();
    dom.el('confirm-modal').classList.add(CLASS_HIDDEN);
    const server = serverWith([{ ...MAP }]);

    const pass = askPendingQuestions();
    await until(() => questionShown(dom, MAP.question), 'the question');

    const other = showConfirm('Disconnect the other client?');
    await pass;
    await dom.el('confirm-no-btn').fire('click');
    await other;

    assert.deepEqual(server.posted, [],
        'a question nobody answered was sent as an answer');
});

test('a pass does not replace a question already open', async () => {
    const dom = installDom();
    dom.el('confirm-modal').classList.add(CLASS_HIDDEN);
    const server = serverWith([{ ...MAP }]);

    const abort = showConfirm('Abort milling?');
    await askPendingQuestions();

    assert.equal(dom.el('confirm-message').textContent, 'Abort milling?',
        'a session question replaced the question the operator was answering');
    assert.deepEqual(server.posted, []);

    await dom.el('confirm-yes-btn').fire('click');
    assert.equal(await abort, true);
});

test('loading a file asks what the load made pending', async () => {
    const dom = installDom();
    dom.el('confirm-modal').classList.add(CLASS_HIDDEN);
    const server = serverWith([{ ...MAP }]);

    const file = await load('file.js');
    state.selectedFile = '/boards/b.ngc';
    await file.loadFile();

    await until(() => questionShown(dom, MAP.question), 'the saved height map question');
    await dom.el('confirm-yes-btn').fire('click');
    await until(() => server.posted.length === 1, 'the answer');

    assert.deepEqual(server.posted, [{ topic: MAP.topic, detail: MAP.detail, yes: true }]);
});
