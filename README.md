# <img src="doc/logo.jpg" alt="coppercli logo" width="32" valign="middle"> coppercli

A platform-agnostic CLI tool for PCB milling with GRBL CNC machines, featuring auto-leveling using a probed height map. Originally based on [OpenCNCPilot](https://github.com/martin2250/OpenCNCPilot).

| Probing | Milling |
|:-------:|:-------:|
| <img src="doc/probing.png" width="400"> | <img src="doc/milling-screen.png" width="400"> |

## Install

[![Windows](https://img.shields.io/badge/Windows-Installer-blue?style=for-the-badge&logo=windows)](https://github.com/thomergil/coppercli/releases/latest)
[![macOS](https://img.shields.io/badge/macOS-Homebrew-orange?style=for-the-badge&logo=apple)](https://github.com/thomergil/homebrew-coppercli)
[![Linux](https://img.shields.io/badge/Linux-Download-yellow?style=for-the-badge&logo=linux)](https://github.com/thomergil/coppercli/releases/latest)

| Platform | Install |
|----------|---------|
| **Windows** | Download and run installer from [Releases](https://github.com/thomergil/coppercli/releases/latest) |
| **macOS** | `brew tap thomergil/coppercli && brew install coppercli` |
| **Linux** | Download tarball from [Releases](https://github.com/thomergil/coppercli/releases/latest), extract, run `./coppercli` |
| **From source** | Clone repo, then `./run.sh` (macOS/Linux) or `run.bat` (Windows) |

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) for running from source.

## Screenshots

| Main Menu                                 | File Browser                                 | Jog                                      |
| ----------------------------------------- | -------------------------------------------- | ---------------------------------------- |
| <img src="doc/main-menu.png" width="200"> | <img src="doc/file-browser.png" width="200"> | <img src="doc/jog-menu.png" width="200"> |

| Probe Setup | Probing | Milling |
|-------------|---------|---------|
| <img src="doc/probe-menu.png" width="200"> | <img src="doc/probing.png" width="200"> | <img src="doc/milling-screen.png" width="200"> |

| Settings | Proxy | Milled PCB |
|----------|-------|------------|
| <img src="doc/settings-menu.png" width="200"> | <img src="doc/proxy.png" width="200"> | <img src="doc/milled-pcb.jpg" width="200"> |

## Tutorial

For a complete end-to-end guide on milling PCBs—from KiCad export through G-code generation to probing and milling, see [Milling a PCB with auto-leveling using a Carbide 3D Nomad 3](https://github.com/thomergil/pcb-nomad3).

## Background

This project is based on [OpenCNCPilot](https://github.com/martin2250/OpenCNCPilot) by [martin2250](https://github.com/martin2250), which is an excellent tool for CNC machine control and PCB auto-leveling. OpenCNCPilot has solid core functionality for G-code parsing and height map interpolation.

However, OpenCNCPilot has some limitations:

- **Windows-only**: Built with WPF, it only runs on Windows
- **GUI-heavy workflow**: The interface requires a lot of mouse clicking and navigating through dialogs, which can be cumbersome.
- **No session persistence**: If you disconnect or the program crashes mid-probe, you lose your progress and have to start over

coppercli addresses these issues by providing a keyboard-driven CLI that runs on Linux, macOS, and Windows, with robust session recovery.

**A Note on Development**: C#/.NET is not my language of choice, but I wanted to leverage the core functionality in OpenCNCPilot rather than rewrite G-code parsing and height map interpolation from scratch. I used [Claude Code](https://claude.ai/claude-code) to rework the codebase into this CLI version.

## Features

### Platform Agnostic
- Runs on Linux, macOS, and Windows
- .NET 8 runtime
- Auto-detect serial port and baud rate (cycles through common rates to find GRBL devices)

### Keyboard-Driven Interface
- Single-key navigation throughout
- Arrow keys for jogging, Tab to cycle speeds
- Number keys and mnemonics for menu selection
- Smart menu defaults: automatically highlights the most logical next step (Connect → Load → Move → Probe → Mill)
- Built-in file browser for G-code and probe grid files (remembers last directory)
- No mouse required

### Probing Features
- **Probe grid auto-leveling**: Compensates for PCB surface irregularities
- **Outline traversal**: Before probing, traverse the outer boundary of the probe area to check for collisions or clearance issues
- **Configurable probe parameters**: Safe height, max depth, feed rate, grid size
- **Save/load probe grids**: Reuse probe data across sessions (.pgrid files)

### Session Persistence

- **Continue where you left off**: Interrupted probing sessions are auto-saved and can be resumed
- **Remembers your last file**: Offers to reload the last G-code file on startup
- **Trusts stored work zero**: Option to accept GRBL's stored work coordinate system from a previous session

### Machine Control

- Multiple jog speed presets (Tab to cycle: Fast/Normal/Slow/Creep)
- Pause/Resume/Stop during milling with automatic spindle shutdown and Z retraction
- Home, unlock, and soft reset commands
- XY, Z, and XYZ homing
- Single Z probe (find Z height at current XY position)
- Quick positioning (go to X0Y0, Z0, Z+6mm, Z+1mm)
- Move to the center of the loaded G-code file

### G-Code Handling
- View file bounds
- Apply height map/probe point compensation
- Run with real-time progress display
- 2D position grid visualization during milling (shows spindle position, visited/unvisited areas)
- Terminal resize detection with auto-redraw

## Proxy Mode (EXPERIMENTAL)

coppercli can act as a serial-to-TCP bridge, allowing remote GRBL clients to connect to your CNC machine over the network.

Select "Proxy [experimental]" from the main menu, or start it from the command line:

```bash
# Start proxy with interactive TUI display
coppercli --proxy

# Override the default TCP port (34000)
coppercli --proxy --port 35000

# Run without TUI (for services/scripts)
coppercli --proxy --headless
```

When the proxy starts, it displays the IP addresses clients can use to connect. A client should connect to the displayed IP and port (default: 34000) using TCP. Only one client can connect at a time. Clients that disconnect ungracefully are detected via heartbeat timeout (30 seconds).

## Command-Line Arguments

| Argument | Short | Description |
|----------|-------|-------------|
| `--proxy` | `-p` | Start directly in proxy mode using saved serial settings |
| `--port <number>` | | Override TCP port for proxy mode (default: 34000) |
| `--headless` | `-H` | Run proxy without TUI (for services/background) |
| `--debug` | `-d` | Enable debug logging to `coppercli.log` |

## Configuration

Two JSON files are stored in the working directory (both managed by coppercli):

**`settings.json`** - User preferences:
- **Connection**: Serial port, baud rate, ethernet IP/port
- **Jogging**: Feed rates and distances for normal/slow modes
- **Probing**: Safe height, max depth, feed rate, grid size
- **Outline traversal**: Height and feed rate for boundary check

**`session.json`** - Session state (auto-managed):
- Last loaded G-code file path
- Last file browser directory
- Interrupted probe session data for recovery

## File Formats

- **G-code**: `.nc`, `.gcode`, `.ngc`, `.gc`, `.tap`, `.cnc`
- **Probe grids**: `.pgrid` (XML format, compatible with OpenCNCPilot's `.hmap`)

## Warning

**This software is EXTREMELY EXPERIMENTAL and may damage your CNC machine. Use at your own risk.**

Always:
- Start with the spindle off when testing
- Use the outline traversal feature to check clearance before probing
- Keep your hand on the emergency stop
- Verify probe data looks reasonable before running G-code

## License

MIT License - see [LICENSE](LICENSE)

## Acknowledgments

- [OpenCNCPilot](https://github.com/martin2250/OpenCNCPilot) by [martin2250](https://github.com/martin2250) - the foundation this project is built on
- [Spectre.Console](https://spectreconsole.net/) - console UI library
- [Claude Code](https://claude.ai/claude-code) - AI pair programming assistant
