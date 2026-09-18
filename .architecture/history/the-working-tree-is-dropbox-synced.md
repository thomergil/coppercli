# Dropbox reverted edits that had been verified and built

**Problem:** Five edited call sites in `coppercli/Menus/ProbeMenu.cs` and
`coppercli/WebServer/CncWebServer.cs` reverted themselves about 40 minutes after they were
written, read back and built green. Conflicted copies appeared under
`coppercli/bin/Debug/net8.0/`.

**Cause:** Provisional; the exact trigger was not established. The tree is a Dropbox-synced
folder, and a sync arriving over a local write is the only mechanism that fits. Build output
churning under the same folder gives the sync more to clash over, which is what the
conflicted copies point at.

**Fix:** None in code. Build from a copy outside the synced folder (`/tmp` works) and keep
uncommitted work backed up outside Dropbox. `CLAUDE.md` already tells agents to delete
conflicted copies and not to compile unasked; what it did not say is that source files
already saved and verified can disappear.

**Rule:** Recheck edited files after synchronization during long sessions. If an edit disappears, inspect Dropbox before editing the same file again.