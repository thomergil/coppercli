# 2026-08 — Dead OpenCNCPilot subsystems carried for six months

**Who:** Thomer, with Claude Opus 4.8, in `4698964`. The replacement macro system landed in
v0.3.0 (`f2a801b`, `2446e36`, `a7d1a15`).

**Tried:** The fork kept OpenCNCPilot's `OperatingMode.SendMacro` path (~180 lines inside
the serial worker, including an unguarded queue read), its `Calculator` expression
evaluator, and ~200 lines of 3-D vector geometry in `Vector3` — cross and dot product,
rotations, normalization, angle, interpolation.

**Believed:** Inherited infrastructure worth keeping; coppercli was going to need macros
and geometry.

**Realized:** coppercli built a separate macro system at the application layer
(`.cmacro` files, `MacroParser`/`MacroRunner`, placeholders, `--macro`) and never wired the
inherited one up. Nothing could reach the `SendMacro` mode. Nothing referenced the geometry
beyond component-wise min/max and magnitude, which is all a PCB height-map tool ever needs.
The compiler confirmed it six months later.

**Lesson.** When forking, delete aggressively. `CLAUDE.md` already points at
`~/src/OpenCNCPilot/` as the reference implementation to *consult*, and that is the right
place for code you might one day want. Carried-over code that nothing calls is not free:
this code included an unguarded queue read in the hot serial loop.

**Still latent:** `MachineSettings.FirmwareType` defaults to `"Grbl"`,
`GrblCodeTranslator` reads it, and the uCNC error/alarm/setting CSVs still ship in
`coppercli.Core/Resources/` — but no UI or CLI path ever sets it. Inherited scaffolding for
a firmware target that was never wired up. Decide it or delete it; do not leave a third
piece of dead fork.

**Touches:** `coppercli.Core/Util/Vector3.cs`, `coppercli.Core/Communication/Machine.cs`,
`coppercli.Core/Util/GrblCodeTranslator.cs`, `coppercli/Macro/`.
