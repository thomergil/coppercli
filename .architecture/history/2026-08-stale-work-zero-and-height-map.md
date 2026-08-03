# 2026-08 — "The complete height map was a lie": state scattered across UIs

**Who:** Thomer, reporting from a real milling session; fixed with Claude Opus 4.8 in `4698964`.

**Tried:** Letting each UI own the flags that describe machine reality. The TUI connection
menu cleared the stored work zero on disconnect. `AppState.IsProbing` was a boolean each
screen set for itself. `IsHomed` was assigned wherever homing happened to be initiated.

**Believed:** Each screen knows what it just did, so each screen can keep the corresponding
flag honest. One writer per code path looked simpler than routing everything through a
single owner.

**Realized:** Every path that did *not* go through the owning screen left the flag stale,
and a stale flag on this system is a gate that opens when it should be shut. Thomer's
report: after booting, declining "trust previous session data",
and picking a new file, the UI still claimed a complete height map and a set work zero.
Neither existed:

- A web-initiated or main-menu disconnect left `IsWorkZeroSet` true, so the milling and
  probing gates trusted an origin the machine no longer held.
- The terminal probe path left `IsProbing` stale, so terminal probes did not get the error
  suppression that web probes did.
- The homed flag survived a disconnect and a soft reset, so soft limits were computed
  against a reference frame the controller had forgotten.

**Lesson → rule `machine-state-single-writer`.** A fact about the machine belongs to the
`Machine` object (or is *derived* from the controller that owns it), assigned in exactly one
place, never mirrored into a UI-owned flag. `IsHomed` is set only inside
`MachineWait.HomeAsync`. `IsProbing` is not stored at all — it is `_probeController?.IsActive`.
Work-zero invalidation hangs off the connection-state event in `AppState`, not off any menu,
so every disconnect path behaves identically. When a new piece of machine state appears, the
question is not "which screens must remember to update it" but "who owns it, and whether
everyone else can derive it".

**Touches:** seam `controllers → machine (IMachine)`, rule `machine-state-single-writer`,
`coppercli/AppState.cs`, `coppercli.Core/Controllers/MachineWait.cs`,
`coppercli.Core/Communication/Machine.cs`.
