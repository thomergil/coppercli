# Architecture Contract — v1

> Binding. The conforming pass treats every clause here as law. Changing a contract
> bumps its version and adds a History entry under `.architecture/history/`.

coppercli drives a GRBL CNC mill over a serial port to cut PCBs. Two things follow from
that: **a wrong move breaks a drill bit or ruins the copper**,
so correctness outranks convenience; and **the operator is standing at the machine**,
often holding a phone, so anything that costs them a keystroke at the wrong moment is a
design defect.

Derived from [OpenCNCPilot](https://github.com/martin2250/OpenCNCPilot) — consult it for
GRBL communication and probing questions. It has no tool-change (M6) support, so
coppercli's tool-change logic has no reference implementation.

## Parts

- **core-comm** (`coppercli.Core/Communication/`) — owns the serial link and the machine's
  live state. `Machine` parses GRBL status reports, queues commands, streams files;
  `SerialProxy` bridges the same serial port to a TCP listener. Knows nothing of any UI.
- **core-controllers** (`coppercli.Core/Controllers/`) — owns every multi-step machine
  workflow as an explicit FSM: `ProbeController`, `MillingController`,
  `ToolChangeController`, over `ControllerBase`. `MachineWait` holds every status predicate
  and every wait/poll loop. Controllers emit events; they never render.
- **core-gcode** (`coppercli.Core/GCode/`) — parses, models, and regenerates toolpaths.
  `GCodeParser`, `GCodeFile`, `ProbeGrid`, `ProbeContext`. Pure computation, no I/O.
- **core-util** (`coppercli.Core/Util/`) — `Constants`, `GrblProtocol`, `GCodeFormat`,
  `Vector3`/`Vector2`, `AtomicFile`, `GrblCodeTranslator`.
- **core-settings** (`coppercli.Core/Settings/`) — `MachineSettings`, `SessionState`. Plain
  serializable DTOs, no behavior.
- **app-state** (`coppercli/AppState.cs`, `Persistence.cs`, `SessionRestore.cs`,
  `Program.cs`) — process-wide composition root: the live `Machine`, the lazily-built
  controllers, the loaded file and probe grid, and the only code that touches the app's
  own files on disk.
- **tui** (`coppercli/Menus/`, `coppercli/Helpers/`) — the Spectre.Console keyboard UI.
  Presentation and input only; delegates work to controllers.
- **macro** (`coppercli/Macro/`) — parses and runs `.cmacro` scripts, driving the TUI's
  own operations unattended.
- **web-server** (`coppercli/WebServer/CncWebServer.cs`, `WebConstants.cs`,
  `RequestGuard.cs`) — embedded `HttpListener` serving the browser UI, the `/api/*` surface,
  and the `/ws` socket. Presentation and transport; delegates work to controllers.
  *Finding: at ~3.7k lines this is the one part without a single responsibility — see
  the GAP on `ui → controllers`.*
- **web-client** (`coppercli/WebServer/wwwroot/`) — vanilla ES-module browser UI, embedded
  in the assembly as a resource. No build step, no framework, no external CDN.
- **tests** (`coppercli.Tests/`) — xUnit, driving Core through `IMachine` fakes, plus
  app-layer state via `InternalsVisibleTo`. `Fakes/FakeGrbl.cs` is a GRBL on a loopback port
  the real `Machine` connects to, so `WebServerFixture` starts the real server and
  controllers over it and drives `/api/*` as a browser does. What reached the machine is
  read back from `FakeGrbl.Received`, in order.

## Interfaces

### controllers → machine · v2 · kind: function · contract: `coppercli.Core/Communication/IMachine.cs` (law)
Every controller reaches the machine only through `IMachine`. That interface file is the
contract; do not restate it here. It exists so controllers are testable without hardware,
`coppercli.Tests/Fakes/` supplies the doubles.
- Status predicates (`IsIdle`, `IsAlarm`, `IsHold`, `IsDoor`, `IsProblematic`) and every
  wait/poll loop live in `MachineWait`. A controller that spells out `machine.Status == "Idle"`
  or writes its own polling loop is a violation.
- **v2:** reusing a wait means checking its bail-out set against the state the caller is in.
  `IsProblematic` covers Door and Hold, so `WaitForIdleAsync` cannot wait either of them out;
  a caller that needs to gets its own wait (`WaitForDoorClosedAsync`,
  `WaitForDoorReleasedAsync`). See rule `no-bail-out-on-the-awaited-state`.
- `MachineWait.HomeAsync` is the only place `IsHomed` is set **true**. It is set false only
  inside `Machine` itself, on connect, disconnect, and soft reset — the three events after
  which the machine has forgotten its reference frame. No UI assigns it.
- **GAP (sharp target):** the `IMachine` interface stops at Core. `AppState.Machine`, `CncWebServer._machine`,
  `MachineCommands`, and `JogHelpers` are all typed to the concrete `Machine`, because
  `IMachine` was scoped to what controllers need — it lacks `Connect`/`Disconnect`,
  `SetFile`, `Jog`, `EnableAutoStateClear`, `FeedOverride*`. Net effect: Core is testable,
  **both UIs are not**. Target: `IMachine` describes the transport contract, and the app
  layer holds an `IMachine`.
- **GAP:** `Machine` raises 17 events with a bare `action?.Invoke(...)` — no dispatcher, so
  every handler runs inline on the raising thread and blocks GRBL streaming.
  `Program.SetupEventHandlers` does Spectre console I/O from there, and at `Program.cs:417`
  a `LineReceived` handler calls `Environment.Exit(0)` on a proxy force-disconnect —
  terminating the process from inside the serial read loop, bypassing every `finally`
  including `Machine`'s own teardown. (The proxy does send feed-hold then soft reset on
  client disconnect, so the spindle is stopped by the other side; the process teardown is
  what is skipped.) Eight of the 17 events have no subscribers at all, and
  `BufferStateChanged` is raised while `_bufferLock` is held — a deadlock that is latent
  only because nothing listens.

