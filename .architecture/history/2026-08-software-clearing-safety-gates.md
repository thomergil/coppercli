# 2026-08 — Software clearing the gates that exist to require a human

**Who:** Thomer, with Claude Opus 4.8, in `4698964`.

**Tried:** Two automations meant to smooth the path to starting a job. (a) The
machine-readiness check cleared a GRBL `Door` state by sending Cycle Start. (b)
`MillingController` carried its own homing routine that treated "machine is Idle" as
homing success.

**Believed:** Both were harmless unblocking — the machine was in a state the job could not
start from, so put it in one it could.

**Realized:** (a) Cycle Start on `Door` restarts the spindle and resumes motion because
the *software* decided the enclosure was clear. (b) A rejected `$H` — exactly what happens
when homing is disabled (`$22=0`) or there are no limit switches — leaves the status at
Idle, so the idle-wait succeeded on its first poll. The job then ran every `G53` safety
retract against machine coordinates that were never established. `CLAUDE.md` already named
homing as *the* worked example of a single source of truth, and there were two
implementations of it. Compounding this, `IsHomed` was never cleared on disconnect or soft
reset, so a power cycle or a replug left milling skipping homing.

**Lesson → rules `never-auto-clear-a-safety-gate` and `machine-state-single-writer`.**
Never auto-clear a state that exists to require human confirmation; the door is the
operator's decision, always. Homing is not optional and there is deliberately no skip —
without it `G53` retracts have no reference to retract to. And detecting that a command
took requires evidence the machine *changed*, not the absence of a complaint: watch for a
status change, count monotonic status reports to tell "GRBL went quiet" from "GRBL is
answering and still Idle", and surface GRBL's own stated reason so `$22=0` says so.

**Touches:** seam `controllers → machine`, seam `machine → GRBL`, rule
`never-auto-clear-a-safety-gate`, rule `machine-state-single-writer`,
`coppercli.Core/Controllers/MachineWait.cs`,
`coppercli.Core/Controllers/HomingOutcome.cs`,
`coppercli.Core/Controllers/MillingController.cs`.
