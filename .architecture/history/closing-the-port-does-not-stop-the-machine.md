# Closing the port does not stop the machine

**Problem:** Operator report: "I stop probing, quit the program, then tried to quit the
server, then the machine continue probing!?! That's a bad and dangerous bug." `ServerMenu`
reconnects a couple of seconds later to a machine still in motion. Stopping a probe also left
the tip where the last descent put it, in the work.

**Cause:** `Machine.Disconnect()` closed the serial port and disposed the stream, and every
teardown path — quitting the server, the last browser leaving, the TUI disconnecting — went
through it. GRBL works through its planner buffer whether or not anyone is listening, so a
probe with queued moves keeps running after the port closes, and closing the port removes the
way to stop it. `SerialProxy.ForceDisconnectClient` had the same defect in a second place: it
closed the client without stopping the machine, while `HandleClientDisconnect` beside it did
stop it.

**Fix:** Three things had to hold. The bytes have to reach the wire:
`Machine.WriteStopSequence(Stream)` writes the two real-time bytes straight to the
connection. Nothing may be streaming while they are written: the decision is taken first, the
worker stopped, and the bytes written after. A port that never answered as GRBL is left
alone: `NeedsStopBeforeDisconnect` exempts `StatusDisconnected`, or auto-detect would send
Ctrl-X to every serial port on the computer. Idle alone is not enough, because a line sent
moments ago sits unparsed in GRBL's receive buffer while the status still reads Idle, so an
outstanding byte count also counts as work. `ForceDisconnectClient` and
`HandleClientDisconnect` both call `SendSafetyStop`, which calls `WriteStopSequence`, so the
sequence and its delays have one definition.
`ProbeController.CleanupAsync` stops the machine, then raises Z to the probe safe height, and
every way a run ends reaches it: the cancel path in `RunAsync` leaves by exception rather
than returning, and `TraceOutlineAsync` calls it from both catch blocks. The lift runs on its
own token, because the run's is already cancelled and a stop must not appear to hang; that
token expiring can throw `OperationCanceledException` rather than return false, so both count
as unconfirmed and the operator is told. Order matters: a lift queued before the soft reset
is wiped by it, so `StoppingARun_RetractsToSafeHeight` asserts M5 precedes the retract rather
than merely containing it, and reversing the two fails the test.
`coppercli.Core/Communication/Machine.cs`, `coppercli.Core/Communication/SerialProxy.cs`,
`coppercli.Core/Controllers/ProbeController.cs`, `coppercli/WebServer/CncWebServer.cs`,
`coppercli.Tests/SafetyCheckTests.cs`, `coppercli.Tests/ProbeControllerTests.cs`; rule
`closing-the-port-does-not-stop-grbl`.

**Rejected:** Calling `machine.FeedHold()` and `machine.SoftReset()`, which only enqueue to
`ToSendPriority`; that queue is drained by the worker loop, which has already exited on the
teardown path, so the stop was never sent. Then writing the bytes as the first statement of
`Disconnect()`, with the worker still feeding lines to a machine that had just been reset.

**Rule:** Dropping the connection does not end the job.
