# Two front ends, each implementing the same workflow

**Problem:** Separate terminal and browser workflow implementations produced these differences:

- The terminal's session restore skipped the height-map question whenever the operator
  declined to trust the stored work zero; the browser's had no such gate.
- Applying a height map is additive, and only the web path reloaded the original G-code
  first, so the same action cut correctly from the browser and roughly twice as deep from the
  terminal.
- Only the TUI connection screen cleared `IsWorkZeroSet`, so a web or main-menu disconnect
  left a stale-trusted origin.
- `/api/mill/start` validated only connection and file, so a direct request could start a job
  with an unapplied height map.
- The probe-contact guard was written out at four call sites; a fifth mover would have
  dragged the probe tip sideways across the copper.
- The serial layer and the milling controller disagreed on what an M6 line is, so `T1 M6` was
  withheld from the machine but never paused the job, which kept cutting with the previous
  tool.
- `MillingOptions.SkipConfirmation` was never read by anything: a write-only flag implying a
  contract the controller did not honor.

**Cause:** After v0.4.0 (`5780ca8`) added the browser UI alongside the TUI, the startup
session-restore question sequence, loading a replacement height map, clearing work zero on
disconnect, the pre-mill validation, the probe-contact guard, the machine-connected test, the
file-summary JSON and the M6 line predicate were each written twice, on the reasoning that
the two interfaces were presentation layers over shared controllers.

**Fix:** Controllers define motion, question order, validation, and the effects of each answer.
Checks used by both interfaces have one implementation. Browser-specific preflight stays in
the web layer. Fixed in `4698964`. Both interfaces still repeat controller setup, recorded
as a GAP on the `ui → controllers` interface.
`coppercli/WebServer/CncWebServer.cs`, `coppercli/Menus/MillMenu.cs`,
`coppercli/Menus/ProbeMenu.cs`, `coppercli/SessionRestore.cs`, `coppercli/AppState.cs`; rules
`workflows-live-in-controllers`, `machine-state-single-writer`.

**Rule:** Put workflow decisions shared by the terminal and browser in controllers. Keep browser-specific preflight checks in the web layer.
