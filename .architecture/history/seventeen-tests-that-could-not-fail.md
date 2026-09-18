# Seventeen tests that could not fail, and a culture guard that never held

**Problem:** A real test failure caused by test ordering rather than by a defect in the code:
an expected `"0.000"` arrived as `"0,000"`. The audit that followed found seventeen tests that
no defect could break, and a guard test that missed the phase member whose collision with the
lifecycle produced the `ToolChange` overwrite.

**Cause:** `CultureInvariantGCodeTests` set `CultureInfo.DefaultThreadCurrentCulture` to
`de-DE` in its constructor and restored it in `Dispose`, carrying
`[Collection("culture-sensitive")]` and a comment saying that kept it from running alongside
other classes. No `[CollectionDefinition("culture-sensitive")]` existed anywhere in the
project, and xUnit silently ignores a collection name it cannot resolve, so the attribute was
inert and the comment was false. `DefaultThreadCurrentCulture` is process-wide, so every
other class running in parallel formatted numbers in German for the duration, and any test
asserting on a formatted number passed or failed by scheduling luck.
Six `*_EventCanBeSubscribed` tests subscribed a handler, never raised the event and asserted
the capture was still null, which a controller that raises no events would also satisfy. Two
`AllPhaseValues_AreValid` theories passed enum literals to `Enum.IsDefined`, which cannot
fail, because an undefined literal would not compile. `SetupGrid_WhenNotIdle_Throws` asserted
nothing. `PhaseEnums_ExcludeControllerStates` compared phase names against
`ControllerState` names by equality, so `WaitingForOperator` slipped past
`WaitingForUserInput` and `Complete` past `Completed`; it caught four of the six members the
phase sweep removed and missed `WaitingForOperator`.

**Fix:** The culture class sets only `CultureInfo.CurrentCulture`, which is per-thread and
flows across `await` with the execution context, so the guard still holds and the culture
change stays on the test's own thread; verified by mutation, since removing a
`GCodeFormat.Inv` still fails the test. The seventeen tests are removed or replaced with
tests that fire the event and assert on what arrives, and `SetupGrid_WhenNotIdle_Throws` now
starts the run and asserts the throw. `PhaseEnums_ExcludeControllerStates` matches a set
of lifecycle names plus their known synonyms and covers all three phase enums. Audited in the
session after `168392e`. `.architecture/rules/check-layering.sh` (two clauses of the new rule
are mechanized there), `coppercli.Tests/CultureInvariantGCodeTests.cs`,
`coppercli.Tests/ProbeControllerTests.cs`, `coppercli.Tests/ToolChangeControllerTests.cs`,
`coppercli.Tests/MachineWaitTests.cs`; rules `tests-detect-plausible-defects`,
`test-doubles-reproduce-grbl-responses`, `culture-invariant-gcode`.

**Rule:** Introduce a plausible defect to confirm that each test for a named rule fails. Test the behavior instead of one spelling of the code. Keep process-wide test state local to the thread; xUnit ignores an unresolvable `[Collection]` name.