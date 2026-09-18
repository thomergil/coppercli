# `IsWorkZeroSet` stays in `AppState`, not `Machine`

**Problem:** An audit during the `4698964` sweep applied rule `machine-state-single-writer`
to `AppState.IsWorkZeroSet` and recommended moving it into `Machine`, or deriving it from
GRBL's reported G54 offset.

**Cause:** GRBL persists G54 in EEPROM, where it survives a power cycle, so after a reconnect
the controller reports a work origin that may have been set for a different board, in a
different session, weeks ago. Deriving "is the work zero set?" from GRBL's own state answers
"is a number stored?", never "does that number still describe the board currently on the
table?" It would always answer yes, which is the unsafe answer.

**Fix:** `IsWorkZeroSet` stays in `AppState`. It records this session's operator asserting an
origin for the workpiece in front of them. Startup asks "trust the previous session's zero?"
rather than reading it, and a disconnect clears it (`stale-work-zero-and-height-map.md`),
because the machine may be repositioned or power-cycled before it returns, so the operator's
assertion expires even though GRBL's EEPROM value does not. The height map is trusted only
against a recorded `ProbeContext`, never because a file exists. `coppercli/AppState.cs`,
`coppercli/SessionRestore.cs`, `coppercli.Core/GCode/ProbeContext.cs`; rule
`machine-state-single-writer`.

**Rejected:** Moving the flag into `Machine`, and deriving it from GRBL's G54, for the reason
above. The audit's recommendation was overruled.

**Rule:** Keep `IsWorkZeroSet` in `AppState`, because it records the operator's assertion about this session's workpiece. Do not derive it from GRBL's stored G54 offset.