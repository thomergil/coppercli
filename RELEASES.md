# Release Notes

## v0.3.0

### New Features

- **Macros**: New macro system for automating repetitive workflows. Create `.cmacro` files with G-code, prompts, and comments. Access via main menu or run directly with `--macro` / `-m` command-line flag.
- **File browser filter**: Press `/` to filter files by name. Type to narrow results, Backspace to edit, Esc to clear.

### Changes

- **Clearer settling message**: During milling startup, the settling overlay now shows "Waiting for idle." when the machine is still moving, instead of a static countdown that never progresses.

## v0.2.3

### New Features

- **Network auto-detect**: Network (TCP/IP) connection menu now includes auto-detect option that scans the local network for devices. Configurable port (default 34000) and subnet mask (/16 to /24, default /24).
- **Probe color legend**: Probing display now shows a live color legend indicating Z values for the low (blue), mid (green), and high (red) colors.

### Changes

- Renamed "Traverse Outline" to "Trace Outline" for clarity.
- Renamed "Ethernet" to "Network (TCP/IP)" in connection menu.
- **Flicker-free jog menu**: Jog menu now uses in-place redraw instead of clearing the screen, eliminating flicker especially over network connections.
- **Simplified probe in jog menu**: The P (probe) command no longer prints verbose status messages; just watch the Z position update.
- **Consistent confirmation prompts**: All y/n prompts now respond immediately on keypress without requiring Enter.
- **Consistent input prompts**: All open-ended prompts now show `>` prefix via `MenuHelpers.Ask` wrapper.
- **File browser shortcuts**: Limited to 36 items (1-9, 0, A-Z). Items beyond use arrow navigation only - no more weird characters.

### Bug Fixes

- **File browser crash in small terminals**: Fixed crash when file browser had more items than fit in the terminal window. Menu now scrolls gracefully with "more above/below" indicators, and supports PageUp/PageDown/Home/End for faster navigation.
- **Auto-clear alarm on connect**: Alarm state is now silently cleared when connecting, before offering to home. Door state still prompts user to close the door.
- **Proxy safety on disconnect**: Proxy now sends feed hold (`!`) when a client disconnects, stopping any in-progress movement.
- **Menu auto-selection bug**: Fixed bug where status changes during menu display could auto-select the first menu option (e.g., auto-triggering probing).
- **Door open on boot**: Fixed error messages when connecting with door open at power-on. GRBL may boot into Alarm state (not Door state) in this scenario; the homing flow now handles both states gracefully by prompting the user to close the door before attempting to unlock.
- **Double brackets in menus**: Fixed `[[experimental]]` displaying literally instead of `[experimental]` in menu items.
- **Connection errors suppressed**: Transient "Error while Parsing Status Message" during initial connection is now suppressed.

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
