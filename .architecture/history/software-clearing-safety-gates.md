# Software clearing the gates that exist to require a human

**Problem:** Two automations meant to smooth the path to starting a job. The
machine-readiness check cleared a GRBL `Door` state by sending Cycle Start, which restarts
the spindle and resumes motion because the software decided the enclosure was clear. And a
job ran every `G53` safety retract against machine coordinates that were never established.

**Cause:** `MillingController` carried its own homing routine that treated "machine is Idle"
as homing success. A rejected `$H` — what happens when homing is disabled (`$22=0`) or there
are no limit switches — leaves the status at Idle, so the idle-wait succeeded on its first
poll. `CLAUDE.md` already named homing as the worked example of a single source of truth, and
there were two implementations of it. `IsHomed` was also never cleared on disconnect or soft
reset, so a power cycle or a replug left milling skipping homing.

**Fix:** Neither gate is cleared by software; the door is the operator's decision. Homing is
not optional and there is deliberately no skip, because without it `G53` retracts have no
reference to retract to. Detecting that a command took requires evidence the machine changed:
watch for a status change, count monotonic status reports to tell "GRBL went quiet" from
"GRBL is answering and still Idle", and surface GRBL's own stated reason so `$22=0` says so.
Fixed in `4698964`. `coppercli.Core/Controllers/MachineWait.cs`,
`coppercli.Core/Controllers/HomingOutcome.cs`,
`coppercli.Core/Controllers/MillingController.cs`; rules `never-auto-clear-a-safety-gate`,
`machine-state-single-writer`; interfaces `controllers → machine`, `machine → GRBL`.

**Rule:** Never auto-clear a state that exists to require human confirmation. The absence of
a complaint is not evidence that a command took.
