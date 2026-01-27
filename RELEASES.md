# Release Notes

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
