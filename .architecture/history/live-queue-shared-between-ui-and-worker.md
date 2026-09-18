# A live mutable queue shared between the display and the probe loop

**Problem:** A probing run died after about 20 points with "Collection was modified;
enumeration operation may not execute", and the whole program went down with it.

**Cause:** `ProbeGrid` exposed the list of points still to measure as a public mutable list,
and the terminal thread enumerated it while the probe loop reordered and removed from it.
Cleanup made it worse: controller cleanup called `Reset()` from a `finally` block, and when
the failure came from the display thread the controller was legitimately still `Running`, so
`Reset()` threw "Cannot reset: controller is Running" and replaced the real error with its
own. The FSM also had no `Completing → Cancelled` edge, so pressing Stop during the final
retract threw out of a `finally` too.

**Fix:** `ProbeGrid` owns the queue and returns `SnapshotRemaining()`, so no caller can
enumerate the live list. Cleanup stops the controller first, wraps cleanup, and logs rather
than propagates. `ControllerBase.ValidTransitions` records `Completing → Cancelled`. Fixed in
`4698964`. `coppercli.Core/GCode/ProbeGrid.cs`,
`coppercli.Core/Controllers/ControllerBase.cs`,
`coppercli.Core/Controllers/ProbeController.cs`; rule `no-live-collections-across-threads`;
interface `ui → controllers`.

**Rule:** Return a snapshot when a UI reads a worker's mutable collection. Preserve the
original error if cleanup also fails. Allow an operator to abort during the final phase.