### ui → controllers · v5 · kind: function + event · contract: `coppercli.Core/Controllers/IController.cs` (law)
Both UIs start a workflow by configuring a controller, subscribing to its four events
(`StateChanged`, `ProgressChanged`, `UserInputRequired`, `ErrorOccurred`), and awaiting
`StartAsync`. Events are **synchronous** — the handler runs inline and the controller waits,
so a handler must not block on the UI thread's own input loop.
- The FSM's legal transitions are the `ValidTransitions` table at the top of
  `ControllerBase.cs`; that table is law. An illegal transition throws. Use
  `TryTransitionTo` where another thread may already have made the transition — an
  operator's pause arriving between a test and a `TransitionTo` would otherwise throw out
  of the run they were intervening in.
- **v5:** `StartAsync` leaves the controller in a terminal state however the run ended, so
  the task completing and the run being over are one fact. `ReleaseAsync` is the only way
  back to Idle: it stops an unfinished run first, then resets. Every start and every stop
  calls it, and nothing calls `Reset` directly, because `Reset` refuses a controller that
  still claims a run. See rule
  `one-way-back-to-idle`.
- **v3:** a `*Phase` enum names only the step of work a run is on. Whether the run is
  paused, waiting on a person, finishing, finished, cancelled or failed is `ControllerState`,
  read through the `ControllerBase` predicates (`IsPaused`, `IsActive`, `HasFinished`,
  `IsWaitingForOperatorState`). A phase member answering a lifecycle question is a second
  copy written on a separate path — see rule `one-field-per-fact` and
  `2026-09-phases-that-restated-the-lifecycle`. Waiting out a pause is
  `ControllerBase.WaitWhilePausedAsync`, not a loop per call site.
- **v2:** a controller instance serves the whole session, so `ControllerBase` declares
  `protected abstract void ResetRunState()` and calls it at the start of every run as well
  as from `Reset()`. Implementing it is how a new controller states what belongs to a run —
  see rule `per-run-state-cleared-at-run-start`.
- **v4:** two questions about a run sound alike and are not one fact.
  `ControllerBase.IsRunInProgress` answers "a run owns the machine": it counts
  `WaitingForUserInput` and `Completing`, and it is what refuses a second start and holds the
  serial port. `CncWebServer.MachineIsBeingDriven` answers "a workflow is moving the tool
  now": it refuses a jog or a goto, and it admits the pause milling holds in for a tool
  change, because that is when the operator is asked to jog to the surface and set Z0. Both
  derive from `ControllerState`; neither is written in terms of the other. Every run
  transitions the FSM, an outline trace included — a workflow that sets `Phase` and leaves
  `State` at `Idle` is invisible to both.
- **v4:** `ControllerBase.EmitError(Exception)` decides once, for all three UIs, whether an
  exception's text reaches the operator. A workflow's own refusal —
  `InvalidOperationException` or `TimeoutException` — is shown unchanged.
  `InvalidControllerStateException` and `ObjectDisposedException` are not, because their text
  names states and objects; nor is anything else. A UI that formats `ex.Message` itself is a
  violation.
- Answering a `UserInputRequired` prompt resumes the run **on the answering thread**, and the
  run can publish its next question before the answer returns
  (`ControllerBaseTests.AnsweringOnePromptPublishesTheNextBeforeItReturns`). Anything
  holding the current question must therefore identify it, not merely hold it — see the
  prompt rules on `web → browser` and `2026-09-a-prompt-id-that-could-not-stop-the-second-tap`.
- The M0/M1 pause leaves the tool where the hold left it and the spindle running, and the
  prompt says so. A feed hold resumes the motion GRBL still has buffered from wherever the
  machine is standing, so lifting clear and returning would have to land on the same point to
  the micron or cut the rest of the pass from the wrong place. A tool change can lift clear
  only because it tears the stream down and starts it again.
- **GAP (sharp target):** the "configure options from settings, load the grid/file,
  subscribe, run, unsubscribe" ritual is written out separately in
  `CncWebServer.cs`, in `Menus/ProbeMenu.cs` / `Menus/MillMenu.cs`, and in
  `Macro/MacroRunner.cs`. `ProbeOptions.FromSettings` and `ToolChangeOptions.FromSettings`
  are single definitions in Core and every caller uses them; it is the surrounding wiring
  that is duplicated, three times over.
  Target: one orchestration entry point per workflow that both UIs call, leaving each UI
  with presentation only. Honored-with-debt until then — the *workflows* are correctly in
  Core; the *wiring* is duplicated, and duplicated wiring is how the two UIs
  drifted apart before (see `2026-08-stale-work-zero-and-height-map`).

### machine → GRBL · v2 · kind: serial wire protocol · contract: `coppercli.Core/Util/GrblProtocol.cs` (law)
Status strings, real-time bytes, and command words are named there and nowhere else.
Targets GRBL 1.1f; 0.8/0.9/1.0 are known-incompatible.
- Every number sent to the machine is formatted through `GCodeFormat.Inv`. A comma decimal
  separator is a GRBL rejection, and on a comma-locale machine an interpolated string
  produces one silently.
- **v2:** closing the port does not stop the machine - GRBL works through its planner buffer
  with nobody listening. `Machine.WriteStopSequence(Stream)` is the one definition of the
  stop: feed hold, then soft reset, each given time to act. `SerialProxy.SendSafetyStop`
  calls it too. `Machine.NeedsStopBeforeDisconnect` decides whether to send it, exempting a
  port that never answered as GRBL. See rule `closing-the-port-does-not-stop-grbl`.

### proxy → TCP clients · v1 · kind: tcp · port 34000
`SerialProxy` re-exports the raw serial stream to one TCP client at a time, so a remote TUI
can drive the mill. Deliberately **unauthenticated** — it is a byte bridge, and anything
that reaches the port can send arbitrary G-code. Documented as such in the README.
- On client disconnect the proxy sends feed-hold then soft reset, so a dropped connection
  cannot leave the spindle running.
- Only one owner of the serial port may exist: `IsSerialPortInUse` lets the proxy refuse a
  TUI client while the web server holds the `Machine` connection.

