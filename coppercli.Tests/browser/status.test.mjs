// These run the real browser modules against a stub page and check what they write to it.
// The C# tests cover the payload's values and check-layering.sh covers what the browser must
// not read; neither covers this.
//
// Activity names and operator text are written out here rather than imported.
// HEADER_TEXT_BY_ACTIVITY is built from those same constants, so importing them would make
// each assertion repeat the table instead of checking it.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { installDom, load } from './dom-stub.mjs';

const BUTTONS = {
    jog: { enabled: true },
    probe: { enabled: true },
    mill: { enabled: true },
    // Disabled by default: with no door hold there is nothing to release.
    doorRelease: { enabled: false }
};

// The same buttons with the door release offered, for a hold no run is handling.
const BUTTONS_DOOR_RELEASABLE = { ...BUTTONS, doorRelease: { enabled: true } };

// A status message as the server sends it, with per-test overrides on top.
function payload(overrides) {
    return {
        connected: true,
        status: 'Idle',
        machineActivity: 'Idle',
        needsAttention: false,
        canPause: false,
        canResume: false,
        machineUnavailable: false,
        canReleaseDoor: false,
        doorMessage: null,
        buttons: BUTTONS,
        // Present by default so updateStatus runs every branch: a branch it skips is part
        // of the page no test writes to.
        workPos: { x: 0, y: 0, z: 0 },
        machinePos: { x: 0, y: 0, z: 0 },
        feedOverride: 100,
        probePin: false,
        depthAdjustment: 0,
        file: { currentLine: 0, totalLines: 0 },
        probe: { state: 'none', total: 0, progress: 0 },
        ...overrides
    };
}

// Node keeps one copy of the modules for the whole file, and they remember which prompt is
// on screen. Ending the run clears that in the browser, so each test starts by ending one
// and the order of the tests does not matter.
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

// Draws a sequence of statuses onto one page, as a browser receives them. On a fresh page
// a control that starts disabled cannot be told from one the code never wrote.
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
    // needsAttention is about a machine that is there and needs something done to it. A
    // machine that stopped answering needs the same controls disabled, and only
    // machineUnavailable covers both.
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

    // Idle: neither applies, so the shared control is disabled.
    dom = await render({});
    assert.equal(dom.el('jog-pause-btn').disabled, true);

    // The raw status word is not used here. A screen reading it would be the defect.
    dom = await render({ machineActivity: 'Idle', status: 'Run' });
    assert.equal(dom.el('jog-pause-btn').disabled, true);
});

test('the header gives the door its own wording, and shows GRBL\'s word otherwise', async () => {
    const worded = {
        DoorOpen: 'Door open',
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

    // Everything after updateButtonState in updateStatus still ran.
    assert.equal(dom.el('jog-home-btn').disabled, true);
    assert.equal(dom.jogButtons[0].disabled, true);
});

test('a recovered prompt is drawn with the words the workflow chose', async () => {
    // Under the tool-change heading, a door prompt reads as a prompt about the tool, and
    // answering it restarts the spindle.
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
    // The browser's equivalent of the terminal jog screen holding at the door: with no run
    // to prompt, the overlay offers the release itself.
    const dom = await renderInTurn(
        { machineActivity: 'DoorHolding', status: 'Door:0', needsAttention: true,
          canReleaseDoor: true, doorMessage: 'Door closed. Continue?',
          buttons: BUTTONS_DOOR_RELEASABLE });

    // Its own element, outside the screens: an open door stops jogging and probing too.
    assert.equal(dom.el('door-overlay').classList.contains('hidden'), false);
    assert.equal(dom.el('door-message').textContent, 'Door closed. Continue?');
    assert.equal(dom.el('door-continue-btn').style.display, '');

    // Nothing to answer once the run offered no Abort and the door cleared.
    assert.equal(dom.el('door-abort-btn').style.display, 'none');

    const cleared = await renderInTurn(
        { machineActivity: 'DoorHolding', status: 'Door:0', needsAttention: true,
          canReleaseDoor: true, doorMessage: 'Door closed. Continue?',
          buttons: BUTTONS_DOOR_RELEASABLE },
        {});
    assert.equal(cleared.el('door-overlay').classList.contains('hidden'), true);
});

test('a tool change answered at a closed door does not put the door question back', async () => {
    // The run releases the hold on the Continue just given, so the browser must not offer a
    // second one over the top of it - the server would refuse that release anyway.
    const dom = await newPage();
    const screens = await load('screens.js');

    const atTheDoor = {
        milling: true,
        machineActivity: 'DoorHolding', status: 'Door:0', needsAttention: true,
        canReleaseDoor: true, doorMessage: 'Door closed. Continue?'
    };

    // Parked on the tool-change question, with the door already closed.
    screens.updateStatus(payload({
        ...atTheDoor,
        toolChange: {
            id: 'p1', title: 'Tool change', message: 'Fit tool 2',
            options: ['Continue', 'Abort'], isDoorPrompt: false
        }
    }));

    assert.equal(dom.el('door-overlay').classList.contains('hidden'), true,
        'the door overlay covered the tool-change question');

    // Answered. The prompt is gone and the run is releasing the hold itself, so the server
    // refuses a release from here.
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
    // Which sentence goes with which door state is decided in Core. A second copy here
    // would show the wrong one the moment Core's wording changed.
    const dom = await render({
        machineActivity: 'DoorResuming', status: 'Door:3', needsAttention: true,
        canReleaseDoor: false, doorMessage: 'Resuming...'
    });

    assert.equal(dom.el('door-message').textContent, 'Resuming...');
});

test('a freshly drawn door prompt cannot be answered by the tap that answered the last', async () => {
    // Answering one prompt can raise the next immediately, and the door overlay draws it in
    // the same place. Without the settle guard the second tap answers a question the
    // operator has not read - and for the enclosure that restarts the spindle.
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
    // Nothing raised it, so no tap can belong to an earlier prompt.
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

    // Only one thing is shown, and the run chose which.
    assert.equal(dom.el('door-overlay').classList.contains('hidden'), true);
});

test('a run\'s enclosure prompt is drawn in the page-level overlay', async () => {
    // The tool-change overlay lives inside the mill screen, so a prompt drawn there is not
    // on the page at all while the operator is on the probe or jog screen.
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
