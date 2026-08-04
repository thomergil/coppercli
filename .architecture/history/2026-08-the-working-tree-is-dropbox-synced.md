# 2026-08 — Dropbox reverted edits that had already been verified and built

**Who:** Observed by Claude Opus 5 in Thomer's working copy, in the session after `eecebf4`.

**What happened:** Five edited call sites in `Menus/ProbeMenu.cs` and
`WebServer/CncWebServer.cs` reverted themselves roughly 40 minutes after they were written,
read back, and built green. Conflicted copies appeared under `coppercli/bin/Debug/net8.0/`.

**Why (provisional — the exact trigger was not established):** the tree is a Dropbox-synced
folder, and a sync arriving over a local write is the only mechanism that fits. Build output
churning under the same folder gives the sync plenty to clash over, which is what the
conflicted copies point at. This is the hazard `CLAUDE.md` is already guarding when it opens
by telling agents to delete conflicted copies and warning against compiling unasked; what was
not written down is that *source files already saved and verified can disappear*.

**Lesson.** On a long session in this tree, confirming an edit landed says nothing about ten
minutes later. Build from a copy outside the synced folder (`/tmp` works) and keep uncommitted
work backed up outside Dropbox. And when code you know you wrote is gone, suspect the sync
before you suspect yourself: "you must not have made that edit" is as wrong a conclusion here
as "you forgot to compile".

**Touches:** working environment; no seam, no rule.
