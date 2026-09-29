# MockMachine follows moves, and a /tmp build kept a stale DLL

**Date:** 2026-09-29

**Context:** Testing `ControllerBase.MoveAndConfirmAsync` (see
`trace-confirmed-moves-by-idle`) needed a double whose position follows the moves it is sent.

**Test-double facts:**
- `MockMachine` follows `G0`/`G1` on every axis, in the frame the line names: `G53` moves
  `MachinePosition`, any other move moves `WorkPosition`. The two positions do not track
  each other; this is deliberate.
- It applies a move *before* `LineSent` fires. A test that must see the position before the
  move lands hooks `AnswerTo` instead.
- A move received while `DoorModel.Holding` (Door or Hold) is dropped, not queued.
- The three fakes read axis words through one parser, `Fakes/GCodeWords.Axis`.

**Build fact:** `rsync` of `/src` to a `/tmp` copy keeps the source files' older mtimes, so an
incremental `dotnet build` there can keep a stale DLL, for example after reverting a
mutation. Mutation tests in a copy build with `--no-incremental`.

**Lesson:** A test that checks position at the moment a line is sent must hook `AnswerTo`,
not `LineSent`. A mutation result from an incremental build in a copied tree is not
evidence until it is rebuilt with `--no-incremental`.

**Rule:** `test-doubles-reproduce-grbl-responses`, `tests-detect-plausible-defects`.

Touches: `coppercli.Tests/Fakes/MockMachine.cs`, `coppercli.Tests/Fakes/FakeMachine.cs`,
`coppercli.Tests/Fakes/FakeGrbl.cs`, `coppercli.Tests/Fakes/GCodeWords.cs`,
`coppercli.Tests/Fakes/DoorModel.cs`.