### web → browser · v5 · kind: http + websocket · contract: `coppercli/WebServer/WebConstants.cs` (law)
Port 34001. Every path (`Api*`), every WebSocket message type (`WsMessageType*`), and every
socket command (`WsCmd*`) is a named constant there; the client's mirror is
`wwwroot/js/constants.js`. Neither side may hardcode a wire value.
- Admission is **`RequestGuard.IsAllowed`, applied once in `HandleRequest` before any
  branch** — API, WebSocket, and static files alike. Refusal is `403`, answered in the
  channel the caller used: JSON on the API and the socket, plain text for a page load, which
  a person reads. Four zero-keystroke checks, in order:
  1. **the peer's source address** must be loopback, sit in a private, link-local, or CGNAT
     block, or share a subnet with a live interface (`NetworkHelpers.IsLocalPeer`) — the
     only input in the request the caller cannot write, and what makes "trusted on this
     network" mean something other than "trusted from anywhere that can reach the port".
     Its limit: a locally-terminating tunnel or proxy (`ssh -R`, ngrok, nginx) makes the
     peer `127.0.0.1`, and no source-address check can see through that;
  2. **`Host`** must be an address literal or a **single label**, optionally suffixed
     `.local`. A single label cannot be delegated in public DNS, so only this network can
     answer for it. Dotted names — `mill.lan`, `mill.home.arpa`, `host.zone.local`, any AD
     or search-domain name — are refused, deliberately and at a known usability cost,
     because accepting a multi-label name is what makes DNS rebinding possible, and an
     `Origin` check cannot see rebinding: the browser by then genuinely believes it is
     same-origin;
  3. **`Origin`**, when present, must match the `Host` in host, port, and scheme;
  4. **`Sec-Fetch-Site`** must not be `cross-site` or `same-site`. **This check is inert on
     the configuration that ships and is not a defence.** Per W3C Fetch Metadata a browser
     attaches no `Sec-Fetch-*` header to a URL that is not potentially trustworthy, and a
     plain-http LAN address is not. It is kept because it works over `localhost` and would
     over TLS. Never state — in code, README, or release notes — that it blocks cross-site
     GETs. A raw socket can set the header; that proves only that the server reads it.
- **What this deliberately leaves open:** a cross-site GET carries no `Origin` and no
  `Sec-Fetch-Site`, so it is indistinguishable from the operator's own navigation and is
  admitted. Safe only while rule `no-side-effect-on-get` holds.
- Every response carries `X-Frame-Options: DENY`, CSP `frame-ancestors 'none'`, `nosniff`,
  and `no-referrer` (`ApplySecurityHeaders`). The UI is large on-screen buttons driving a
  machine; inside a frame every request it makes is genuinely same-origin, so refusing to be
  framed is the only answer.
- **There is no login, token, password, or PIN, and none may be added** — see rule
  `web-ui-needs-no-typed-credential`, `2026-08-web-access-token`, and
  `2026-08-a-guard-built-on-a-header-browsers-never-send`.
- The socket is the live channel; `/api/*` is request/response.
- **v5:** nothing enforces a single browser. A second browser is offered a take-over and
  still works if it declines, no `/api/*` endpoint checks which client is calling, and two
  tabs of one browser share a cookie so they are not distinguishable. `IsSupersededClient`
  replaces a stored connection only when it is from the same browser and no longer open - a
  reconnect after a reload - so a second tab cannot take the first off the status stream
  while leaving it able to send commands. See
  `2026-09-one-browser-client-was-never-enforced`.
- Server→client frames are `{type, data:{...}}`; client→server are `{type, ...fields}`.
- **v4:** every method check goes through `RequireMethod`, which answers `405`. A handler
  that matches on the method with no `else` writes nothing, and the guard's
  `finally { response.Close(); }` turns that into an empty 200 the UI reads as a lost
  connection.
- **v4:** an endpoint that starts something answers whether it started. `StartMilling`,
  `StartProbing` and `StartProbeTraceOutline` return the reason for a refusal or `null`, and
  `WriteStartResult` turns a reason into `409` with that text. The browser puts its screen up
  on this answer, so a refusal reported only later over the socket leaves a screen claiming a
  run that never began.
- **v4:** client→server commands live in the `DirectCommands` table alongside the HTTP path
  that runs the same thing, so the two entry points cannot drift. Each entry's `DuringRun` flag says
  whether it may be sent while a job drives the machine; the check is
  `MachineIsBeingDriven`, made once in `RunDirectCommand`. `WsCmdPing` is a real command with
  a handler — it refreshes the client's activity and so is what keeps the stale-client reaper
  off a quiet browser. Every `WsCmd*` is validated by `validateConstants`, and every
  broadcast names its type constant.
- **v5:** the status reports `probing` and `tracingOutline` separately, both derived from
  `IProbeController`. An outline trace is a run like any other - it owns the machine and
  locks the screen - but it measures nothing, so the progress window follows
  `IsMeasuringGrid` alone. `/api/probe/status` answers the same fact under `active`.
- **v5:** `/api/probe/stop` answers whether it confirmed the machine stopped, and `500`
  with `CliConstants.StopTimedOutWarning` when it could not. The browser leaves the progress
  view up when the stop is not confirmed, rather than showing a setup screen that says the
  run is over.
- **v4 (prompts):** `PendingPrompt` holds the one question a run is waiting on. An answer
  must name that question's id **and** be one of the `Options` it offered, because answering
  resumes the run on the answering thread and the run can publish its next question before
  the answer returns. The id has to reach four places or it protects nothing: the broadcast,
  `DetectToolChange`/`DetectOperatorPause` in the status, the client's `lastPrompt`, and the
  answer body. The id alone does not stop a double-tap — see rule
  `a-redrawn-control-settles-before-it-answers`.
- **GAP:** failure signalling is still inconsistent on pause: `/api/mill/pause` returns 400 on
  illegal state while `/api/probe/pause` returns 200 with `{success:false}`. Left as it
  stands; the start path is the one the operator's screen depends on.
- **DECISION (accepted, not a target):** the web file browsers can reach the whole
  filesystem. `/api/probe/save` writes to a path the client chooses, and `/api/files` /
  `/api/file/load` enumerate and read anywhere the process can reach. The operator picks a
  board file from wherever it sits, the same as in the terminal UI. LAN peers are already
  trusted by `web-ui-needs-no-typed-credential`. The save path is forced to the `.pgrid`
  extension.
  `/api/file/upload` still contains uploads (`Path.GetFileName` + `IsContainedIn`), because
  there the client supplies the bytes as well as the name.
  See `2026-09-a-prompt-id-that-could-not-stop-the-second-tap`.

