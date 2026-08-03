# 2026-08 — `WorkOffset` is not G54, and undo-by-snapshot destroys tool compensation

**Who:** Thomer, with Claude Opus 4.8, in `4698964`. Depth adjustment itself arrived in
v0.3.1 (`010915c`, `ba348f1`).

**Tried:** Depth adjustment for re-milling read `machine.WorkOffset.Z`, added the
adjustment, and wrote it back with `G10 L2 P1`. A later fix restored the absolute Z origin
captured before the run.

**Believed:** `WorkOffset` was the work origin, so read-modify-write was symmetric; and
restoring the pre-job snapshot was the obvious way to undo the adjustment.

**Realized:** `WorkOffset` is the *combined* WCO from the status report (G54 + G92 +
tool-length offset), but `G10 L2 P1` sets G54 alone. Writing the combined figure into the
G54 slot re-datums Z by whatever the other two contribute, every time it is applied.
Separately, nothing ever undid the adjustment, so re-milling stacked it: two passes at
−0.05 mm cut 0.10 mm while the display still read −0.05. And the snapshot-restore fix
silently destroyed a mid-job tool change's length compensation, which legitimately rewrites
the same register — the next plunge was off by the difference between the two tools.

**Lesson → rule `read-g54-explicitly`.** Query G54 explicitly (`$#`,
`RefreshWorkOffsetsAsync`) before any `G10 L2 P1`; never derive it by subtracting
`WorkPosition` from `MachinePosition`. Undo an offset **relatively** — subtract what you
added from whatever the origin has become — never by restoring an absolute snapshot, because
something else legitimately owns that same register.

**Touches:** seam `controllers → machine`, rule `read-g54-explicitly`,
`coppercli.Core/Controllers/MillingController.cs`,
`coppercli.Core/Controllers/ToolChangeController.cs`,
`coppercli.Tests/DepthAdjustmentTests.cs`.
