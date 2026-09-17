# One door policy, two implementations

**Problem:** The door policy — which states are answered, how many refused releases are
enough, which states are waited out instead — was written twice and had already drifted: only
one copy flushed the keyboard before asking, only one read Escape while waiting, and only one
withdrew its message on the way out. The terminal copy had no test at all.

**Cause:** `MachineWait` owned `GetDoorState`, `CanReleaseDoorHold`, `GetDoorMessage` and
`ReleaseDoorHoldAsync`, so the door looked single-sourced. Those are the primitives. The
policy that composes them lived in `ControllerBase.EnsureDoorClosedAsync` for a run and in
`MenuHelpers.WaitForDoorClear` for a screen with none. Found in the audit round after
`two-resume-windows-for-one-door.md`.

**Fix:** `MachineWait.ClearDoorHoldAsync` is the loop, and callers pass in how to ask and how
to announce. The browser's endpoint is not a third copy: it validates and releases once,
because the page redraws every broadcast interval and the operator clicks again, so the
browser is the loop. Extracting the loop nearly lost something the old shape held by
accident: the run's abort used to throw from inside the loop, while the machine was still at
the door, and returned as a value and thrown one await later, the retract that follows an
abort was queued against a hold that had gone. `AbandoningTheDoorPrompt_QueuesNoRetract` and
its tool-change twin caught it.

**Rule:** Owning the primitives is not owning the policy; count the places that compose them,
not only the places that define them. When lifting a loop out, ask what each `throw` was
doing where it stood, because unwinding one await later is a different program.
