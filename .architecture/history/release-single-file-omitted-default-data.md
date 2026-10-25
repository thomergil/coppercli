# The single-file downloads omitted default data

**Date:** 2026-10-25 · **Found by:** 0.6.0 release check, from the existing build → release
artifact GAP.

**Cause:** `dotnet publish` copied the GRBL code tables and `machine-profiles.yaml` beside
the executable. The GitHub workflow archived only the executable for macOS and Linux and
uploaded only the executable for Windows portable. The installer copied the full publish
directory and was unaffected.

**Decision:** embed the three GRBL tables in Core and the default machine profiles in the
app. Keep the external `machine-profiles.yaml` as an override for custom profiles. The asset
names and one-file downloads stay the same.

**Evidence:** both `ReleaseResourceTests` failed before embedding and passed afterward. A
published Linux ARM64 executable copied alone to an empty directory served ten built-in
profiles, including `nomad3`, from `/api/profiles`.

**Lesson:** a single-file publish can still leave content files beside the executable.
Check the contents of the downloaded asset, not only the publish directory. Assert that
required defaults are embedded and smoke-test the bare executable.

**Rule:** build → release artifact v2; `single-file-assets-carry-default-data`.

Touches: `coppercli.Core/coppercli.Core.csproj`, `coppercli/coppercli.csproj`,
`MachineProfiles.Load`, `ReleaseResourceTests`.
