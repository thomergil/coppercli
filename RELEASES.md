# Release Notes

## v0.2.2

### New Features

- **Proxy [experimental]**: New menu option to act as a serial-to-TCP bridge, allowing remote GRBL clients to connect over the network. Displays local IP addresses for easy client connection.
- **Command-line arguments**:
  - `--proxy` or `-p`: Start directly in proxy mode using saved serial settings
  - `--port <number>`: Override the default TCP port (34000) for proxy mode
  - `--headless` or `-H`: Run proxy without TUI (for services/scripts)
  - `--debug` or `-d`: Enable debug logging
- **Auto-reconnect remembers connection type**: Last successful connection type (Serial or Ethernet) is now remembered and used for auto-reconnect on startup.

### Bug Fixes

- **File browser crash with special characters**: Fixed crash when browsing directories/files containing `[` or `]` characters (Spectre.Console markup escape issue).
- **Windows installer terminal behavior**: Terminal window now closes when exiting the program instead of leaving a cmd prompt open.
- **Session restore respects rejection**: When declining to reload a G-code file on startup, it's now cleared from session so it doesn't keep asking. Last browse directory is preserved.

## v0.2.1

### New Features

- **File browser timestamps**: File browser now shows modification timestamps for each file.
- **T commands ignored**: G-code T (tool change) commands are now silently ignored instead of generating warnings.
- **Logging infrastructure**: New Logger class writes to `coppercli.log` for debugging. Off by default; enable via Settings > Toggle Debug Logging. During milling, logs TX/RX, state changes, and mode changes.
- **Overlay on map**: Hold/Alarm/Settling overlay now drawn on top of the position map instead of replacing it.
- **X=Stop in overlay**: Overlay box now shows X=Stop option.
- **Probe status visibility**: Main menu now shows whether probe points have been applied (green "applied" / yellow "not applied").

### Bug Fixes

- **Full-circle arc fix**: Always output X/Y coordinates for arc commands, fixing GRBL error:33 on helical full-circle arcs (milldrilling).
- **Resume fix**: Improved resume logic to properly distinguish between Hold state (needs CycleStart) and Manual mode after M0 (needs FileStart).
- **Filename preserved**: Fixed GCodeFile methods (Split, ArcsToLines, ApplyProbeGrid, RotateCW) to preserve filename when creating transformed copies.
