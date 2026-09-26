// Runs the real browser modules against the stub page and checks what they write to it. The
// C# tests cover the payload's values and check-layering.sh covers what the browser must not
// read; neither covers this.
//
// Activity names and operator text are written out here rather than imported:
// HEADER_TEXT_BY_ACTIVITY is built from those same constants, so importing them would make
// each assertion repeat the table instead of checking it.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { installDom, load, payload, BUTTONS } from './dom-stub.mjs';

// For a door hold with no run behind it, where the operator presses the release.
const BUTTONS_DOOR_RELEASABLE = { ...BUTTONS, doorRelease: { enabled: true } };

// Node loads each module once per file, and mill.js keeps the prompt on screen in module
// state. endMillRun clears it, so the order of the tests does not matter.
async function newPage() {
    const dom = installDom();
    (await load('mill.js')).endMillRun();
    return dom;
}

async function render(overrides) {
    const dom = await newPage();
    const screens = await load('screens.js');
    screens.updateStatus(payload(overrides));
    return dom;
}

// A control that starts disabled on a fresh page cannot be told from one the code never
// wrote, so a test that checks re-enabling draws a sequence of statuses onto one page.
async function renderInTurn(...states) {
    const dom = await newPage();
    const screens = await load('screens.js');
    for (const state of states) {
        screens.updateStatus(payload(state));
    }
    return dom;
}

test('a machine that recovers re-enables the controls it disabled', async () => {
    const dom = await renderInTurn(
        { machineActivity: 'DoorOpen', status: 'Door:1', needsAttention: true,
          machineUnavailable: true },
        {});

    assert.equal(dom.el('jog-home-btn').disabled, false, 'jog-home stayed dead after recovery');
    assert.equal(dom.jogButtons[0].disabled, false, 'jog buttons stayed dead after recovery');
    assert.equal(dom.modeButtons[0].disabled, false, 'mode buttons stayed dead after recovery');
    assert.equal(dom.el('status-indicator').classList.contains('alarm'), false);
});

test('the pause control comes back when the machine starts cutting', async () => {
    const dom = await renderInTurn(
        {},
        { machineActivity: 'Running', status: 'Run', canPause: true });

    assert.equal(dom.el('jog-pause-btn').disabled, false, 'pause stayed dead while cutting');
});

test('the depth readout follows the status stream', async () => {
    const dom = await render({ depthAdjustment: -0.12 });

    assert.equal(dom.el('premill-depth-value').textContent, '-0.12');
});

test('a machine that needs attention disables the controls that send commands', async () => {
    let dom = await render({ machineActivity: 'DoorOpen', status: 'Door:1', needsAttention: true,
                             machineUnavailable: true });
    assert.equal(dom.el('jog-home-btn').disabled, true);
    assert.equal(dom.jogButtons[0].disabled, true);
    assert.equal(dom.modeButtons[0].disabled, true);
    assert.equal(dom.el('status-indicator').classList.contains('alarm'), true);

    dom = await render({});
    assert.equal(dom.el('jog-home-btn').disabled, false);
    assert.equal(dom.jogButtons[0].disabled, false);
    assert.equal(dom.modeButtons[0].disabled, false);
    assert.equal(dom.el('status-indicator').classList.contains('connected'), true);
});

test('a machine that dropped off the link takes no jog either', async () => {
    // needsAttention covers a connected machine that needs something done to it. A machine
    // that stopped responding needs the same controls disabled, and only machineUnavailable
    // covers both.
    const dom = await render({
        connected: false, machineActivity: 'Disconnected', status: 'Disconnected',
        needsAttention: false, machineUnavailable: true
    });

    assert.equal(dom.el('jog-home-btn').disabled, true, 'home was live with no machine there');
    assert.equal(dom.jogButtons[0].disabled, true, 'the jog pad was live with no machine there');
});

test('the pause control follows the two answers the server sends, not the status word', async () => {
    let dom = await render({ machineActivity: 'Running', status: 'Run', canPause: true });
    assert.equal(dom.el('jog-pause-btn').disabled, false);

    dom = await render({ machineActivity: 'Hold', status: 'Hold:0', canResume: true });
    assert.equal(dom.el('jog-pause-btn').disabled, false);

    dom = await render({});
    assert.equal(dom.el('jog-pause-btn').disabled, true);

    // status reads Run while canPause is false: a screen branching on the status word would
    // enable the control here.
    dom = await render({ machineActivity: 'Idle', status: 'Run' });
    assert.equal(dom.el('jog-pause-btn').disabled, true);
});

test('the header gives the door its own wording, and shows GRBL\'s word otherwise', async () => {
    const worded = {
        DoorOpen: 'Door open',
        DoorRetracting: 'Door - machine retracting',
        DoorHolding: 'Door closed - machine holding',
        DoorResuming: 'Door closed - machine resuming'
    };
    for (const [machineActivity, text] of Object.entries(worded)) {
        const dom = await render({ machineActivity, status: 'Door', needsAttention: true });
        assert.equal(dom.el('status-text').textContent, text, machineActivity);
    }

    for (const [machineActivity, status] of [['Alarm', 'Alarm:1'], ['Other', 'Jog'], ['Idle', 'Idle']]) {
        const dom = await render({ machineActivity, status });
        assert.equal(dom.el('status-text').textContent, status, machineActivity);
    }
});

