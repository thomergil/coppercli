# 2026-08 — A test suite that had not compiled since February

**Who:** Thomer. Tests introduced with the controller layer in v0.4.0 (`5780ca8`);
discovered and repaired in `4698964`.

**Tried:** v0.4.0 landed ~169 unit tests alongside the controller layer —
`ControllerBaseTests`, `MachineWaitTests`, `ProbeControllerTests`,
`ToolChangeControllerTests`, plus `FakeMachine` and `MockMachine`. No CI ran them.

**Believed:** Having the tests in the tree meant they were being run.

**Realized:** The test doubles were missing three `IMachine` members added when the
controller layer landed, so the test project stopped **building** on 2026-02-03 — and
nothing invoked `dotnet test`. Not one of those tests executed for five months, the
project's entire dormant period. When they were fixed, the doubles turned out to be lying
too: `FakeMachine` silently discarded every `G53` command and never modelled work offsets,
so retracts and offset writes "succeeded" without being simulated — the safety
behavior the tests existed to cover.

**Lesson.** A test that is not run by CI does not exist. Build the test project with
warnings-as-errors on every push and add a test step to the release build. And audit the
fakes: a double that no-ops the exact commands under test converts a red suite into a green
one, which is worse than having no suite at all.

**Touches:** seam `controllers → machine`, `coppercli.Tests/Fakes/FakeMachine.cs`,
`coppercli.Tests/Fakes/MockMachine.cs`, `.github/workflows/`.
