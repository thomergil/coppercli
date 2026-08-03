# 2026-08 — `Queue.Synchronized` mistaken for concurrency, and a wall clock trusted with a safety question

**Who:** Thomer, with Claude Opus 4.8, in `4698964`. Both defects are in the serial layer
inherited from OpenCNCPilot.

**Tried:** The serial worker's send / sent / priority queues used
`Queue.Synchronized(new Queue())`, and `BufferState` (GRBL's receive-buffer byte
accounting) was updated next to, but not with, the `Sent` queue. Separately, every wait in
the controller layer computed `DateTime.Now.AddMilliseconds(timeout)`, and
`Machine.LastStatusReceived` was a `DateTime` that homing compared against to decide
whether GRBL was responding again.

**Believed:** `Queue.Synchronized` made the queues thread-safe; and the wall clock is fine
for short waits.

**Realized:** `Queue.Synchronized` makes each *call* atomic, not a check-then-take
sequence. A `Clear()` arriving from the UI or the web between `Count` and `Dequeue` threw
inside the serial worker and tore the connection down — leaving GRBL to finish its buffered
moves with nothing attached. That happens precisely when someone hits Reset mid-job. The
buffer accounting could likewise race to a negative. And a DST
shift or an NTP correction can stretch a wait by an hour or expire every deadline at once,
and the decision at stake is whether a `$H` took, which every subsequent `G53` retract
depends on.

**Lesson → rules `no-live-collections-across-seams` and `monotonic-time-and-event-counts`.**
"Synchronized" collections are a trap: per-operation locking does not make a read-then-mutate
sequence safe. Use `ConcurrentQueue` with `TryPeek`/`TryDequeue`, and hold an explicit lock
over any *pair* of values that describe one fact (bytes outstanding, and the lines they
belong to). Timeouts use a monotonic clock (`Stopwatch`), always. Where the question is "is
the peer still talking?", **count events** (`StatusReportCount`, monotonic via `Interlocked`)
rather than timing them — a clock step must never be able to answer a safety question.
Related: a diagnostic must never be able to kill the connection, so the traffic log
snapshots its writer and swallows `ObjectDisposedException`.

**Touches:** seam `controllers → machine`, seam `machine → GRBL`, rule
`monotonic-time-and-event-counts`, `coppercli.Core/Communication/Machine.cs`,
`coppercli.Core/Controllers/MachineWait.cs`.
