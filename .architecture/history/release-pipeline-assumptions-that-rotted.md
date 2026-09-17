# Release-pipeline assumptions that rotted silently

**Problem:** CI saved a 10 KB HTML page as `is-setup.exe` and noticed only when
`Start-Process` tried to run it. The Intel macOS build stopped running. `Assembly.Location`
returned an empty string in every published release.

**Cause:** Three build-pipeline assumptions, each reasonable when written. The Inno Setup
download URL `jrsoftware.org/download.php/is.exe` now redirects to an HTML download page. The
`macos-13` runner was retired; the Intel build had already been added, dropped and re-added
once before that. `Assembly.Location` returns an empty string in a single-file app, which is
how releases are published; the code fell back to `AppContext.BaseDirectory`, so the fallback
was the only path that ever ran in a release build and the defect was invisible. Also found:
a PowerShell `Get-ChildItem -Path <dir-wildcard> -Filter ISCC.exe` lookup matched the filter
against directories rather than their contents, so it could never find anything. Commits
`bedb2d9`, `92f44e2`, `1d7913c`; the Intel-macOS round trip is `f2d9d63` → `43df67d` →
`7bc0e62`.

**Fix:** Use the Inno Setup 6.7.x preinstalled on the runner and download only as a genuine
fallback, pinning the release asset and verifying the downloaded bytes before executing them.
Stay on Inno Setup 6.x deliberately: 7 installs to a different directory. Locate tools by
searching rather than by hardcoding a version's path, and fail loudly naming the directories
that were found. Cross-compile Intel macOS from the ARM runner. Use
`AppContext.BaseDirectory`, never `Assembly.Location`. `.github/workflows/`, `installer/`,
`scripts/`, `coppercli/Helpers/Logger.cs`.

**Rule:** Never fetch a build dependency from a vendor's "latest" redirect.
