# The terminal's apply question repeated what the session pass had settled

**Date:** 2026-09-22

**Problem:** After the session questions moved to one shared list, the File menu still asked
"Apply the existing height map?" on its own. It asked about a map the session pass had just
applied, and it offered a map whose discard had failed.

**Fix:** The File menu runs the session pass first, then asks the apply question only for a
map that is adopted and not applied (`AppState.HasCompleteMapNotApplied`). The pass settles
a saved map or an unadopted autosave; the apply question covers only what the pass leaves.

**Browser side:** `showConfirm` resolves `null`, not `false`, when another question replaces
it. `session-restore.js` sends nothing for `null`. Returning `false` there would send "no",
and "no" to an autosaved map deletes it.

**Lesson:** A front end that asks its own question about a fact on the shared list must ask
it after the pass, and only about what the pass left unsettled. Send nothing for a question
that another question replaced before it was answered.

**Rule:** `session-questions-cleared-by-their-answer`.

Touches: `coppercli/Menus/FileMenu.cs`, `wwwroot/js/session-restore.js`, `showConfirm`.
