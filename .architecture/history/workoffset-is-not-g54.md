# `WorkOffset` is not G54, and undo-by-snapshot destroys tool compensation

**Problem:** Applying depth adjustment re-datumed Z by whatever G92 and the tool-length
offset contributed, every time it was applied. Nothing ever undid the adjustment, so
re-milling stacked it: two passes at −0.05 mm cut 0.10 mm while the display still read
−0.05. The snapshot-restore fix then destroyed a mid-job tool change's length compensation,
so the next plunge was off by the difference between the two tools.

**Cause:** Depth adjustment read `machine.WorkOffset.Z`, added the adjustment and wrote it
back with `G10 L2 P1`. `WorkOffset` is the combined WCO from the status report
(G54 + G92 + tool-length offset), but `G10 L2 P1` sets G54 alone. The later fix restored the
absolute Z origin captured before the run, and a tool change legitimately rewrites that same
register. Depth adjustment arrived in v0.3.1 (`010915c`, `ba348f1`).

**Fix:** Query G54 explicitly (`$#`, `RefreshWorkOffsetsAsync`) before any `G10 L2 P1`; never
derive it by subtracting `WorkPosition` from `MachinePosition`. Undo an offset relatively, by
subtracting what you added from whatever the origin has become. Fixed in `4698964`.
`coppercli.Core/Controllers/MillingController.cs`,
`coppercli.Core/Controllers/ToolChangeController.cs`,
`coppercli.Tests/DepthAdjustmentTests.cs`; rule `read-g54-explicitly`; interface
`controllers → machine`.

**Rejected:** Undoing the adjustment by restoring the pre-job absolute Z snapshot. Something
else legitimately owns that register.

**Rule:** Never undo an offset by restoring an absolute snapshot of a register another
workflow also writes.
