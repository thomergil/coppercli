# The test project had not compiled since February

**Problem:** About 169 unit tests landed with the controller layer in v0.4.0 (`5780ca8`) --
`ControllerBaseTests`, `MachineWaitTests`, `ProbeControllerTests`,
`ToolChangeControllerTests`, plus `FakeMachine` and `MockMachine`. None of them executed for
five months, the project's entire dormant period.

**Cause:** The test doubles were missing three `IMachine` members added when the controller
layer landed, so the test project stopped building on 2026-02-03, and nothing invoked
`dotnet test`. When the doubles were repaired, `FakeMachine` turned out to discard every
`G53` command and to model no work offsets, so retracts and offset writes reported success
without being simulated — the safety behavior the tests existed to cover.

**Fix:** Repaired in `4698964`. The test project is built with warnings-as-errors on every
push and the release build has a test step. `coppercli.Tests/Fakes/FakeMachine.cs`,
`coppercli.Tests/Fakes/MockMachine.cs`, `.github/workflows/`; interface
`controllers → machine`.

**Rule:** Run the C# test project in CI. Test doubles must model the commands whose effects the tests assert.