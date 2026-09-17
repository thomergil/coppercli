export const state = {
    ws: null,
    jogModes: [],      // Loaded from server config
    jogModeIndex: 0,   // Set by loadConfig before the jog screen opens
    connected: false,
    hasFile: false,

    // What the server last reported about the two runs. Only updateStatus writes these, and
    // the screen lock derives from them.
    isMilling: false,
    isProbing: false,

    // True while a tool change waits for the operator to set Z0; the lock lets them jog.
    awaitingZeroZ: false,

    // True while the machine is tracing a probe grid's outline. It measures nothing, so the
    // probing progress display stays hidden.
    tracingOutline: false,

    selectedFile: null,
    currentScreen: null,
    reconnectAttempts: 0,
    isProbePollRunning: false,
    probeDataDisplayed: false,
    probeDefaults: { margin: 0.5, gridSize: 5 },  // Fallbacks, overwritten by loadConfig
    millGrid: { maxWidth: 50, maxHeight: 20 }     // Fallbacks, overwritten by loadConfig
};