### shared constants → client · v2 · kind: http · `GET /api/constants`
Any value both C# and JavaScript need crosses here. `GetSharedConstants()` in
`CncWebServer.cs` serves it, `validateConstants()` in `helpers.js` compares it against the
client's own copy at startup and reports mismatches. A value duplicated between the two
languages without passing through `/api/constants` is a violation, not a shortcut.
- **v2:** a value published here that neither side reads and no check compares is deleted,
  not wired up. The `api` path group was two dozen paths stored twice with no way to notice a
  disagreement; a wrong path answers 404, which is loud. Publish a value when something
  consumes or verifies it.
- **GAP (sharp target):** this is still a lint pass, not a data channel. `constants.js`
  hardcodes every value and **nothing in the UI reads a value *from* the endpoint** — a
  mismatch is only `console.warn`ed. Three groups are published and never compared: `probe`
  limits, `millGrid`, and `depthAdjustment`, and `index.html` hardcodes the very limits being
  published. `mill.js` reimplements `CncWebServer.MapToGrid` line for line, so the client both
  fetches grid cells and recomputes them. Target: the client *consumes* these values rather
  than restating and comparing them. The genuine data channel is `/api/config`.

### app → disk · v1 · kind: file · contract: `coppercli/Persistence.cs` (sole writer)
Settings, session state, and the probe autosave live under the OS app-data directory.
`Persistence` is the only code that reads or writes them; writes go through
`AtomicFile` so a power cut mid-write cannot leave a half-file. An unreadable file is
quarantined and replaced with defaults rather than crashing the app.
- Renaming a `MachineSettings` property requires an entry in the `SettingsMigrations` array
  in the same file. Migrations are idempotent and rewrite the file once.

### probe data lifecycle · v3 · kind: state model · contract: the `<remarks>` block atop `coppercli.Core/Controllers/ProbeController.cs` (law)
The four states (`none` / `ready` / `partial` / `complete`), what each offers the operator,
and the autosave rules are documented there. Do not restate or re-derive them; UI state is
computed from the grid via `ComputeProbeState`, and both it and Save-vs-Clear read
`AppState.ReadUsableAutosave`.
- A height map is only trusted against the `ProbeContext` saved with it: the source file
  and work origin it was measured against. Existence of a file is never the answer to
  "do I have probe data?".
- **v2:** a reading is judged before it is recorded. `ProbeGrid.GetNeighbourDeviation` compares it
  against the mean of its measured orthogonal neighbours; past
  `ControllerConstants.ProbeHeightDeviationToleranceMm` the run lifts to safe height,
  pauses for the operator, and re-probes the point on resume. Nothing suspect reaches the
  map or the autosave, and the point stays queued — see rule `resume-is-not-approval`.
- **v3:** `AppState.ReadUsableAutosave` is the only code that reads the autosave, and
  `AppState.CurrentProbeGrid` the only answer to "does the operator have probe data": the
  grid in memory, or that autosave when nothing is loaded. It reads without adopting, so a
  status may ask (rule `no-side-effect-on-get`), and it returns nothing for a map measured
  for another file or before the origin moved (rule `derived-artifact-records-its-context`).
  The mill preflight, `/api/probe/save` and `/api/probe/apply` read it, so a complete map
  sitting unapplied in the autosave refuses the job rather than letting it cut uncorrected.
  **GAP:** other call sites still read `AppState.ProbePoints` directly, and its setter is
  public, so nothing yet makes the wrong choice hard.
- **v3:** a saved map gets the checks a grid built in memory gets. `ProbeGrid.Load` refuses
  extents that are not finite or not ordered, fewer than two nodes on an axis, a point
  outside the grid, and a height or origin that is not a number — a file reaching
  `InterpolateZ` decides the commanded Z of every cutting move, and a non-finite origin
  would let it declare itself applicable to any job.

### build → release artifact · v1 · kind: ci/shell · contract: `.github/workflows/release.yml`
A tag `v*` builds self-contained single-file executables for `win-x64`, `osx-arm64`,
`osx-x64`, `linux-x64`. Every file the app loads at runtime must be either an
`EmbeddedResource` or present in the shipped artifact.
- **GAP (live defect):** only `WebServer/wwwroot/**` is embedded. `machine-profiles.yaml`
  and `Resources/*.csv` (the GRBL error, alarm, and setting tables) are
  `CopyToOutputDirectory`, and the Unix tarball step archives only the single executable —
  so the macOS and Linux downloads ship without them, and **both loaders fail silently**
  (`GrblCodeTranslator` returns null, `MachineProfiles` returns empty). The README
  advertises built-in machine profiles on all three platforms. The Windows installer copies
  `publish\*` recursively and is unaffected. Target: embed them, or archive the publish
  directory, and make a missing data file a loud error per `error-before-use`.
- **GAP:** `scripts/build-release.sh` + `create-release.sh` implement a second, local
  release path that names assets `-macos-arm64` while CI names them `-osx-arm64`;
  `update-homebrew-formula.sh` understands only the CI names, and `create-release.sh`
  pushes a tag that races the workflow it triggers. Target: one release path.

### cli invocation · v1 · kind: cli · contract: `coppercli/Program.cs`
`--debug`/`-d`, `--server`/`-s` with `--proxy-port` / `--web-port`, `--macro`/`-m` followed
by `--name value` placeholder pairs. `--port` is a legacy alias for `--web-port`.

### macro file · v1 · kind: file · contract: `docs/macros.md` (law)
The `.cmacro` command vocabulary is the table in that document. A new command changes the
document and `MacroParser` together.
- **GAP:** undocumented behavior remains. `wait <anything>` ignores its argument and always
  waits for Idle, and `jog` / `probe grid` / `mill` open the **interactive** TUI menus rather
  than running headless, which matters to anyone writing an unattended macro. (Two documented
  workflows that could not work are now fixed: `load [name:file]` no longer roots the
  placeholder before substitution, and `probe z` calls
  `ProbeController.ProbeZSingleAsync` rather than waiting on a callback nothing invoked —
  see `2026-09-phases-that-restated-the-lifecycle`.)
