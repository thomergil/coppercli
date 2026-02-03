// coppercli Web UI State

// Global state object - exported for access from other modules
export const state = {
    ws: null,
    jogModes: [],      // Loaded from server config
    jogModeIndex: 0,   // Current mode (0=Fast, 1=Normal, etc.)
    connected: false,
    hasFile: false,    // Whether a G-code file is loaded
    selectedFile: null,
    currentFilePath: null,
    currentScreen: null, // Current screen ID (for redirect logic)
    isMilling: false,
    isProbing: false,
    lockedScreen: null,  // Screen ID when locked (probing/milling active)
    userStoppedMilling: false,  // Prevents re-locking after user presses STOP
    reconnectAttempts: 0,
    isProbePollRunning: false,
    probeDataDisplayed: false,  // Prevent repeated probe data display
    // Server-provided config (loaded at startup to avoid duplicating constants)
    probeDefaults: { margin: 0.5, gridSize: 5 },  // Fallbacks, overwritten by loadConfig
    millGrid: { maxWidth: 50, maxHeight: 20 }     // Fallbacks, overwritten by loadConfig
};
