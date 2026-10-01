// A modal that serves one caller at a time answers each caller once. Asking again answers the earlier
// caller with the "replaced" value, so no caller waits forever, and `waiting` says whether an
// answer is still owed. The confirm dialog, the pre-mill window and the sections picker all
// rely on this.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { installDom, load } from './dom-stub.mjs';

const REPLACED = 'replaced';
const GIVEN = 'given';

test('asking again answers the earlier caller with the replaced value', async () => {
    installDom();
    const { singleModalAnswer } = await load('helpers.js');
    const answer = singleModalAnswer(REPLACED);

    const first = answer.ask();
    const second = answer.ask();
    answer.give(GIVEN);

    assert.equal(await first, REPLACED, 'the caller whose modal was replaced was left waiting or got the new answer');
    assert.equal(await second, GIVEN, 'the caller on screen did not get the answer given');
});

test('waiting is true from ask until the answer is given', async () => {
    installDom();
    const { singleModalAnswer } = await load('helpers.js');
    const answer = singleModalAnswer(REPLACED);

    assert.equal(answer.waiting, false, 'a modal nobody asked is waiting for an answer');
    const asked = answer.ask();
    assert.equal(answer.waiting, true, 'an open modal says it owes no answer');
    answer.give(GIVEN);
    assert.equal(answer.waiting, false, 'an answered modal still says it owes an answer');
    assert.equal(await asked, GIVEN);
});
