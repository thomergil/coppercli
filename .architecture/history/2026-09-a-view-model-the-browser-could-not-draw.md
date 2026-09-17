# 2026-09 — A view model the browser could not draw

**Who:** Thomer, with Claude Opus 5, after `2026-09-a-two-state-door-in-ten-places` named
the three door states in Core but left the web status publishing the door as three booleans.

**Tried:** decide every answer about the machine once, in `MachineWait`, and ship it as a
value. Before this, both UIs read GRBL's status word and worked out their own answers.

**Believed:** the change was finished when the payload carried the answers, the browser read
them, and 529 tests plus `check-layering.sh` were green.

**Realized:** none of those checks ran the browser's code. `jog.js` read a variable the same
edit had deleted, so `updateJogButtons` threw a `ReferenceError` on every status message and
`websocket.js` logged it as "Failed to parse message". Everything after that line in
`updateStatus` stopped running: the Continue Milling button, the tool-change prompt recovery
a reloaded browser needs, the depth display, the mill grid. The build compiles only the C#,
every test in the suite is C#, and the layering grep searches for what the browser must not
read, so none of them could see it.

The door wording this change existed to deliver was written to
`getElementById('mill-status')`, an element `index.html` has never had. A lookup table, three
text constants and a class constant fed a branch that could not run. Five more writes to
absent ids were found the same way.

**Dead end: checking a browser change from the server side.** The payload test asserted that
six fields were present. Nine mutations of their values were run against the full suite —
`machineActivity` always `Idle`, `Running` shipped as `Hold`, `canPause` and `canResume`
swapped, `needsAttention` inverted, the `connected` widening reverted — and eight passed. The
one that failed was a lower-cased enum name, because `Enum.TryParse` is case-sensitive.

`coppercli.Tests/browser/status.test.mjs` replaces it: tests that import the real
`screens.js` and `jog.js` under `node --test` against a stub page and read back what they
wrote. Each was checked by making the defect it covers and confirming it failed. It runs in
CI under a pinned Node 22, with no `package.json` and nothing to install.

**Lesson:** run the browser's code in a check, or the C# checks will pass over a browser
defect. Nothing asserted that an id the code writes to exists on the page either;
`an-element-the-code-writes-to-exists` and `page.test.mjs` now do.

The first version of the browser suite had three holes of its own. Its stub page created any
element the code asked for, so the id in the code and the id in the test always matched and
`index.html` was never read. `app.js` and `settings.js` were not imported by any test, so a
fault in either would have taken down the whole UI with every check green. And every payload
it sent had `enabled: true`, so the branch that draws why a control is blocked — which runs
before the jog lockout — never executed. The stub now takes its ids from `index.html` and
throws on one the page lacks, and `page.test.mjs` imports every module the page imports.

**Also learned:** `check-layering.sh`'s adoption loop matched `public static bool`, so
`GetActivity` returning an enum was outside a rule added in the same change to catch
that defect. It now matches any return type, and neither a test nor a comment counts as a
caller. It still matches by name, so an unadopted overload of an adopted name passes; only a
compiler can catch that, and the rule says so.

**Also learned, on the second pass:** the browser tests reached 31 of the page's 109 element
ids, because the payload they sent had seven fields and `updateStatus` skipped every branch
behind `workPos`, `file`, `probe` and the rest. The six writes to absent ids had been in
those branches. Each test also drew on a page built that moment, where a control starts
enabled, so a `jog.js` in which every disable was one-way passed all nine tests — on the
machine that means the jog buttons stay disabled after the door is closed until the page is
reloaded. The payload now carries every field the code branches on, and two tests draw a
second status onto the same page.

**Also learned, about the gates themselves:** `check-layering.sh` asserted that four
directories and `ARCHITECTURE.md` exist before running, but four empty directories satisfied
that and every grep then matched nothing, so the script exited 0 having checked no code. It
now names each file a rule reads and requires it to be non-empty. `node --test` on a glob
that matches nothing also exits 0, so the CI step lists the files first.

**Dead end: adding a prompt to a controller no front end listens to.** A door opened during
a probe run made every wait give up, so the run failed with a message that never mentioned
the door. Calling `EnsureDoorClosedAsync` from `ProbeController` looked like the same fix
that worked for milling and tool change, but only those two controllers have a subscriber
for `UserInputRequired`. With none, `UserInputRequired?.Invoke` did nothing and the run
waited on an answer that could not arrive, holding the serial port until the operator
pressed Stop — worse than the failure it replaced. The call was removed.

`ControllerBase.RequestUserInputAsync` now throws when nothing is subscribed, so the next
controller to make this mistake fails at the first prompt instead of hanging. Delivering the
probe prompt needs it wired into `ProbeMenu` and both probe start paths in the web server,
which is its own change.

**Superseded by 2026-09-two-resume-windows-one-door.** That change did the wiring: `ProbeMenu`
and both probe start paths subscribe `UserInputRequired`, and `EnsureDoorClosedAsync` prompts
only for the one door state a cycle start can end, publishing the other two as progress. The
probe run calls it. Do not read the dead end above as a reason to remove that call.

**Touches:** interfaces `controllers → machine` v4, `machine → GRBL` v3, `web → browser` v6, rules
`the-browser-draws-what-it-was-handed` (new), `an-element-the-code-writes-to-exists` (new),
`a-new-distinction-lands-with-its-callers`, `a-grep-is-not-the-guard`,
`shared-constants-flow-through-api`, `a-test-must-be-able-to-fail`, `no-magic-values`,
`coppercli.Core/Controllers/MachineActivity.cs`,
`coppercli.Core/Controllers/MachineWait.cs`, `coppercli.Core/Util/GrblProtocol.cs`,
`coppercli.Core/Controllers/ProbeController.cs`,
`coppercli/WebServer/CncWebServer.cs`, `coppercli/WebServer/wwwroot/js/constants.js`,
`coppercli/WebServer/wwwroot/js/helpers.js`, `coppercli/WebServer/wwwroot/js/jog.js`,
`coppercli/WebServer/wwwroot/js/mill.js`, `coppercli/WebServer/wwwroot/js/screens.js`,
`coppercli/Menus/MillMenu.cs`, `coppercli/Menus/JogMenu.cs`, `coppercli/Menus/MainMenu.cs`,
`coppercli/Menus/ConnectionMenu.cs`, `coppercli/Macro/MacroRunner.cs`,
`coppercli/Helpers/DisplayHelpers.cs`, `coppercli/Helpers/MenuHelpers.cs`,
`coppercli/CliConstants.cs`, `coppercli.Tests/MachineWaitTests.cs`,
`coppercli.Tests/WebServerSequenceTests.cs`, `coppercli.Tests/FakeMachineDoorTests.cs`,
`coppercli.Tests/browser/`, `.architecture/rules/check-layering.sh`,
`.github/workflows/test.yml`.
