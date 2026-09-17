// Entry point. loadConfig is awaited before any screen is initialized, because the jog
// screen reads the modes it fetches.

import { $, validateConstants } from './helpers.js';
import { showScreen, restoreScreenFromHash, initHeader } from './screens.js';
import { connectWebSocket } from './websocket.js';
import { loadConfig, initJogScreen } from './jog.js';
import { initFileScreen } from './file.js';
import { initMillScreen, startMill } from './mill.js';
import { initProbeScreen, initProbeFilesScreen, initProbeSaveModal, initProbeRecoveryModal, checkAndShowUnsavedProbe } from './probe.js';
import { initSettingsScreen } from './settings.js';
import { initTrustZeroModal, checkAndShowTrustZero } from './trust-zero.js';
import { SCREEN_SUFFIX } from './constants.js';

document.addEventListener('DOMContentLoaded', init);

async function init() {
    await loadConfig();

    validateConstants();

    document.querySelectorAll('[data-screen]').forEach(btn => {
        btn.addEventListener('click', () => {
            const screenName = btn.dataset.screen;
            showScreen(screenName + SCREEN_SUFFIX);
        });
    });

    // mill-btn starts the run rather than navigating, so it is wired apart from the loop above.
    $('mill-btn').addEventListener('click', startMill);

    initHeader();
    initJogScreen();
    initFileScreen();
    initMillScreen();
    initProbeScreen();
    initProbeFilesScreen();
    initProbeSaveModal();
    initProbeRecoveryModal();
    initTrustZeroModal();
    initSettingsScreen();

    connectWebSocket();

    restoreScreenFromHash();

    await checkAndShowTrustZero();

    await checkAndShowUnsavedProbe();
}

