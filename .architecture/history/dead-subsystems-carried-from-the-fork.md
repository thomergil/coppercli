# Dead OpenCNCPilot subsystems carried for six months

**Problem:** Three inherited subsystems that nothing called stayed in the tree for six
months, including an unguarded queue read in the serial worker's hot loop.

**Cause:** The fork kept OpenCNCPilot's `OperatingMode.SendMacro` path (about 180 lines
inside the serial worker, including that queue read), its `Calculator` expression evaluator,
and about 200 lines of 3-D vector geometry in `Vector3` — cross and dot product, rotations,
normalization, angle, interpolation — on the expectation that coppercli would need macros
and geometry. coppercli instead built a separate macro system at the application layer
(`.cmacro` files, `MacroParser`/`MacroRunner`, placeholders, `--macro`) in v0.3.0
(`f2a801b`, `2446e36`, `a7d1a15`) and never wired the inherited one up. Nothing could reach
the `SendMacro` mode, and nothing referenced the geometry beyond component-wise min/max and
magnitude, which is all a PCB height-map tool needs.

**Fix:** Deleted in `4698964`. `coppercli.Core/Util/Vector3.cs`,
`coppercli.Core/Communication/Machine.cs`, `coppercli.Core/Util/GrblCodeTranslator.cs`,
`coppercli/Macro/`.
Still open: `MachineSettings.FirmwareType` defaults to `"Grbl"`, `GrblCodeTranslator` reads
it, and the uCNC error/alarm/setting CSVs still ship in `coppercli.Core/Resources/`, but no
UI or CLI path sets it. Decide it or delete it.

**Rule:** When forking, delete aggressively. `CLAUDE.md` names `~/src/OpenCNCPilot/` as the
reference implementation to consult, which is where code you might one day want belongs.
