# 2026-09 — One door policy, two implementations

**Who:** Thomer, with Claude Opus 5, during the audit round that followed
`2026-09-two-resume-windows-one-door`.

**Tried:** finish the door work by checking that the one-channel rule held everywhere.

**Believed:** it did. `MachineWait` owned `GetDoorState`, `CanReleaseDoorHold`,
`GetDoorMessage` and `ReleaseDoorHoldAsync`, so the door looked single-sourced.

**Realized:** those are the primitives. The *policy* that composes them — which states are
answered, how many refused releases are enough, which states are waited out instead — was
written twice: once in `ControllerBase.EnsureDoorClosedAsync` for a run, and once in
`MenuHelpers.WaitForDoorClear` for a screen with none. The two had already drifted: only one
flushed the keyboard before asking, only one read Escape while waiting, and only one
withdrew its message on the way out. The terminal copy had no test at all.

`MachineWait.ClearDoorHoldAsync` is now the loop, and callers pass in how to ask and how to
announce. The browser's endpoint is not a third copy: it validates and releases once, because
the page redraws every broadcast interval and the operator clicks again — the browser is the
loop.

Extracting it nearly lost something the old shape held by accident. The run's abort used to
throw from inside the loop, while the machine was still at the door; returned as a value and
thrown one await later, the retract that follows an abort was queued against a hold that had
gone. `AbandoningTheDoorPrompt_QueuesNoRetract` and its tool-change twin caught it.

**Lesson:** owning the primitives is not owning the policy. Count the places that compose
them, not just the places that define them. And when lifting a loop out, ask what each
`throw` was doing where it stood: unwinding one await later is a different program.
