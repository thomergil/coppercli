# `Queue.Synchronized` is not concurrency, and a wall clock decided a safety question

**Problem:** A Reset mid-job tore the connection down from inside the serial worker, leaving
GRBL to finish its buffered moves with nothing attached. The buffer accounting could race to
a negative. A DST shift or an NTP correction could stretch a wait by an hour or expire every
deadline at once.

**Cause:** The serial worker's send, sent and priority queues used
`Queue.Synchronized(new Queue())`, which makes each call atomic but not a check-then-take
sequence: a `Clear()` arriving from the UI or the web between `Count` and `Dequeue` threw
inside the worker. `BufferState`, GRBL's receive-buffer byte accounting, was updated next to
but not with the `Sent` queue. Every wait in the controller layer computed
`DateTime.Now.AddMilliseconds(timeout)`, and `Machine.LastStatusReceived` was a `DateTime`
that homing compared against to decide whether GRBL was responding again — the decision being
whether a `$H` took, which every subsequent `G53` retract depends on. Both defects are in the
serial layer inherited from OpenCNCPilot.

**Fix:** `ConcurrentQueue` with `TryPeek`/`TryDequeue`, and an explicit lock over any pair of
values that describe one fact (bytes outstanding, and the lines they belong to). Timeouts use
a monotonic clock (`Stopwatch`). "Is the peer still talking?" is answered by counting events
(`StatusReportCount`, monotonic via `Interlocked`) rather than timing them. The traffic log
snapshots its writer and swallows `ObjectDisposedException`, so a diagnostic cannot kill the
connection. Fixed in `4698964`. `coppercli.Core/Communication/Machine.cs`,
`coppercli.Core/Controllers/MachineWait.cs`; rules `no-live-collections-across-threads`,
`monotonic-time-and-event-counts`; interfaces `controllers → machine`, `machine → GRBL`.

**Rule:** Hold one lock across a queue check and removal. Use a monotonic clock for deadlines that affect machine decisions.