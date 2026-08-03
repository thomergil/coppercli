# 2026-08 — Release-pipeline assumptions that rotted silently

**Who:** Thomer. Commits `bedb2d9`, `92f44e2`, `1d7913c`; the Intel-macOS round trip is
`f2d9d63` → `43df67d` → `7bc0e62`.

**Tried:** Three build-pipeline assumptions, each reasonable when written: fetch Inno Setup
from `jrsoftware.org/download.php/is.exe`; build Intel macOS on the `macos-13` runner;
locate the running executable with `Assembly.Location`.

**Believed:** A vendor "latest download" URL is stable; a GitHub-hosted runner image stays
available; `Assembly.Location` returns the exe path.

**Realized:** The Inno URL now redirects to an HTML download page — CI saved a
10 KB web page as `is-setup.exe` and only noticed when `Start-Process` tried to run it.
`macos-13` was retired (the Intel build had already been added, dropped, and re-added once
before that). `Assembly.Location` returns an **empty string** in a single-file app, which is
exactly how releases are published; the code happened to fall back to
`AppContext.BaseDirectory`, so the fallback was the only path that ever ran in a release
build and the bug was invisible. Also found: a PowerShell `Get-ChildItem -Path <dir-wildcard>
-Filter ISCC.exe` lookup matched the filter against directories rather than their contents,
so it could never find anything.

**Lesson.** Never fetch a build dependency from a vendor's "latest" redirect — pin the
release asset and verify the downloaded bytes before executing them. Prefer what the runner
already ships (Inno Setup 6.7.x is preinstalled) and download only as a genuine fallback.
Stay on Inno Setup 6.x deliberately: 7 installs to a different directory. Locate tools by
searching rather than hardcoding a version's path, and fail loudly naming the directories
you *did* find. For Intel macOS, cross-compile from the ARM runner rather than depending on
a legacy runner image. Use `AppContext.BaseDirectory`, never `Assembly.Location`.

**Touches:** `.github/workflows/`, `installer/`, `scripts/`, `coppercli/Helpers/Logger.cs`.