- **GAP:** the macro engine is a third front end and the least governed one — it drives
  `Machine` directly rather than through the controllers, and re-implements
  `MachineWait.WaitForIdleAsync`. `MacroMenu.RunMacroFromPath` discards the runner's
  success flag, so a failed macro still exits 0.

## Rules

- **web-ui-needs-no-typed-credential** *(error)* — the web UI must stay reachable by typing
  a bare LAN address (`http://192.168.1.5:34001`) into a phone browser, with nothing to
  enter and no secret in the URL. No token, password, PIN, or key the operator must carry
  or type. LAN peers are deliberately trusted; the owner made that call explicitly, and
  re-declined a PIN when the token came out. Protection is limited to checks that cost the
  operator zero keystrokes: the peer's source address, plus the `Host` and `Origin` headers
  a browser sends unasked. (`Sec-Fetch-Site` is checked too but arrives only over
  `localhost` or TLS — see the `web → browser (HTTP/WS)` entry.)
  _Check: `.architecture/rules/check-layering.sh`; `coppercli.Tests/RequestGuardTests.cs`._
  _History: 2026-08-web-access-token,
  2026-08-a-guard-built-on-a-header-browsers-never-send._

- **one-way-back-to-idle** *(error)* — the controller owns whether a run is going. A task
  handle is held to await, never to answer that question, and the two cannot disagree
  because `StartAsync` always ends in a terminal state. `ReleaseAsync` is the single route
  back to Idle; no caller writes its own `Reset` guard. A stop path that already holds a
  run's task awaits that run's own teardown rather than releasing on top of it: a second
  `ReleaseAsync` into a teardown that has overrun sends another feed-hold and soft reset,
  which wipes the lift the first one queued (rule `closing-the-port-does-not-stop-grbl`).
  _Check: `.architecture/rules/check-layering.sh`; `coppercli.Tests/ControllerBaseTests.cs`
  (mutation: dropping the terminal-state guarantee fails two tests)._
  _History: 2026-09-two-records-of-whether-a-run-is-over,
  2026-09-three-owners-of-do-i-have-probe-data._

- **one-handler-per-control** *(error)* — a control carries one handler registration.
  `onclick` and `addEventListener` are separate slots and both fire, so a control bound
  through each runs its handler twice on one tap. What a control does as the machine's state
  changes is decided inside that one handler, not by writing a second slot.
  _Check: `.architecture/rules/check-layering.sh` (names the doubled element)._
  _History: 2026-09-two-records-of-whether-a-run-is-over._

- **ui-text-is-a-constant** *(error)* — text a person reads is named once in `constants.js`
  and referenced, never spelled at the point of use. A second spelling of the same label
  drifts from the first the moment either is edited.
  _Check: `.architecture/rules/check-layering.sh`._

