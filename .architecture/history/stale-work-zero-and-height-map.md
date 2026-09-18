# Machine-state flags owned by each UI went stale

**Problem:** After booting, declining "trust previous session data" and picking a new file,
the UI still claimed a complete height map and a set work zero. Neither existed. A
web-initiated or main-menu disconnect left `IsWorkZeroSet` true, so the milling and probing
gates trusted an origin the machine no longer held. The terminal probe path left `IsProbing`
stale, so terminal probes did not get the error suppression web probes did. The homed flag
survived a disconnect and a soft reset, so soft limits were computed against a reference
frame the controller had forgotten.

**Cause:** Each UI owned the flags that describe machine reality. The TUI connection menu
cleared the stored work zero on disconnect. `AppState.IsProbing` was a boolean each screen
set for itself. `IsHomed` was assigned wherever homing happened to be initiated. Every path
that did not go through the owning screen left the flag stale, which could allow a job
to start with an invalid work origin.

**Fix:** A fact about the machine belongs to `Machine`, or is derived from the controller
that owns it, and is assigned in exactly one place. `IsHomed` is set only inside
`MachineWait.HomeAsync`. `IsProbing` is not stored; it is `_probeController?.IsActive`.
Work-zero invalidation runs on the connection-state event in `AppState`, not a menu action,
so every disconnect path behaves identically. Fixed in `4698964`. `coppercli/AppState.cs`,
`coppercli.Core/Controllers/MachineWait.cs`, `coppercli.Core/Communication/Machine.cs`; rule
`machine-state-single-writer`; interface `controllers → machine (IMachine)`.

**Rule:** Define machine state in `Machine` and derive UI values from it. Keep operator assertions about the workpiece in `AppState`, where they can be cleared when the session changes.
