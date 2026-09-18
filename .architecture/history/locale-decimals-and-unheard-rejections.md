# Comma decimals, and no listener for the rejection

**Problem:** On a decimal-comma locale the safety retract reported success while the tool
never moved, and milling continued.

**Cause:** Controller-generated G-code used interpolated `{value:F3}` formatting, which
follows the current culture. On German, French, Dutch, Spanish and most European and Latin
American locales every coordinate went out as `Z-1,000`, which GRBL rejects. Sends were
fire-and-forget, so nothing listened for the rejection. Two independent defects, lethal only
together.

**Fix:** Every generated line goes through `GCodeFormat.Inv`. GRBL rejections reach the
caller as a `CommandRejected` event, because waiting for Idle cannot detect one: a refused
command never leaves Idle, so the wait succeeds immediately. Numbers and dates now display
invariant regardless of the operator's locale, a side effect accepted deliberately. Fixed in
`4698964`. `coppercli.Core/Util/GCodeFormat.cs`,
`coppercli.Core/Communication/GrblRejection.cs`,
`coppercli.Tests/CultureInvariantGCodeTests.cs`; rules `culture-invariant-gcode`,
`fail-safe-on-uncertainty`; interface `machine → GRBL`.

**Rule:** Format numbers for GRBL with invariant decimals. Treat an offset write as applied only after GRBL accepts it.