- **no-exception-text-on-screen** *(error)* — an exception message names files, offsets and
  types the operator cannot act on. It goes to the log; the screen gets a sentence about what
  failed and what to do. On the C# side `WriteFailure` is the one path that answers a caught
  exception.
  _Check: `.architecture/rules/check-layering.sh` (the JS half; the C# side is one helper)._

- **closing-the-port-does-not-stop-grbl** *(error)* — any path that drops the connection
  stops the machine first, and the bytes go straight to the stream, because the worker that
  drains the send queue is gone by then. Any path that ends a run converges on one teardown
  that stops the machine and then lifts the tool clear: `ProbeController.CleanupAsync`. A
  lift queued before the soft reset is wiped by it, so the test asserts the order.
  _Check: `coppercli.Tests/SafetyGuardTests.cs`; `coppercli.Tests/ProbeControllerTests.cs`
  (mutation: reordering or removing either half fails)._
  _History: 2026-09-closing-the-port-does-not-stop-the-machine._

- **guard-covers-whole-surface** *(error)* — a check that admits or refuses a request runs
  once, before any routing branch, and covers static files as well as `/api/*` and `/ws`.
  A guard on the API alone produces a page that loads and then silently does nothing.
  _Check: reader judgment of `HandleRequest`._  _History: 2026-08-web-access-token._

- **no-side-effect-on-get** *(error)* — a GET changes nothing: no machine motion, no file
  written, no state loaded, no client slot reserved. A
  cross-site GET carries no `Origin` and, on plain http, no `Sec-Fetch-Site`, so
  `RequestGuard` cannot tell an `<img>` on someone else's page from the operator's own
  navigation and admits it; the only thing making that safe is that GETs do nothing.
  Everything that changes state is POST and stays POST. Reserving the single client slot
  from `ServeStaticFile` once let a cross-site `<img>` mint phantom pending clients, so the
  operator's own socket skipped `Connect()` and offered a force-disconnect that can drop the
  serial port mid-cut. `GET /api/probe/status` reports the autosave through
  `AppState.ReadUsableAutosave`, which reads it without adopting it. Taking a load out of a
  GET takes out the checks that load carried, so reading and adopting are separate calls
  (`ReadUsableAutosave` and `EnsureProbeDataLoaded`) and the checks belong to the read —
  never a fall back to the raw file state.
  _Check: reader judgment of `HandleApi`; `coppercli.Tests/RequestGuardTests.cs`;
  `coppercli.Tests/WebServerSequenceTests.cs`._
  _History: 2026-08-a-guard-built-on-a-header-browsers-never-send,
  2026-09-three-owners-of-do-i-have-probe-data._

- **machine-state-single-writer** *(error)* — a fact about the machine is owned by
  `Machine` (or derived from the controller that owns it) and assigned in exactly one
  place; no UI keeps its own mirror. `IsHomed` is set true only in `MachineWait.HomeAsync`
  and false only inside `Machine`; `AppState.IsProbing` is derived, not stored; work-zero
  invalidation hangs off the
  connection-state event, not off a menu. **Scope:** this governs facts the *machine* owns.
  A fact the *operator* asserted about the *workpiece* — `IsWorkZeroSet`, whether a height
  map still applies — is session state and deliberately stays in `AppState`; do not "fix"
  it into `Machine`. _Check: reader judgment; grep for assignments._
  _History: 2026-08-stale-work-zero-and-height-map, 2026-08-work-zero-deliberately-stays-in-appstate._

- **per-run-state-cleared-at-run-start** *(error)* — controllers are session-lifetime
  singletons, so every field describing the current run is declared in
  `ControllerBase.ResetRunState()` and cleared when a run *starts*, not only on `Reset()`:
  abort paths do not all reach a reset. The method is `abstract` so a new controller must
  answer the question. Anything derivable from `State` is not stored — `IsPaused` is
  `State == Paused`, as `IsActive` already was. **Scope:** what the machine or the operator
  owns outlives the run and must survive the reset — the shift still sitting in GRBL's G54
  (`_outstandingDepthAdjustment`), the probe grid and its progress index, which let an
  interrupted board resume. Ask which of the two a new field is before adding it.
  _Check: `coppercli.Tests/ControllerBaseTests.cs`._
  _History: 2026-08-a-pause-flag-that-outlived-its-job._

- **one-field-per-fact** *(error)* — a boolean saying "X is outstanding" and a separate field
  saying "how much X" are one fact and must be one field, 0 meaning none. Held apart, they
  drift: a later run with adjustment 0 passed the restore's tolerance check and cleared
  `_depthAdjustmentApplied` while the earlier shift stayed baked into G54. A duplicate need
  not be a field: an enum member that answers a question another type owns is one
  (`MillingPhase.Paused` beside `ControllerState.Paused`), and so is a running aggregate
  kept beside the collection it summarizes. `ProbeGrid.MinHeight`/`MaxHeight` were widened
  by each measurement and so could never narrow, leaving a re-probed node's stale extreme
  for `InterpolateZ` to clamp a cut depth against; both are now derived from the points.
  Ask of any summary whether the data can change in a direction it cannot follow. The
  inverse also holds: two questions that sound alike are not one fact. `IsRunInProgress`
  ("a run owns the machine") and `MachineIsBeingDriven` ("a workflow is moving the tool now")
  differ exactly at the tool-change pause, where the operator is meant to jog. Before merging
  two predicates, name the case where their answers must differ; if there is one, both stay,
  each derived from the same state. A question is owned the way a field is: "does the
  operator have usable probe data" was answered by whether the autosave parses, by
  `AppState.ProbePoints`, and by a third computation in the web status, so the mill
  preflight read the answer that said none while the probe screen read the one that said
  complete. `AppState.CurrentProbeGrid` is the single answer now, and every gate and every
  display derives from it. Name the owner of the question, not only of the field.
  _Check: `coppercli.Tests/DepthAdjustmentTests.cs`; `coppercli.Tests/ProbeControllerTests.cs`
  (`PhaseEnums_DoNotRestateTheRunLifecycle`); `coppercli.Tests/WebServerSequenceTests.cs`._
  _History: 2026-08-a-pause-flag-that-outlived-its-job,
  2026-09-phases-that-restated-the-lifecycle,
  2026-09-three-owners-of-do-i-have-probe-data._

- **never-auto-clear-a-safety-gate** *(error)* — software never clears a state that exists
  to require human confirmation. The enclosure door blocks a job and only the operator
  resumes it. Homing is deliberately impossible to skip: without it, `G53` retracts have
  no reference to retract to. _Check: reader judgment; `coppercli.Tests/SafetyGuardTests.cs`._
  _History: 2026-08-software-clearing-safety-gates,
  2026-09-a-wait-that-bailed-on-the-state-it-was-waiting-out._

- **no-cached-physical-measurement** *(error)* — never cache a measurement of a physical
  thing across a boundary where a human can silently change it. The tool-setter reference
  length is measured every time, never persisted. The boundary is not the session: the
  setter's trigger height was cached to rapid toward, but it is probed once with the old tool
  and once with the new, so the rapid always aimed one tool at another tool's height. Ask what
  the number describes and whether it still describes the thing about to move — not whether
  the line is reachable. _Check: reader judgment._
  _History: 2026-08-cached-reference-tool-length,
  2026-08-a-rapid-aimed-at-the-other-tools-trigger-height._

- **derived-artifact-records-its-context** *(error)* — an artifact computed from a setup
  carries that setup with it and is re-validated against it before use. A height map stores
  its `ProbeContext` (source file, work origin); a map with no recorded context is `Unknown`
  and is questioned, never assumed usable. The test runs in one place —
  `AppState.ReadUsableAutosave` — so no gate can reach the file without it, and a map that
  names a source file must carry an origin that can be read: a non-finite origin compares
  false against every tolerance and would leave the map `Unknown`, which no gate refuses.
  _Check: `coppercli.Tests/ProbeContextTests.cs`; `coppercli.Tests/ProbeGridLoadTests.cs`._
  _History: 2026-08-probe-data-inferred-from-a-file-on-disk,
  2026-09-three-owners-of-do-i-have-probe-data._

- **a-loader-enforces-the-constructors-invariants** *(error)* — a file read back into an
  object that decides machine motion gets the checks the constructor makes.
  `ProbeGrid.Load` refuses what `RequireUsableShape` refuses — extents that are not finite
  or not ordered, fewer than two nodes on an axis — plus point indices outside the grid and
  heights that are not numbers, because a loaded map decides the commanded Z of every
  cutting move. A field the trust check depends on is required, not optional: see
  `derived-artifact-records-its-context` for the origin case.
  _Check: `coppercli.Tests/ProbeGridLoadTests.cs`._
  _History: 2026-09-three-owners-of-do-i-have-probe-data._

- **read-g54-explicitly** *(error)* — before any `G10 L2 P1`, query G54 itself
  (`RefreshWorkOffsetsAsync`); never use the combined `WorkOffset`, and never derive it from
  `MachinePosition − WorkPosition`. Undo an offset relatively, never by restoring an
  absolute snapshot; a tool change legitimately owns the same register.
  _Check: `coppercli.Tests/DepthAdjustmentTests.cs`._  _History: 2026-08-workoffset-is-not-g54._

- **monotonic-time-and-event-counts** *(error)* — timeouts use `Stopwatch`, never
  `DateTime.Now`. A clock must never answer a question that is not about time. "Is the peer
  still talking?" is answered by counting events (`StatusReportCount`), never by timing them;
  "is this probe reading trustworthy?" is answered from the measurement — the height against
  its measured neighbours, `ProbeGrid.GetNeighbourDeviation` — never from how long the probe
  took. Before timing a code path, ask what else is inside the interval: `RetractZAsync` and
  `MoveToPointAsync` deliberately return without awaiting so GRBL can buffer them, so a
  stopwatch around `G38.2` spanned the previous retract, the traverse, and the descent as
  well. _Check: grep for `DateTime.Now` in wait paths; reader judgment of any `Stopwatch`
  that gates a decision._
  _History: 2026-08-synchronized-queues-and-wall-clock-deadlines,
  2026-09-a-stopwatch-timing-everything-but-the-probe._

- **no-live-collections-across-threads** *(error)* — never expose a live mutable collection
  across a UI/worker boundary; own it and hand out snapshots. `Queue.Synchronized` does not
  make a check-then-take safe. Cleanup in a `finally` must never throw over the error that
  caused it. _Check: reader judgment; `coppercli.Tests/ProbeGridTests.cs`._
  _History: 2026-08-live-queue-across-the-ui-worker-interface,
  2026-08-synchronized-queues-and-wall-clock-deadlines._

- **error-before-use** *(error)* — before the app relies on a file, resource table, or
  profile it ships with, it asserts that file, table, or profile is present and fails with one
  clean line naming what is missing. A missing `machine-profiles.yaml` or GRBL code table
  currently returns empty/null and the feature quietly disappears. _Check: reader judgment of the load paths._

- **workflows-live-in-controllers** *(error)* — every multi-step machine operation is an
  FSM in `coppercli.Core/Controllers/`. `CncWebServer.cs` and the TUI menus may configure,
  subscribe, start, and render — never decide the sequence of machine moves. Single-shot
  commands from a UI go through `coppercli/Helpers/MachineCommands.cs`, the app-layer funnel;
  no menu and no HTTP handler calls `SendLine` directly. Honored today: zero `SendLine` calls
  outside Core and `MachineCommands`.
  _Check: grep `SendLine` in `coppercli/Menus/` and `coppercli/WebServer/` — must be empty._

- **controllers-never-render** *(error)* — controllers emit events and return values; they
  never touch the console, `AnsiConsole`, `HttpListener`, or a socket.
  _Check: dependency direction; grep Core for UI types._

- **core-is-platform-independent** *(error)* — `coppercli.Core` never references the
  `coppercli` project and never imports a UI or web-host type. Dependencies flow one way.
  Shared constants used by Core live in `coppercli.Core/Util/Constants.cs`.
  _Check: `.architecture/rules/check-layering.sh`._

- **shared-constants-flow-through-api** *(error)* — a value both the server and the browser
  need is served by `GetSharedConstants()` and verified by `validateConstants()`. Never
  write the same literal into both `CliConstants.cs`/`Constants.cs`/`GrblProtocol.cs` and
  `constants.js`. A value only one side needs is not published here: publishing it makes a
  second copy that nothing compares, which is the drift this rule exists to stop.
  _Check: reader judgment; `validateConstants()` reports at runtime._
  _History: 2026-09-a-prompt-id-that-could-not-stop-the-second-tap._

- **ws-message-types-updated-in-four-places** *(error)* — a new WebSocket message type is
  added to `WebConstants.cs`, `constants.js`, the `wsMessageTypes` object in
  `GetSharedConstants()`, and the validation list in `helpers.js`. Three out of four is a
  mismatch that only shows up at runtime. The same four points apply to the `WsCmd*`
  client→server commands, under `commands` — an unvalidated command breaks jogging on a
  rename, silently.
  _Check: `.architecture/rules/check-layering.sh`._

- **api-paths-are-constants** *(error)* — no `fetch()` hardcodes an `/api` path and no
  handler hardcodes one; both sides use their named constant.
  _Check: `.architecture/rules/check-layering.sh`._

- **culture-invariant-gcode** *(error)* — every number sent to the machine is formatted
  through `GCodeFormat.Inv`. An interpolated string on a comma-decimal locale emits
  `Z-1,000`, which GRBL rejects. _Check: `.architecture/rules/check-layering.sh`._

- **settings-rename-needs-migration** *(error)* — renaming a `MachineSettings` property
  adds a `SettingsMigrations` entry in `Persistence.cs`, with a version comment. Do not
  rely on backwards compatibility; migrate and use the new name everywhere.
  _Check: reader judgment of the diff._

- **resume-is-not-approval** *(error)* — a return value that decides whether to commit a
  suspect measurement names *why* the run is continuing; a boolean cannot. "Keep going" does
  not distinguish "the reading was fine" from "a person intervened and resumed", and that
  difference is the whole decision. `RetractAndConfirmHeightAsync` returned
  `!ct.IsCancellationRequested`, which the caller read as "record it", so the height taken
  *before* the operator cleared the debris went into the map and the autosave and the grid
  reported itself complete. It is now the three-valued `HeightVerdict`
  (`Accepted` / `Remeasure` / `Cancelled`), and a resume re-probes the point. More generally:
  when an operator intervenes, the run resumes at the step that raised the question, not past
  it. _Check: `coppercli.Tests/ProbeControllerTests.cs` — the locking test is
  mutation-verified; returning `Accepted` in place of `Remeasure` reproduces the defect._
  _History: 2026-09-a-stopwatch-timing-everything-but-the-probe._

- **no-bail-out-on-the-awaited-state** *(error)* — a wait helper must not treat the state the
  caller is waiting to leave as a bail-out condition; such a wait can never succeed, and it
  fails as an ordinary `false` that the caller's retry turns into what looks like a hang.
  `EnsureDoorClosedAsync` called `WaitForIdleAsync`, which counts Door as `IsProblematic` and
  gave up on its first poll, so the door prompt re-appeared within milliseconds of being
  answered. Door and Hold sit in `IsProblematic` precisely because most callers are not
  waiting them out; a caller that *is* gets its own wait —
  `WaitForDoorClosedAsync`, `WaitForDoorReleasedAsync`. Before reusing a wait, check its
  bail-out set against the state you are starting from.
  _Check: `coppercli.Tests/MachineWaitTests.cs`._
  _History: 2026-09-a-wait-that-bailed-on-the-state-it-was-waiting-out._

- **a-test-must-be-able-to-fail** *(error)* — a test earns its place only if some plausible
  defect turns it red. Subscribing an event without raising it, or passing an enum literal to
  `Enum.IsDefined`, asserts nothing; seventeen such tests were removed or replaced. Where a
  test guards a named rule, prove it by mutation before trusting it. A guard test matches the
  *meaning* the rule is about, not the spelling it was written against —
  `PhaseEnums_DoNotRestateTheRunLifecycle` compared names by equality and let
  `WaitingForOperator` past `WaitingForUserInput`, missing both members that had actually
  caused a defect. And no test mutates process-wide state: xUnit runs classes in parallel and
  ignores an unresolvable `[Collection]` name without warning, so
  `CultureInfo.DefaultThreadCurrentCulture` set in one class decided whether tests in other
  classes passed. Scope it to the thread (`CultureInfo.CurrentCulture` flows across `await`).
  _Check: reader judgment of new tests; `.architecture/rules/check-layering.sh`._
  _History: 2026-09-seventeen-tests-that-asserted-nothing,
  2026-08-a-test-suite-that-had-not-compiled-since-february._

- **fail-safe-on-uncertainty** *(error)* — when a safety-relevant step cannot be confirmed
  (a retract that GRBL rejected, a probe that did not report contact, a status that never
  arrived), the job stops rather than continuing. A rejected safety retract must never be
  swallowed. _Check: reader judgment; `coppercli.Tests/SafetyGuardTests.cs`._

- **fake-answers-like-the-machine** *(error)* — a test double reproduces the machine's
  observable answer to each command under test. `FakeMachine` reporting `Idle` after every
  pause line hid that GRBL answers M0/M1 with `Hold:0` while M6 never reaches it, so a gate
  that could not fire on hardware passed 338 green tests. One status
  hardcoded across a family of commands erases the distinction the code is deciding on, and
  the suite then agrees with the code because both read the same invention.
  _Check: reader judgment of `coppercli.Tests/Fakes/`._
  _History: 2026-08-a-fake-that-answered-idle-to-every-pause,
  2026-08-a-test-suite-that-had-not-compiled-since-february._

- **a-redrawn-control-settles-before-it-answers** *(error)* — a control that answers a
  question, and is redrawn with the next question in the same place, refuses input for
  `PROMPT_SETTLE_MS` after each redraw. An id is not enough on its own. The answer resumes
  the run on the answering thread, and the run publishes its next question before the answer
  returns, so the second tap carries the **new** question's own valid id and the server has no
  grounds to refuse it. The two guards cover different cases: the id (`PendingPrompt`, which
  also requires the answer to be one of the question's `Options`) refuses a stale answer or
  one from a second device; the settle refuses the second tap of a double-tap, and so is set
  longer than `DOUBLE_TAP_DELAY_MS`. Ask what the control will be showing when the second
  event arrives, not whether the first was addressed correctly.
  _Check: `coppercli.Tests/PendingPromptTests.cs`;
  `coppercli.Tests/ControllerBaseTests.cs` (`AnsweringOnePromptPublishesTheNextBeforeItReturns`);
  reader judgment of any handler that both answers and redraws._
  _History: 2026-09-a-prompt-id-that-could-not-stop-the-second-tap._

- **no-magic-values** *(error)* — every literal with semantic meaning is a named constant
  in the file that owns it: `coppercli.Core/Util/Constants.cs` (Core-wide),
  `coppercli/CliConstants.cs` (CLI/UI), `coppercli.Core/Util/GrblProtocol.cs` (GRBL wire),
  `coppercli/WebServer/WebConstants.cs` (HTTP/WS wire), `wwwroot/js/constants.js` (client).
  Logging strings are exempt. _Check: reader judgment; see `CLAUDE.md` for the grep recipes._

## Intent

The shape the system is being built toward. **Not law** — the conforming pass measures the
gap, never reports a planned item as drift.

- **shared workflow orchestration** *(part, planned)* — one entry point per workflow that
  both the TUI and the web server call, so the mill/probe/tool-change start sequences leave
  `CncWebServer` entirely. Named by the owner as "the single highest-leverage remaining
  item", deferred as needing its own careful cycle. **Delta:** absent; the wiring ritual is
  written out on both sides. This is the GAP on the `ui → controllers` interface.
- **`CncWebServer` split** *(part, planned)* — the ~3.7k-line static class separated into
  request routing, workflow orchestration, and broadcast/lifecycle. **Delta:** one file,
  one static class.

_Beyond these there is no roadmap: zero `TODO`/`FIXME` markers in ~14k lines, no design
doc, no diagrams, no reachable issue backlog. Direction is set per-session by the owner and
lives only in the prompt log, which is why this memory exists._

## Known gaps in the record

Each is something a human must supply.

- **The prompt log is not committed.** `prompts/` is gitignored, so the blunt record of
  *why* — the reversals, the rejected designs, the owner's steering — does not survive a
  fresh clone. `.architecture/history/` now carries what could be recovered from it.
- **`CLAUDE.md` still lags the tree, though less than it did.** Fixed 2026-09: the
  `MachineWait.HomeAsync` example no longer restates a body that had drifted (it points at
  the file instead, since a copy is a second definition), and the reference to a
  `StatusHelpers.cs` that does not exist is gone. Still outstanding: `coppercli.Tests/` is
  absent from the project structure, and the document names none of `AtomicFile`,
  `ProbeContext`, `HomingOutcome`, `EtaEstimator`, `GCodeFormat`, `GrblRejection`,
  `SessionRestore`, `PassThrough`, or `RequestGuard`.
- **The OpenCNCPilot reference implementation is unavailable here.** `CLAUDE.md` instructs
  agents to consult `~/src/OpenCNCPilot/` for GRBL and probing questions; that tree is not
  present, so some upstream semantics (e.g. `ProbeOptions.MaxDepth`) cannot be settled.
- **The only end-to-end user guide is off-repo** — <https://thomer.com/pcb-nomad3>,
  unversioned against the app. Nothing in the tree will catch it if it drifts.

---

**History:** `.architecture/history/` — one lesson per file, append-only. Read it before
changing a interface; the entries record approaches already tried and abandoned. Grep the `Touches:`
line at the foot of each entry for the interface, rule, or path you are about to touch.
