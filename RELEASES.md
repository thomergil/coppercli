# Release Notes

## v0.3.1

### Changes

- Renamed "Network (TCP/IP)" to "Network" in connection menu.

## v0.3.0

### Safety Improvements

- **Emergency stop (X key) now uses machine coordinates**: Previously sent `G0 Z6` in work coordinates, which could plunge the tool if work zero was set incorrectly. Now sends `G53 G0 Z-1` to retract to near top of machine travel regardless of work coordinate offset.
- **Defense in depth for coordinate systems**: All manual moves (jog presets, probe moves, tool change) now explicitly send G90 (absolute mode) before executing. Prevents dangerous behavior if G-code left machine in G91 (incremental) mode.
- **State initialization before milling**: Sends G90 G17 (absolute mode, XY plane) before starting any G-code file to establish known machine state.
- **Dangerous G-code detection**: Parser now warns about G28/G30 (home commands that may crash into workpiece) and G20 (imperial units that may cause coordinate confusion).
- **Pre-mill safety check**: If loaded file contains dangerous commands or uses imperial units, displays warnings and asks for confirmation before running. Defaults to NO.
- **Tool change uses machine coordinates**: All safety retracts during M6 tool change use G53 (machine coordinates) for predictable behavior.
- **Homing required before milling**: If machine hasn't been homed, milling will automatically home first. Without homing, machine coordinates are undefined and safety retracts could move in the wrong direction.

### New Features

- **Tool change support (M6)**: Automatic tool change handling during milling. When the G-code contains M6, coppercli pauses, guides you through the tool change, and automatically compensates for the new tool length.
  - **With tool setter**: If your machine has a tool setter (probe button), coppercli measures both tools and calculates the Z offset automatically. No need to re-zero.
  - **Without tool setter**: Prompts you to probe the PCB surface with the new tool to re-establish Z zero.
  - **M0 after M6 skipped**: If M0 (program pause) immediately follows M6 (as pcb2gcode generates), the redundant M0 is skipped. This allows coppercli to work with pcb2gcode's native tool change format without requiring `nom6=1`.
- **Machine profiles**: Select your CNC machine in Settings to auto-configure tool setter position. Built-in profiles for Carbide 3D (Nomad 3, Shapeoko), Sienci (LongMill), OpenBuilds (LEAD, MiniMill), SainSmart/Genmitsu, Inventables (X-Carve), and generic 3018/6040 machines. Add custom machines in `machine-profiles.yaml`.
- **Machine profile warning**: Main menu displays selected machine profile. If no profile is selected, shows warning in red. Before milling, displays confirmation overlay if no profile is configured.
- **Sleep prevention**: Prevents system idle sleep during milling and probing. Uses `SetThreadExecutionState` on Windows, `caffeinate` on macOS, and `systemd-inhibit` on Linux. In network mode, warns if sleep prevention is unavailable since system sleep could disconnect and leave machine in unknown state.
- **Tool setter setup**: Settings menu includes interactive jog-based setup to configure or override tool setter position.
- **Macros**: New macro system for automating repetitive workflows. Create `.cmacro` files with G-code, prompts, and comments. Access via main menu or run directly with `--macro` / `-m` command-line flag.
- **Macro placeholders**: Use `[name:file]` syntax for files that vary between runs. Prompts file browser at runtime, or pass via CLI with `--name path`.
- **File browser filter**: Press `/` to filter files by name. Type to narrow results, Backspace to edit, Esc to clear.

### Changes

- **Tool setter Y coordinate now optional**: For machines with moving beds (like Nomad 3), only the X coordinate is needed to reach the tool setter. Y can be omitted in `machine-profiles.yaml` to avoid unnecessary bed movement.
- **Feed override during milling**: Press `+` to increase feed rate 10%, `-` to decrease 10%, `0` to reset to 100%. Shows current override in status line when not 100%.
- **Vim-style jog multiplier**: Press a digit (1-5 in Fast mode, 1-9 in other modes) before a jog direction to multiply the distance. For example, in Normal mode (1mm), pressing `3→` jogs 3mm right.
- **Jog menu shows machine position**: Now displays both work and machine coordinates.
- **Jog menu key changes**: Some keys changed to support vim-style multipliers and HJKL navigation:
  - `H` → `M` for Home (H is now vim-style left)
  - `1` → `B` for go to Z+1mm
  - `6` → `T` for go to Z+6mm (retract height)
  - `X` → `N` for go to X0 Y0 (origin)
  - Added `HJKL` for vim-style X/Y jogging
- **Clearer settling message**: During milling startup, the settling overlay now shows "Waiting for idle." when the machine is still moving, instead of a static countdown that never progresses.
- **Faster settling**: Reduced post-idle settle time from 10s to 5s.
- **Proxy auto-recovery**: Serial proxy now auto-recovers after system suspend/resume by detecting unhealthy state and attempting to reconnect.
- **Connection handling**: "Port opened but no GRBL response" now auto-disconnects instead of prompting.
- **G-code compatibility**: G53, G10, G28, G30, G38.x, G43.1, G94 no longer produce parser warnings. G93 (inverse time feed rate) produces a warning since height map and time estimates assume G94.
- **Proxy no longer experimental**: Proxy mode has been tested and the [experimental] tag removed from the menu.
- **T codes parsed with comments**: Tool change commands now extract tool name from comments (e.g., `T2 (1/8" End Mill)`) for display during tool changes.
- **Tool change uses Y to confirm**: Changed from P to Y for consistency with other confirmation prompts.

### Bug Fixes

- **Proxy network disconnect**: When network connection is lost during milling via proxy, the proxy now sends soft reset (in addition to feed hold) to fully stop the machine and turn off the spindle. Previously, spindle kept spinning.
- **Mill startup Z safety**: Before starting a G-code file, coppercli now raises Z to safe height (machine coordinates) to prevent dragging across workpiece if Z was left low from previous operation.
- **Windows NuGet restore**: Added troubleshooting note to README for `NETSDK1064: Package System.IO.Ports was not found` error - run `dotnet restore` first.

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
