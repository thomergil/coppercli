// coppercli Web UI - Main Entry Point

// First import: installs the access-token wrapper around fetch before any other
// module is evaluated, so nothing can issue an unauthenticated request.
import './auth.js';

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

// Initialize on load
document.addEventListener('DOMContentLoaded', init);

async function init() {
    // Load configuration from server (jog modes, etc.)
    await loadConfig();

    // Validate duplicated constants match server (dev aid - logs warnings on mismatch)
    validateConstants();

    // Wire up all buttons with data-screen attribute for navigation
    document.querySelectorAll('[data-screen]').forEach(btn => {
        btn.addEventListener('click', () => {
            const screenName = btn.dataset.screen;
            showScreen(screenName + SCREEN_SUFFIX);
        });
    });

    // Mill button (special - starts milling, not just navigation)
    $('mill-btn').addEventListener('click', startMill);

    // Initialize screen-specific handlers
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

    // Connect WebSocket
    connectWebSocket();

    // Restore screen from URL hash (for page reload)
    restoreScreenFromHash();

    // Check for stored work zero and offer to trust it
    await checkAndShowTrustZero();

    // Check for unsaved probe data and show save modal if needed
    await checkAndShowUnsavedProbe();
}

