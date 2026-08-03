# 2026-08 — Two front ends, each implementing the same workflow

**Who:** Thomer. The split arrived with the Web UI in v0.4.0 (`5780ca8`); the drift was
swept in `4698964`.

**Tried:** After v0.4.0 added the browser UI alongside the TUI, several workflows were
written once per front end: the startup session-restore question sequence, loading a
replacement height map, clearing work zero on disconnect, the pre-mill validation, the
"don't move XY while the probe is touching the board" guard, the machine-connected test,
file-summary JSON, and the M6 line predicate. `MillingOptions` even grew a
`SkipConfirmation` flag "for Web UI".

**Believed:** The two interfaces were presentation layers over shared controllers, so a
little parallel glue at the edges was acceptable.

**Realized:** Every one of them drifted, and the drift *was* the bug:
- The terminal's session restore skipped the height-map question whenever the operator
  declined to trust the stored work zero; the browser's had no such gate.
- Applying a height map is **additive**, and only the web path reloaded the original G-code
  first — so the same action cut correctly from the browser and roughly twice as deep from
  the terminal.
- Only the TUI connection screen cleared `IsWorkZeroSet`, so a web or main-menu disconnect
  left a stale-trusted origin.
- `/api/mill/start` validated only connection and file, letting a direct request start a
  job with an unapplied height map.
- The probe-contact guard was written out at four call sites; a fifth mover would have
  dragged the probe tip sideways across the copper.
- The serial layer and the milling controller disagreed on what an M6 line is, so `T1 M6`
  was withheld from the machine but never paused the job — it kept cutting with the
  previous tool.
- `SkipConfirmation` was never read by anything: a write-only flag implying a contract the
  controller did not honor.

**Lesson → rule `workflows-live-in-controllers`.** Two UIs is the strongest
argument for putting the *whole* workflow in the controller layer — not just the motion,
but the question sequence, the ordering, the validation, and the consequences of each
answer. If a front end *can* express a policy, the two front ends will eventually express
different ones. Corollaries: a predicate that gates two layers (is this an M6 line?) needs
exactly one definition; and a controller option that exists "for the web UI" is a layering
smell — gate the web at its own preflight instead.

**Still open:** the controller-*wiring* ritual is written out on both sides. See the
GAP on the `ui → controllers` seam.

**Touches:** seam `ui → controllers`, rule `workflows-live-in-controllers`, rule
`machine-state-single-writer`, `coppercli/WebServer/CncWebServer.cs`,
`coppercli/Menus/MillMenu.cs`, `coppercli/Menus/ProbeMenu.cs`,
`coppercli/SessionRestore.cs`, `coppercli/AppState.cs`.
