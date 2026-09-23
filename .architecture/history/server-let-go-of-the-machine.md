# The web server let go of the machine, and each disconnect cleared homing

**Date:** 2026-09-23 · **Decided by:** Thomer (owner)

**Problem:** The operator homed the mill, and the web UI later said it was not homed.

**Rejected proposal:** The first proposal was to clear `Machine.IsHomed` and
`AppState.IsWorkZeroSet` only when there was evidence that GRBL had restarted, not on every
link drop. That reverses a recorded safety decision (`stale-work-zero-and-height-map`,
`work-zero-deliberately-stays-in-appstate`, `automatic-door-release-and-unverified-homing`).
A power cycle, or a board moved on the bed while the link is down, leaves no evidence the
program can see. The rule that clears homing and work zero on disconnect stays.

**Cause:** `CncWebServer` disconnected the machine when the last browser WebSocket closed,
which happens every time a phone locks its screen, and again after a 5-minute idle timer.
Each disconnect correctly cleared homing and work zero.

**Decision (owner):** "The server should always hang on to the machine. We can't just let
go of the machine." In server mode the server connects at startup and keeps the connection
for its whole life. Browsers opening and closing never connect or disconnect it. The owner
was asked and chose to keep the remote-terminal takeover: a terminal on another computer may
take the machine through the proxy, except while the machine is in use, and the server
reconnects once that terminal leaves.

**Fix:** `coppercli/WebServer/MachineHold.cs` owns who has the serial port in server mode.
`KeepConnectedAsync` keeps the connection matching `IsHeld` in both directions. A terminal
asks through `POST /api/terminal-takeover`; if it does not claim the port within
`TerminalTakeoverWindowMs`, the server connects again. A browser takes the machine back
through `POST /api/browser-takeover`, which never disconnects the server. `Stop` at shutdown
waits for any connect still running. `CncWebServer.HoldsMachine` is true when no server
exists, so no caller reads a missing server as a machine the server has yielded. Removed: the idle-disconnect timer, the disconnect on the
last browser leaving, the connect on WebSocket open, `TryReconnectLoop`, `/api/connect`,
`/api/disconnect` and `/api/ports`.

**Also learned:**
- `Machine.Connect` does not throw on failure. It raises `NonFatalException` and returns.
  A retry loop that expects an exception sees every failure as a success, so
  `CncWebServer.ConnectMachine` wraps it and throws with the reason.
- In server mode the console is the monitor screen. Machine errors go to its message list,
  and `AppState.SuppressErrors` is set for the server's whole lifetime.

**Lesson:** When state is lost on disconnect, find out why the disconnect happened before
weakening the rule that clears the state.

**Rule:** `server-holds-the-machine`; `machine-state-single-writer` is unchanged.

Touches: `coppercli/WebServer/MachineHold.cs`, `CncWebServer.ConnectMachine`,
`CncWebServer.HoldsMachine`, `web → browser`, `proxy → TCP clients`.
