# 2026-08 — A live mutable queue handed across the UI/worker seam

**Who:** Thomer, reporting a crash from a real probing run (it died after ~20 points);
fixed with Claude Opus 4.8 in `4698964`.

**Tried:** `ProbeGrid` exposed the list of points still to measure as a public mutable
list. Controller cleanup called `Reset()` from a `finally` block.

**Believed:** The display just reads it; the probe loop just writes it.

**Realized:** The terminal thread enumerated the list while the probe loop reordered and
removed from it — "Collection was modified; enumeration operation may not execute",
reliably after a few dozen points, losing the run. The *cleanup* then made it worse: when
the failure came from the display thread the controller was legitimately still `Running`, so
`Reset()` threw "Cannot reset: controller is Running", replacing the real error with its own
and taking the whole program down. The FSM also had no `Completing → Cancelled` edge, so
pressing Stop during the final retract — a normal operator action — threw out of
a `finally` too.

**Lesson → rule `no-live-collections-across-seams`.** Never hand out a live mutable
collection across a UI/worker seam; own the queue and return a snapshot
(`SnapshotRemaining()`), so no caller *can* enumerate the live list. Cleanup must never
throw over the error that caused it: stop the controller first, wrap cleanup, log rather
than propagate. And model the states an operator can actually reach — an abort during a
"final" phase is normal, not exceptional. `ControllerBase`'s `ValidTransitions` table
records that judgment.

**Touches:** seam `ui → controllers`, rule `no-live-collections-across-seams`,
`coppercli.Core/GCode/ProbeGrid.cs`, `coppercli.Core/Controllers/ControllerBase.cs`,
`coppercli.Core/Controllers/ProbeController.cs`.