test('a machine with no link reads Disconnected, whatever it last reported', async () => {
    const dom = await render({ connected: false, machineActivity: 'Disconnected', status: 'Alarm:1' });

    assert.equal(dom.el('status-text').textContent, 'Disconnected');
    assert.equal(dom.el('status-indicator').classList.contains('connected'), false);
    assert.equal(dom.el('status-indicator').classList.contains('alarm'), false);
});

test('a blocked button is drawn with its reason, and the rest of the screen still updates', async () => {
    // updateButtonState runs before updateJogButtons, so a fault in this branch stops the
    // jog buttons being disabled, and websocket.js reports it as a parse failure.
    const dom = await render({
        machineActivity: 'DoorOpen', status: 'Door:1', needsAttention: true,
        machineUnavailable: true,
        buttons: {
            jog: { enabled: false, reason: 'Connect first' },
            probe: { enabled: false, reason: 'No file loaded' },
            mill: { enabled: true }
        }
    });

    assert.equal(dom.el('jog-btn').disabled, true);
    assert.equal(dom.el('jog-btn').title, 'Connect first');
    assert.equal(dom.el('jog-btn').classList.contains('disabled'), true);
    assert.equal(dom.el('jog-btn').querySelector('.disabled-reason').textContent, ' (Connect first)');
    assert.equal(dom.el('mill-btn').disabled, false);

    assert.equal(dom.el('jog-home-btn').disabled, true);
    assert.equal(dom.jogButtons[0].disabled, true);
});

test('a recovered prompt is drawn with the words the workflow chose', async () => {
    // Drawn under a fixed tool-change heading, a door prompt reads as a prompt about the
    // tool, and answering it restarts the spindle.
    const dom = await render({
        toolChange: {
            phase: 'WaitingForOperator',
            id: 'door-1',
            title: 'Enclosure Door',
            message: 'The door is closed and the machine is holding.',
            options: ['Continue']
        }
    });

    assert.equal(dom.el('toolchange-info').textContent, 'Enclosure Door');
    assert.equal(dom.el('toolchange-message').textContent,
        'The door is closed and the machine is holding.');
});

test('the door question is drawn when no run is asking it, and taken down when it clears', async () => {
    const dom = await renderInTurn(
        { machineActivity: 'DoorHolding', status: 'Door:0', needsAttention: true,
          canReleaseDoor: true, doorMessage: 'Door closed. Continue?',
          buttons: BUTTONS_DOOR_RELEASABLE });

    // The door overlay sits outside the screens, because an open door stops jogging and
    // probing too.
    assert.equal(dom.el('door-overlay').classList.contains('hidden'), false);
    assert.equal(dom.el('door-message').textContent, 'Door closed. Continue?');
    assert.equal(dom.el('door-continue-btn').style.display, '');

    // No run prompt here, so there is no Abort option to draw.
    assert.equal(dom.el('door-abort-btn').style.display, 'none');

    const cleared = await renderInTurn(
        { machineActivity: 'DoorHolding', status: 'Door:0', needsAttention: true,
          canReleaseDoor: true, doorMessage: 'Door closed. Continue?',
          buttons: BUTTONS_DOOR_RELEASABLE },
        {});
    assert.equal(cleared.el('door-overlay').classList.contains('hidden'), true);
});

test('a tool change answered at a closed door does not put the door question back', async () => {
    // Answering the tool-change prompt makes the run release the hold, so a door question
    // drawn here offers a release the server refuses.
    const dom = await newPage();
    const screens = await load('screens.js');

    const atTheDoor = {
        milling: true,
        machineActivity: 'DoorHolding', status: 'Door:0', needsAttention: true,
        canReleaseDoor: true, doorMessage: 'Door closed. Continue?'
    };

    screens.updateStatus(payload({
        ...atTheDoor,
        toolChange: {
            id: 'p1', title: 'Tool change', message: 'Fit tool 2',
            options: ['Continue', 'Abort'], isDoorPrompt: false
        }
    }));

    assert.equal(dom.el('door-overlay').classList.contains('hidden'), true,
        'the door overlay covered the tool-change question');

    // The prompt is answered and gone, and the run is releasing the hold itself.
    screens.updateStatus(payload(atTheDoor));

    assert.equal(dom.el('door-continue-btn').style.display, 'none',
        'the door question came back with a button after it was answered');
});

test('an open door has nothing to press, because only a closed one can be released', async () => {
    const dom = await render({
        machineActivity: 'DoorOpen', status: 'Door:1', needsAttention: true,
        canReleaseDoor: false, doorMessage: 'Close the door.'
    });

    assert.equal(dom.el('door-overlay').classList.contains('hidden'), false);
    assert.equal(dom.el('door-message').textContent, 'Close the door.');
    assert.equal(dom.el('door-continue-btn').style.display, 'none');
});

