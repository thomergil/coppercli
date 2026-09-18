# The browser's status handler threw on every message

**Problem:** `updateJogButtons` threw a `ReferenceError` on every status message and
`websocket.js` logged it as "Failed to parse message". Everything after that line in
`updateStatus` stopped running: the Continue Milling button, the tool-change prompt recovery
a reloaded browser needs, the depth display and the mill grid. The door wording the change
existed to deliver was written to `getElementById('mill-status')`, an element `index.html`
has never had, so a lookup table, three text constants and a class constant fed a branch that
could not run; five more writes to absent ids were found the same way. 529 C# tests and
`check-layering.sh` were green.

**Cause:** The change decided every answer about the machine once, in `MachineWait`, and
shipped it as a value, replacing both UIs reading GRBL's status word and working out their
own answers. `jog.js` read a variable the same edit had deleted. The build compiles only the
C#, every test in the suite is C#, and the layering grep searches for what the browser must
not read, so none of them ran the browser's code. It follows
`door-state-checked-in-ten-places.md`, which named the three door states in Core but left the
web status publishing the door as three booleans.

**Fix:** `coppercli.Tests/browser/status.test.mjs` imports the real `screens.js` and `jog.js`
under `node --test` against a stub page and reads back what they wrote. Each test was checked
by making the defect it covers and confirming it failed. It runs in CI under a pinned Node
22, with no `package.json` and nothing to install. `page.test.mjs` asserts that every id the
code writes to exists on `index.html` and imports every module the page imports; rule
`browser-target-ids-exist`.
The first version of the browser suite had three holes of its own: its stub page created any
element the code asked for, so the id in the code and the id in the test always matched and
`index.html` was never read; `app.js` and `settings.js` were imported by no test, so a fault
in either would have taken down the whole UI with every check green; and every payload it
sent had `enabled: true`, so the branch that draws why a control is blocked, which runs
before the jog lockout, never executed. The stub now takes its ids from `index.html` and
throws on one the page lacks.
On the second pass the browser tests reached 31 of the page's 109 element ids, because the
payload they sent had seven fields and `updateStatus` skipped every branch behind `workPos`,
`file`, `probe` and the rest; the six writes to absent ids had been in those branches. Each
test also drew on a page built that moment, where a control starts enabled, so a `jog.js` in
which every disable was one-way passed all nine tests — on the machine that means the jog
buttons stay disabled after the door is closed until the page is reloaded. The payload now
carries every field the code branches on, and two tests draw a second status onto the same
page.
`check-layering.sh`'s adoption loop matched `public static bool`, so `GetActivity` returning
an enum was outside a rule added in the same change to catch that defect; it now matches any
return type, and neither a test nor a comment counts as a caller. It still matches by name,
so an unadopted overload of an adopted name passes; only a compiler can catch that, and the
rule says so. The script also asserted that four directories and `ARCHITECTURE.md` exist
before running, which four empty directories satisfied, so every grep matched nothing and it
exited 0 having checked no code; it now names each file a rule reads and requires it to be
non-empty. `node --test` on a glob that matches nothing also exits 0, so the CI step lists
the files first.
`ControllerBase.RequestUserInputAsync` now throws when nothing is subscribed, so the next
controller to prompt with no subscriber fails at the first prompt instead of hanging.
`coppercli.Core/Controllers/MachineActivity.cs`, `coppercli.Core/Controllers/MachineWait.cs`,
`coppercli.Core/Util/GrblProtocol.cs`, `coppercli.Core/Controllers/ProbeController.cs`,
`coppercli/WebServer/CncWebServer.cs`, `coppercli/WebServer/wwwroot/js/constants.js`,
`coppercli/WebServer/wwwroot/js/helpers.js`, `coppercli/WebServer/wwwroot/js/jog.js`,
`coppercli/WebServer/wwwroot/js/mill.js`, `coppercli/WebServer/wwwroot/js/screens.js`,
`coppercli/Menus/MillMenu.cs`, `coppercli/Menus/JogMenu.cs`, `coppercli/Menus/MainMenu.cs`,
`coppercli/Menus/ConnectionMenu.cs`, `coppercli/Macro/MacroRunner.cs`,
`coppercli/Helpers/DisplayHelpers.cs`, `coppercli/Helpers/MenuHelpers.cs`,
`coppercli/CliConstants.cs`, `coppercli.Tests/MachineWaitTests.cs`,
`coppercli.Tests/WebServerSequenceTests.cs`, `coppercli.Tests/FakeMachineDoorTests.cs`,
`coppercli.Tests/browser/`, `.architecture/rules/check-layering.sh`,
`.github/workflows/test.yml`; rules `browser-uses-core-status-values`,
`browser-target-ids-exist`, `new-state-cases-update-callers`,
`behavior-rules-require-behavior-tests`, `publish-shared-constants-through-api`, `tests-detect-plausible-defects`,
`no-magic-values`; interfaces `controllers → machine` v4, `machine → GRBL` v3,
`web → browser` v6.

**Rejected:**

- Checking a browser change from the server side. The payload test asserted that six fields
  were present. Nine mutations of their values were run against the full suite —
  `machineActivity` always `Idle`, `Running` shipped as `Hold`, `canPause` and `canResume`
  swapped, `needsAttention` inverted, the `connected` widening reverted — and eight passed.
  The one that failed was a lower-cased enum name, because `Enum.TryParse` is case-sensitive.
- Calling `EnsureDoorClosedAsync` from `ProbeController`. A door opened during a probe run
  made every wait give up, so the run failed with a message that never mentioned the door,
  and the fix that worked for milling and tool change looked like it applied. Only those two
  controllers had a subscriber for `UserInputRequired`; with none, `UserInputRequired?.Invoke`
  did nothing and the run waited on an answer that could not arrive, holding the serial port
  until the operator pressed Stop — worse than the failure it replaced. The call was removed.
  Superseded by `two-resume-windows-for-one-door.md`, which subscribed `UserInputRequired` in
  `ProbeMenu` and both probe start paths and restored the call; do not read this dead end as
  a reason to remove it again.

**Rule:** Run the browser modules in CI. C# tests cannot detect exceptions in browser code.
