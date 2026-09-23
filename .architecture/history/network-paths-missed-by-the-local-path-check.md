# The local-path check let three spellings of a network share through

**Date:** 2026-09-22

**Problem:** `CncWebServer.IsLocalPath` refused only paths starting `\\` or `//`. On
Windows, `\/host`, `/\host` and `\??\UNC\host\share` also reach a network share. The
operator runs coppercli on Windows.

**Cause:** The check listed the spellings its author knew instead of the rule Windows
applies: any two leading separators, in any mix, name a host, and a separator followed by
`?` names a device path.

**Why it matters:** opening a share sends the Windows login to that host. A path the
browser saved is remembered (`SessionState.LastProbeFile`) and read again on every
session-question pass, so one bad path sends the login to that host on every pass.

**Fix:** `IsLocalPath` refuses any two leading separators and a separator followed by `?`.
`WebServerSequenceTests.APathOnAnotherHost_IsRefused` covers all six forms.

**Lesson:** Check a path against the rule the OS uses to parse it, not a list of examples.
Check it where the browser supplies it, because a remembered path is read again later
without a check.

Touches: `web → browser` (the whole-filesystem DECISION), `CncWebServer.IsLocalPath`,
`SessionState.LastProbeFile`.
