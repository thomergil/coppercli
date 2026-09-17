// Every outcome the server can send has words for the operator. A name with no entry
// leaves the operator with the bare "axes zeroed" line and nothing about the height map.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { load } from './dom-stub.mjs';

const constants = await load('constants.js');

// Read from the module, not listed here: a list would need editing alongside every new
// outcome, which is the omission this test exists to catch. TEXT_ZEROED_* are the words for
// the axes and start with a different prefix.
const outcomes = Object.entries(constants).filter(([name]) => name.startsWith('ZEROED_'));

test('every height map outcome has words', () => {
    assert.ok(outcomes.length > 0, 'no outcome names found, so this test checks nothing');

    for (const [name, outcome] of outcomes) {
        const words = constants.HEIGHT_MAP_TEXT_BY_OUTCOME[outcome];
        assert.ok(words && words.length > 0,
            `${name} (${outcome}) has no words, so the operator is told the axes were `
            + 'zeroed and nothing about the height map');
    }
});
