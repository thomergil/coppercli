# A readiness check refused a closed-door hold

**Problem:** Mill refused to start after the enclosure had been opened and closed, reporting
"Machine did not settle. Wait for it to stop moving, clear any alarm, then try again." None
of the three actions the message named could help: the machine was not moving, there was no
alarm, and nothing in that path sent a cycle start.

**Cause:** `MillMenu.Show()` and `CncWebServer.StartMilling` each ran
`MachineCommands.EnsureMachineReady` before starting `MillingController`. That helper waits
for Idle and counts `Door` as problematic. Closing the door does not end the hold: GRBL
parks, then sits in `Door` with the substate reading closed until a cycle start arrives,
confirmed in grbl 1.1's `protocol.c`, where the ajar flag is cleared inside
`while (sys.suspend)` from a live read of the switch, and in `report.c`, where `Door:0` means
closed and ready to resume. `MillingController.EnsureDoorClosedAsync` already asked the
operator and released the hold, but it ran inside `RunAsync`, behind the gate, so it was
never reached. The gate was written the day before that handler and never removed when the
handler superseded it.

**Fix:** The gate was deleted from both UIs. The controller answers whether the machine's
state allows a job; the UIs keep `CheckMillCanStart`, which answers whether the job is fit to
run, and the alarm case moved into that mapping. Each of the four refusal cases — open door,
closed door still holding, alarm, moving machine — has its own sentence, and
`DescribeNotReady` tells the first two apart. The door prompt moved to `ControllerBase`,
because `ToolChangeController` had been releasing a door hold on the strength of an answer to
"change the tool and press Continue", a question that never mentions the enclosure.
`MachineWait.ReleaseDoorHoldAsync` waits only long enough for GRBL's reading of the switch to
catch up with the operator's answer, not for the door to be closed: waiting out a door that
is really open would leave the cycle start pending, so the machine would start later,
unattended. `coppercli.Core/Controllers/MachineWait.cs`,
`coppercli.Core/Controllers/ControllerBase.cs`,
`coppercli.Core/Controllers/MillingController.cs`,
`coppercli.Core/Controllers/ToolChangeController.cs`, `coppercli/Menus/MillMenu.cs`,
`coppercli/Menus/ConnectionMenu.cs`, `coppercli/Menus/JogMenu.cs`,
`coppercli/WebServer/CncWebServer.cs`, `coppercli/Helpers/MenuHelpers.cs`,
`coppercli.Core/Util/GrblProtocol.cs`, `coppercli/CliConstants.cs`,
`coppercli/WebServer/wwwroot/js/screens.js`, `coppercli.Core/Controllers/DoorState.cs`; rule
`controllers-check-machine-readiness`; interface `controllers → machine` v2 → v3.

**Rule:** When a new handler supersedes an older check, delete the older one: left in front,
it refuses first and the handler behind it is never reached. A refusal must name a state the
operator can act on.
