# 2026-09 — Seventeen tests that could not fail, and a culture guard that never held

**Who:** Thomer, with Claude Opus 5, in the session after `168392e`, auditing the suite
after a real failure caused by test ordering rather than by a defect in the code.

**Tried:** `CultureInvariantGCodeTests` set `CultureInfo.DefaultThreadCurrentCulture` to
`de-DE` in its constructor and restored it in `Dispose`, carrying
`[Collection("culture-sensitive")]` and a comment saying that kept it from running
alongside other classes.

**Believed:** the collection attribute serialized the class against the rest of the suite,
so a process-wide culture change was contained.

**Realized:** no `[CollectionDefinition("culture-sensitive")]` existed anywhere in the
project. xUnit silently ignores a collection name it cannot resolve, so the attribute was
inert and the comment was false. `DefaultThreadCurrentCulture` is process-wide, so every other class
running in parallel formatted numbers in German for the duration, and any test asserting on
a formatted number passed or failed by scheduling luck. It cost one real failure, an
expected `"0.000"` arriving as `"0,000"`. The class now sets only
`CultureInfo.CurrentCulture`, which is per-thread and flows across `await` with the
execution context — the guard still holds, and the culture change stays on the test's own
thread. Verified by mutation:
removing a `GCodeFormat.Inv` still fails the test.

The audit that followed found seventeen tests that no defect could break. Six
`*_EventCanBeSubscribed` tests subscribed a handler, never raised the event, and asserted
the capture was still null — which a controller that raises no events would also satisfy.
Two `AllPhaseValues_AreValid` theories passed enum literals to `Enum.IsDefined`, which cannot
fail, because an undefined literal would not compile. `SetupGrid_WhenNotIdle_Throws`
asserted nothing. All are now removed or replaced with tests that fire the event
and assert on what arrives, and `SetupGrid_WhenNotIdle_Throws` now starts the run and
asserts the throw.

**And a guard test that matched on spelling.** `PhaseEnums_DoNotRestateTheRunLifecycle`
compared phase names against `ControllerState` names by equality, so `WaitingForOperator`
slipped past `WaitingForUserInput` and `Complete` past `Completed`. It caught four of the six
members the phase sweep removed, and `WaitingForOperator` — the one whose collision with the
lifecycle produced the `ToolChange` overwrite — was among the two it missed. It now matches a
set of lifecycle names plus their known synonyms, and covers all three phase enums.

**Lesson → new rule `a-test-must-be-able-to-fail`.** A test earns its place only if some
plausible defect makes it fail; where it guards a named rule, prove that by mutation before
trusting it. A guard test matches the meaning the rule is about, not the spelling the rule
happened to be written against. And a test that mutates process-wide state is a hazard to
every other test, so scope the mutation to the thread. With xUnit, an unresolvable
`[Collection]` name is ignored without warning, so the attribute is never evidence of
isolation.

**Touches:** rules `a-test-must-be-able-to-fail`, `fake-answers-like-the-machine`,
`culture-invariant-gcode`, `.architecture/rules/check-layering.sh` (two clauses of the new
rule are mechanized there), `coppercli.Tests/CultureInvariantGCodeTests.cs`,
`coppercli.Tests/ProbeControllerTests.cs`, `coppercli.Tests/ToolChangeControllerTests.cs`,
`coppercli.Tests/MachineWaitTests.cs`.