test('the door text is the server\'s, so the browser keeps no mapping of its own', async () => {
    // Core maps each door state to its sentence. A second copy here would show the old
    // wording as soon as Core's changed.
    const dom = await render({
        machineActivity: 'DoorResuming', status: 'Door:3', needsAttention: true,
        canReleaseDoor: false, doorMessage: 'Resuming...'
    });

    assert.equal(dom.el('door-message').textContent, 'Resuming...');
});

test('a freshly drawn door prompt cannot be answered by the tap that answered the last', async () => {
    // Answering one prompt can publish the next at once, and the door overlay draws it in the
    // same place. Without the settle guard the second tap answers a question the operator has
    // not read, and for the enclosure that restarts the spindle.
    const dom = await newPage();
    const screens = await load('screens.js');

    screens.updateStatus(payload({
        machineActivity: 'DoorHolding', status: 'Door:0', needsAttention: true,
        canReleaseDoor: true, doorMessage: 'Door closed. Continue?',
        toolChange: {
            phase: 'WaitingForOperator', id: 'settle-1', isDoorPrompt: true,
            title: '', message: 'Door closed. Continue?',
            options: ['Continue', 'Abort']
        }
    }));

    assert.equal(dom.el('door-continue-btn').disabled, true,
        'a newly drawn enclosure prompt was answerable at once');
    assert.equal(dom.el('door-abort-btn').disabled, true);
});

test('a door prompt redrawn on the next status tick is still settling', async () => {
    // The overlay is redrawn on every status broadcast, which is more often than the settle
    // lasts. A redraw that re-enabled the buttons would cut the settle to one tick.
    const dom = await newPage();
    const screens = await load('screens.js');

    const holding = payload({
        machineActivity: 'DoorHolding', status: 'Door:0', needsAttention: true,
        canReleaseDoor: true, doorMessage: 'Door closed. Continue?',
        toolChange: {
            phase: 'WaitingForOperator', id: 'settle-2', isDoorPrompt: true,
            title: '', message: 'Door closed. Continue?',
            options: ['Continue', 'Abort']
        }
    });

    screens.updateStatus(holding);
    screens.updateStatus(holding);

    assert.equal(dom.el('door-continue-btn').disabled, true,
        'a redraw re-enabled the button, so the settle is as short as one status tick');
});

test('a door question with no run behind it is answerable at once', async () => {
    // No run raised this question, so no tap can belong to an earlier prompt.
    const dom = await render({
        machineActivity: 'DoorHolding', status: 'Door:0', needsAttention: true,
        canReleaseDoor: true, doorMessage: 'Door closed. Continue?'
    });

    assert.equal(dom.el('door-continue-btn').disabled, false);
});

test('a run that offers Abort gets a way off the door overlay', async () => {
    // The overlay covers the whole page. With only Continue on it, an operator who wants to
    // stop the job can reach no control that does.
    const dom = await render({
        machineActivity: 'DoorHolding', status: 'Door:0', needsAttention: true,
        canReleaseDoor: true, doorMessage: 'Door closed. Continue?',
        toolChange: {
            phase: 'WaitingForOperator', id: 'p1', isDoorPrompt: true,
            title: '', message: 'Door closed. Continue?', options: ['Continue', 'Abort']
        }
    });

    assert.equal(dom.el('door-abort-btn').style.display, '');
});

test('a run\'s prompt about anything else keeps the door overlay down', async () => {
    const dom = await render({
        machineActivity: 'DoorHolding', status: 'Door:0', needsAttention: true,
        toolChange: {
            phase: 'WaitingForOperator', id: 'p1', isDoorPrompt: false,
            title: 'Tool Change', message: 'The run is asking.', options: ['Continue']
        }
    });

    assert.equal(dom.el('toolchange-message').textContent, 'The run is asking.');

    assert.equal(dom.el('door-overlay').classList.contains('hidden'), true);
});

test('a run\'s enclosure prompt is drawn in the page-level overlay', async () => {
    // The tool-change overlay is inside the mill screen, so a prompt drawn there is not
    // visible while the operator is on the probe or jog screen.
    const dom = await render({
        machineActivity: 'DoorHolding', status: 'Door:0', needsAttention: true,
        canReleaseDoor: true,
        toolChange: {
            phase: 'WaitingForOperator', id: 'p1', isDoorPrompt: true,
            title: '', message: 'Door closed. Continue?', options: ['Continue', 'Abort']
        }
    });

    assert.equal(dom.el('door-overlay').classList.contains('hidden'), false);
    assert.equal(dom.el('door-message').textContent, 'Door closed. Continue?');
    assert.equal(dom.el('door-continue-btn').style.display, '');
    assert.equal(dom.el('toolchange-message').textContent, '');
});

test('a status message with no answers in it disables the controls rather than enabling them', async () => {
    const dom = installDom();
    const jog = await load('jog.js');

    jog.updateJogButtons(undefined);

    assert.equal(dom.el('jog-home-btn').disabled, true);
    assert.equal(dom.jogButtons[0].disabled, true);
    assert.equal(dom.modeButtons[0].disabled, true);
    assert.equal(dom.el('jog-pause-btn').disabled, true);
});
