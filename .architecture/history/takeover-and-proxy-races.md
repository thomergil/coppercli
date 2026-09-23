# Races between the server, the proxy and a terminal takeover

**Date:** 2026-09-23

**Context:** `MachineHold` (see `server-let-go-of-the-machine`) hands the serial port between
the web server and a terminal on the proxy. Audit rounds on that change found each of the
races below.

**Races found and fixed:**
- **Yield set before the busy check.** Setting the yield, then checking whether the machine
  was in use and rolling back, let the connection loop see the yield for a moment and
  disconnect a running job. `TryYieldToTerminal(machineInUse)` checks and sets under one lock.
- **A claim granted twice.** When the proxy could claim the port twice, one terminal
  session's release ended another session's claim, and the server reconnected under a live
  terminal. `TryClaimSerialPort` grants once, and only while yielded and disconnected.
- **Reclaim while the proxy still had the port.** A browser takeover that ignored whether
  the proxy still held the port let the server open it while the proxy was still writing its
  safety stop. The server reconnects only after `ReleaseSerialPort`.
- **Proxy sessions and the accept loop.** The proxy ran each session inline in its accept
  loop, so a second client was never answered and the "client already connected" rejection
  never ran. Moving the session to its own thread then admitted a new client while the
  previous session was still stopping the machine. The slot is now held until the session's
  `finally` (stop, close, release) has finished.
- **Safety stop sent from two places.** The stop is sent in one place,
  `SerialProxy.StopMachineAndClosePort`, after both pump threads have joined and before the
  port closes. It takes the port with `Interlocked.Exchange`, so `Stop` and the session
  cannot both send the stop or both close the port.

**Not closed (raised to the owner, undecided):** a takeover that arrives between a run
start's connected check and its controller starting. No run-start gate exists.

**Lesson:** For a port handed between owners, each hand-off step (check and set, claim,
release, reclaim) must be one atomic step, and the port is not free until the previous
holder's stop sequence has run.

Touches: `coppercli/WebServer/MachineHold.cs`, `coppercli.Core/Communication/SerialProxy.cs`,
`proxy → TCP clients`, rule `server-holds-the-machine`.
