# 2026-09 — A readiness gate that refused what the controller could release

**Who:** Thomer, with Claude Opus 5, reporting that Mill refused to start after the
enclosure had been opened and closed: "it just says Machine is not ready".

**Tried:** `MillMenu.Show()` and `CncWebServer.StartMilling` each ran
`MachineCommands.EnsureMachineReady` before starting `MillingController`. That helper waits
for Idle and counts `Door` as problematic, so it refused with "Machine did not settle. Wait
for it to stop moving, clear any alarm, then try again."

**Believed:** an open door blocks the start, the operator closes it, and they start again.
The comment on `EnsureMachineReadyAsync` said exactly that.

**Realized:** closing the door does not end the hold. GRBL parks, then sits in `Door` with
the substate reading closed until a cycle start arrives — confirmed in grbl 1.1's
`protocol.c`, where the ajar flag is cleared inside `while (sys.suspend)` from a live read of
the switch, and in `report.c`, where `Door:0` means closed and ready to resume. So starting
again met the same refusal every time, and none of the three things the message told the
operator to do could help: the machine was not moving, there was no alarm, and nothing in
that path sent a cycle start.

`MillingController.EnsureDoorClosedAsync` already handled this correctly — it asks the
operator and then releases the hold — but it ran inside `RunAsync`, behind the gate, so it
was never reached. The gate was written the day before that handler and never removed when
the handler superseded it.

Fixed by deleting the gate from both UIs. The controller answers whether the machine's state
allows a job; the UIs keep `CheckMillCanStart`, which answers whether the *job* is fit to
run, and the alarm case moved into that one mapping.

Two more defects had the same cause. The door prompt now lives on `ControllerBase`,
because `ToolChangeController` was releasing a door hold on the strength of an answer to
"change the tool and press Continue", a question that never mentioned the enclosure.
And `MachineWait.ReleaseDoorHoldAsync` waits only long enough for GRBL's reading of the
switch to catch up with the operator's answer, not for the door to be closed: waiting out a
door that is really open would leave the cycle start pending, so the machine would start
later, unattended.

**Lesson → rule `machine-readiness-is-the-controllers`.** When a new handler supersedes an
older check, delete the older one: left in front, it refuses first and the handler behind it
is never reached. And a refusal must name a state the operator can act on: "not ready"
covering an open door, a closed door still holding, an alarm and a moving machine gives them
nothing to do. Each of those four now has its own sentence, and `DescribeNotReady` tells the
first two apart.

**Touches:** rule `machine-readiness-is-the-controllers`, `controllers → machine` (v2 → v3),
`coppercli.Core/Controllers/MachineWait.cs`, `coppercli.Core/Controllers/ControllerBase.cs`,
`coppercli.Core/Controllers/MillingController.cs`,
`coppercli.Core/Controllers/ToolChangeController.cs`, `coppercli/Menus/MillMenu.cs`,
`coppercli/Menus/ConnectionMenu.cs`, `coppercli/Menus/JogMenu.cs`,
`coppercli/WebServer/CncWebServer.cs`, `coppercli/Helpers/MenuHelpers.cs`,
`coppercli.Core/Util/GrblProtocol.cs`, `coppercli/CliConstants.cs`,
`coppercli/WebServer/wwwroot/js/screens.js`, `coppercli.Core/Controllers/DoorState.cs`.
