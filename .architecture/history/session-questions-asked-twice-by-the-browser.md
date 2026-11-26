# Session questions: the browser asked its own, and a refusal reported an answer that was never given

**Problem:** The browser never called `/api/session/restore`. It asked two startup questions
in its own windows (trust the work origin; keep or save an autosaved map) and never the file
or saved-map ones. It decided from status fields when to ask, so a declined origin or an
adopted map came back on every page load, and two tabs could answer one question differently.

**Cause:** The terminal's loop remembered which topics it had asked, because three answers
left their question's state unchanged (`startup-prompt-loop-never-terminated.md`). That
memory lived in one terminal pass, so a browser re-reading the list would repeat every
question; the browser got its own copies of the conditions instead.

**Fix:** Each condition in `SessionRestore.GetPendingSteps` is now made false by its own
answer:
- file: yes loads it, no forgets the path;
- work origin: yes sets it, no clears the stored-origin flag;
- autosave: yes adopts the map, no deletes it;
- saved map: yes adopts and applies it, no forgets the path.

`SessionRestore.Answer` acts only on a pending question with the topic and detail shown, under
one lock; the endpoint refuses a missing field or unknown topic. A failed answer stays pending
and is skipped for the rest of the pass. The browser's `session-restore.js` asks the pending
questions one at a time on page open, reconnect and G-code load, reruns once for a trigger
that arrives mid-pass, and never asks while another question is open. A question replaced
before it is answered sends nothing, because "no" to an autosaved map deletes it. The
trust-zero window and endpoint, the startup probe-save and probe-recovery windows, and the
status fields only they read were removed.

**Rejected:**
- An asked set in the browser, or per client on the server: a second record of what the
  conditions already say.
- Keeping the browser's own windows beside the shared list.
- Matching an answer by topic alone: a stale "no" would delete whatever map was pending on
  that topic by then, not the one the operator saw.
- Ending the pass on a failed answer: one question that always fails would hold back the rest.

## A refusal must name its real cause (2026-11-26)

**Problem:** A single Windows terminal at startup answered "Reload last file?" and got
`ErrorQuestionAlreadyAnswered`. In terminal mode nothing else can answer, load a file or
change `LastLoadedGCodeFile`. The G-code file sat in a Dropbox-synced folder and had been
regenerated minutes earlier, so `File.Exists` returned true when the
question was shown and false when it was answered.

**Wrong belief:** a question vanishes between showing and answering only because an answer,
or a change the program made, settled it. The conditions also read inputs no answer controls:
the G-code file and the saved map file on disk, and `Machine.Connected` for the work origin.

**Fix:** `SessionRestore.ReadQuestions()` reads every condition in one pass and returns the
pending steps plus `UnavailableQuestion`s (topic, `MatchesShownDetail`, refusal text).
`GetPendingSteps` wraps it. A question is recorded as unavailable only while its own
answer-cleared condition still holds and only the outside check (file exists, connected)
fails. `Answer` takes both the pending check and the refusal from that one pass, under
`AnswerLock`. Refusals: `ErrorSessionFileGone` (names the file), `ErrorSessionDisconnected`,
otherwise `ErrorQuestionAlreadyAnswered`. The browser shows the 409 `error` text unchanged.
- ReloadFile and work origin match the exact shown detail.
- The saved map's detail is built from the map file; when that file is gone, the answer is
  matched by the prefix `string.Format(SavedHeightMapDetail, mapName, "")`. `{1}` must stay
  last in `SavedHeightMapDetail` (noted on the constant); if it moves, the prefix never
  matches and the refusal falls back to `ErrorQuestionAlreadyAnswered`.
- `AppState.SavedProbeFileToOffer` is the one guard for the saved-map offer (G-code loaded,
  no map adopted, a path named); `ReadUsableSavedProbeGrid(path)` reads the map at the path that guard returns, and
  `ApplySavedMap` adopts the map from that same path.

**Rejected:**
- A separate `WhatWentAway(topic)` table beside the conditions: it was a second definition, read the disk twice, and could name a file the operator was
  never asked about (a stale detail, or a question already answered elsewhere whose file was
  deleted later).
- A nullable detail meaning "match any" for the saved map: another tab loading then
  discarding a different map could make it name the wrong file.
- Dropping `File.Exists` from the reload offer: it would offer a question whose yes can only
  fail.
- Switching the work-origin check to `MachineWait.IsResponding`: the question would not be asked until
  GRBL's first status report. It stays on `Machine.Connected`.

**Rule:** `session-questions-cleared-by-their-answer`.

**Touches:** rule `session-questions-cleared-by-their-answer`, rule
`derived-artifact-records-its-context`, interface `web → browser`,
`coppercli/SessionRestore.cs`, `coppercli/AppState.cs`, `coppercli/CliConstants.cs`,
`coppercli/WebServer/wwwroot/js/session-restore.js`, `coppercli.Tests/SessionRestoreTests.cs`.
