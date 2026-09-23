# The browser asked its own session questions, because the list of topics already asked lived only in the terminal's loop

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

**Rule:** `session-questions-cleared-by-their-answer`.
