# 2026-09 — Closing the port does not stop the machine

**Who:** Thomer, with Claude Opus 5, after a report from the operator: "I stop probing, quit
the program, then tried to quit the server, then the machine continue probing!?! That's a
bad and dangerous bug."

**Tried:** `Machine.Disconnect()` closed the serial port and disposed the stream. Every
teardown path - quitting the server, the last browser leaving, the TUI disconnecting -
went through it.

**Believed:** dropping the connection ends the job, because nothing more can be sent.

**Realized:** GRBL works through its planner buffer whether or not anyone is listening. A
probe with queued moves keeps running after the port closes, and `ServerMenu` reconnects a
couple of seconds later to a machine still in motion. Closing the port removes the way to
stop it.

Three things had to be true to fix it. **The bytes have to reach the wire.** The first
attempt called `machine.FeedHold()` and `machine.SoftReset()`, which only enqueue to
`ToSendPriority`; that queue is drained by the worker loop, which has already exited on the
teardown path, so the stop was never sent. `Machine.WriteStopSequence(Stream)` now writes
the two real-time bytes straight to the connection. **Nothing may be streaming while they
are written.** The second attempt wrote them as the first statement of `Disconnect()`, with the
worker still feeding lines to a machine that had just been reset; the decision is now taken
first, the worker stopped, and the bytes written after. **A port that never answered as
GRBL is left alone.** `NeedsStopBeforeDisconnect` exempts `StatusDisconnected`, or
auto-detect would send Ctrl-X to every serial port on the computer. Idle is not enough on
its own: a line sent moments ago sits unparsed in GRBL's receive buffer while the status
still reads Idle, so an outstanding byte count also counts as work.

`SerialProxy.ForceDisconnectClient` had the same defect in a second place - it closed the
client without stopping the machine, while `HandleClientDisconnect` beside it did stop it.
Both now call `SendSafetyStop`, which calls `WriteStopSequence`, so the sequence and its
delays have one definition.

**A stopped probe now lifts.** Stopping left the tip where the last descent put it, in
the work. `ProbeController.CleanupAsync` stops the machine, then raises Z to the probe safe
height. Every way a run ends reaches it: the cancel path in `RunAsync` leaves by exception
rather than returning, and `TraceOutlineAsync` calls it from both catch blocks.
The lift runs on its own token, because the run's is already cancelled and a stop must not
appear to hang. That token expiring can throw `OperationCanceledException` rather than
return false, so both count as unconfirmed and the operator is told.

**Lesson → new rule `closing-the-port-does-not-stop-grbl`.** Order matters: a lift queued
before the soft reset is wiped by it, so `StoppingARun_RetractsToSafeHeight` asserts M5 precedes the
retract rather than merely containing it, and reversing the two fails the test.

**Touches:** rule `closing-the-port-does-not-stop-grbl`, `coppercli.Core/Communication/Machine.cs`,
`coppercli.Core/Communication/SerialProxy.cs`, `coppercli.Core/Controllers/ProbeController.cs`,
`coppercli/WebServer/CncWebServer.cs`, `coppercli.Tests/SafetyGuardTests.cs`,
`coppercli.Tests/ProbeControllerTests.cs`.
