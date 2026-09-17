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
- **core-settings** (`coppercli.Core/Settings/`) — `MachineSettings`, `SessionState`, and
  `SettingRange`: what each numeric setting may be, and how to read and write it. A range
  belongs beside the setting it bounds, so the settings screen, the web API and the loader
  all check against one table. Nothing else here: the DTOs stay plain.
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
  *Finding: this is the one part without a single responsibility, and the largest file in
  the tree by some way — see the GAP on `ui → controllers`.*
- **web-client** (`coppercli/WebServer/wwwroot/`) — vanilla ES-module browser UI, embedded
  in the assembly as a resource. No build step, no framework, no external CDN.
- **tests** (`coppercli.Tests/`) — two suites. xUnit drives Core through `IMachine` fakes,
  plus app-layer state via `InternalsVisibleTo`. `coppercli.Tests/browser/` runs the real
  `wwwroot/js` modules against a stub page under `node --test`, with no `package.json` and
  nothing to install; `dotnet test` does not see it, so CI runs it as its own step. `Fakes/FakeGrbl.cs` is a GRBL on a loopback port
  the real `Machine` connects to, so `WebServerFixture` starts the real server and
  controllers over it and drives `/api/*` as a browser does. What reached the machine is
  read back from `FakeGrbl.Received`, in order.

## Interfaces

### controllers → machine · v4 · kind: function · contract: `coppercli.Core/Communication/IMachine.cs` (law)
Every controller reaches the machine only through `IMachine`. That interface file is the
contract; do not restate it here. It exists so controllers are testable without hardware,
`coppercli.Tests/Fakes/` supplies the doubles.
- Status predicates (`IsIdle`, `IsAlarm`, `IsHold`, `IsDoor`, `IsUnavailable`) and every
  wait/poll loop live in `MachineWait`. A controller that spells out `machine.Status == "Idle"`
  or writes its own polling loop is a violation.
- **v2:** reusing a wait means checking its bail-out set against the state the caller is in.
  `MachineWait.IsUnavailable` is that set — alarm, the three door states, asleep and no link.
  Hold is *not* in it. So `WaitForIdleAsync` cannot wait a door out, and a caller that needs
  to gets its own wait (`WaitForDoorReleasedAsync`, and the report-counted catch-up inside
  `ReleaseDoorHoldAsync`). See rule `no-bail-out-on-the-awaited-state`.
- **v3:** the door has three states, not two. `MachineWait.GetDoorState` returns which one
  as a `DoorState`; the three predicates behind it are private, so the partition has one
  reader.
- **v4:** every question a screen asks about the machine is answered in `MachineWait` and
  shipped as a value: `GetActivity` returns a `MachineActivity`, and `IsResponding`,
  `NeedsAttention`, `CanPause`, `CanResume` and `IsUnavailable` are derived from it.
  `/api/status` sends `needsAttention`, `canPause` and `canResume` under their own names,
  `IsResponding` as `connected`, and `CanReleaseDoorHold` as `canReleaseDoor`.
  The terminal calls the same functions. The payload sends `activity.ToString()`, so the
  member names are part of the wire contract. GRBL's status word is still sent, for display
  only. See rule `the-browser-draws-what-it-was-handed`.
- **GAP (undecided):** GRBL has one resume, so the cycle start that releases a door hold
  releases a feed hold with it. A run that was paused when the door opened therefore
  carries on cutting while `ControllerState` still reads `Paused` and the screen still
  offers Resume. Two answers are open and neither is derivable from the code: re-assert
  the feed hold immediately after the release (the tool moves for GRBL's reaction time),
  or let the release end the pause, which is what the prompt the operator answered says it
  does. _Target: the owner picks one; the prompt wording follows it._
  `MachineWait.ReleaseDoorHoldAsync` is the only place coppercli releases a door hold (a
  client of `SerialProxy` writes its own bytes to the port and is outside this), and it sends
  the cycle start only on GRBL's own reading of the switch; `ControllerBase.EnsureDoorClosedAsync`
  is the only place a workflow asks the operator for it.
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
  `Program.SetupEventHandlers` does Spectre console I/O from there, and its `LineReceived`
  handler calls `Environment.Exit(0)` on a proxy force-disconnect —
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
  paused, waiting on the operator, finishing, finished, cancelled or failed is `ControllerState`,
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
  (`ControllerBaseTests.AnsweringAPrompt_PublishesTheNextBeforeItReturns`). Anything
  holding the current prompt must therefore identify it by id — see the prompt rules on
  `web → browser` and `2026-09-a-prompt-id-that-could-not-stop-the-second-tap`.
- The M0/M1 pause leaves the tool where the hold left it and the spindle running, and the
  prompt says so. A feed hold resumes the buffered motion from wherever the machine is, so
  retracting and returning would have to land on the same point to the micron or cut the rest
  of the pass from the wrong place. A tool change can retract only because it tears the
  stream down and restarts it.
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

### machine → GRBL · v3 · kind: serial wire protocol · contract: `coppercli.Core/Util/GrblProtocol.cs` (law)
Status strings, real-time bytes, and command words are named there and nowhere else.
Targets GRBL 1.1f; 0.8/0.9/1.0 are known-incompatible.
- Every number sent to the machine is formatted through `GCodeFormat.Inv`. A comma decimal
  separator is a GRBL rejection, and on a comma-locale machine an interpolated string
  produces one silently.
- **v2:** closing the port does not stop the machine - GRBL keeps working through its
  planner buffer. `Machine.WriteStopSequence(Stream)` is the one definition of the
  stop: feed hold, then soft reset, each given time to act. `SerialProxy.SendSafetyStop`
  calls it too. `Machine.NeedsStopBeforeDisconnect` decides whether to send it, exempting a
  port that never answered as GRBL. See rule `closing-the-port-does-not-stop-grbl`.
- **v3:** GRBL reports the door as `Door:<n>`, and the number is what separates an open door
  from a closed one. `Machine` splits the two and `GrblProtocol` names both halves: `DoorSubStateClosed`,
  `Ajar`, `Retracting` and `Resuming`, plus `StatusJog`, `StatusHome`, `StatusCheck` and
  `StatusSleep` for the states coppercli does not drive. Nothing outside `GrblProtocol`
  contains a status word or a substate number.

### proxy → TCP clients · v1 · kind: tcp · port 34000
`SerialProxy` re-exports the raw serial stream to one TCP client at a time, so a remote TUI
can drive the mill. Deliberately **unauthenticated** — it is a byte bridge, and anything
that reaches the port can send arbitrary G-code. Documented as such in the README.
- On client disconnect the proxy sends feed-hold then soft reset, so a dropped connection
  cannot leave the spindle running.
- Only one owner of the serial port may exist: `IsSerialPortInUse` lets the proxy refuse a
  TUI client while the web server holds the `Machine` connection.

### web → browser · v7 · kind: http + websocket · contract: `coppercli/WebServer/WebConstants.cs` (law)
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
  `WriteStartResult` turns a reason into `409` with that text. The browser opens its screen
  on this answer, so a refusal reported later over the socket leaves a screen showing a run
  that never started.
- **v4:** client→server commands live in the `DirectCommands` table alongside the HTTP path
  that runs the same thing, so the two entry points cannot drift. Each entry's `DuringRun` flag says
  whether it may be sent while a job drives the machine; the check is
  `MachineIsBeingDriven`, made once in `RunDirectCommand`. `WsCmdPing` is a real command with
  a handler — it refreshes the client's activity and so is what keeps the stale-client reaper
  off a quiet browser. Every `WsCmd*` is validated by `validateConstants`, and every
  broadcast names its type constant.
- **v5:** the status reports `probing` and `tracingOutline` separately, both derived from
  `IProbeController`. An outline trace is a run like any other - it owns the machine and
  locks the screen - but it measures nothing, so the progress window reads `IsMeasuringGrid`.
  `/api/probe/status` reports the same under `active`.
- **v5:** `/api/probe/stop` answers whether it confirmed the machine stopped, and `500`
  with `CliConstants.StopTimedOutWarning` when it could not. The browser leaves the progress
  view up when the stop is not confirmed, rather than a setup screen that implies the run is
  over.
- **v7:** `/api/door/release` releases a door hold the operator confirmed in the browser's
  door overlay, and refuses while any run is in progress: a run owns the machine until it
  ends, and answering its own prompt is then the only way the cycle start goes out.
  `/api/resume` refuses at a door: a bare cycle start there restarts the spindle, so it goes
  through this path instead, where the click is the confirmation. `doorMessage` carries the
  sentence the overlay shows and `machineUnavailable` gates the jog controls, which
  `needsAttention` left live for a machine that had dropped off the link. The status
  recovers a pending prompt from `PendingPrompt`, so a probe or outline trace parked on the
  enclosure reaches a browser that reloaded, as milling and tool change already did.
- **v6:** `/api/status` sends answers rather than the state to work them out from.
  `machineActivity` is a `MachineActivity` name, and `connected`, `needsAttention`,
  `canPause`, `canResume` and `canReleaseDoor` are derived from it in Core; the three door
  booleans are gone. `doorMessage` carries the sentence the door overlay shows, so the
  browser keeps no copy of the mapping from door state to prompt. The short label in the
  header is the browser's own wording, as every screen's labels are.
  A command that can be refused answers `409` with the reason and is sent over HTTP, because
  a reason cannot come back over the WebSocket — the browser's Resume is an HTTP call for
  that reason and its feed hold is not. See rule `the-browser-draws-what-it-was-handed`.
- **v4 (prompts):** `PendingPrompt` holds the one prompt a run is waiting on. An answer must
  name that prompt's id **and** be one of the `Options` it offered, because answering resumes
  the run on the answering thread and the run can publish its next prompt before the answer
  returns. The id has to reach four places or it protects nothing: the broadcast,
  `DetectToolChange`/`DetectPendingPrompt` in the status, the client's `lastPrompt`, and the
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
  not wired up. The `api` path group was two dozen paths stored twice with nothing comparing
  them, and a wrong path answers 404 anyway. Publish a value only when something consumes or
  verifies it.
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
and the autosave rules are documented there. Do not restate or re-derive them: `ProbeGrid.State`
computes the state from the map and `ProbeGrid.StateOf` does the same where there may not be
one, so the probe screen, the web status and the browser all read one answer. The wire
spelling stays in `WebConstants.cs`; `ComputeProbeState` only names it.
- A height map is only trusted against the `ProbeContext` saved with it: the source file
  and work origin it was measured against. Existence of a file is never the answer to
  "do I have probe data?".
- **v2:** a reading is checked before it is recorded. `ProbeGrid.GetNeighbourDeviation`
  compares it against the mean of its measured orthogonal neighbours; past
  `ControllerConstants.ProbeHeightDeviationToleranceMm` the run retracts to the safe height,
  pauses for the operator, and re-probes the point on resume. Nothing suspect reaches the map
  or the autosave, and the point stays queued — see rule `resume-is-not-approval`.
- **v3:** `AppState.ReadUsableAutosave` is the only code that reads the autosave, and
  `AppState.CurrentProbeGrid` the only source of "is there probe data": the grid in memory,
  or that autosave when nothing is loaded. It reads without adopting, so a status can call it
  (rule `no-side-effect-on-get`), and it returns nothing for a map measured for another file
  or before the origin moved (rule `derived-artifact-records-its-context`).
  The mill start check, `/api/probe/save` and `/api/probe/apply` read it, so a complete map
  sitting unapplied in the autosave refuses the job rather than letting it cut uncorrected.
  `AppState.AdoptProbeGrid` is the only writer of `ProbePoints`, and the setter is private.
  **GAP:** other call sites still read `AppState.ProbePoints` directly, so the wrong choice
  is still available to a reader.
- **v3:** a saved map gets the same checks as a grid built in memory. `ProbeGrid.Load`
  refuses extents that are not finite or not ordered, fewer than two nodes on an axis, a
  point outside the grid, and a height or origin that is not a number. A loaded file reaching
  `InterpolateZ` sets the commanded Z of every cutting move, and a non-finite origin would
  make it applicable to any job.

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
  handle is held to await, never to answer that question, and the two cannot disagree because
  `StartAsync` always ends in a terminal state. `ReleaseAsync` is the only route back to
  Idle; no caller writes its own `Reset` guard. A stop path that holds a run's task awaits
  that run's teardown rather than releasing on top of it: a second `ReleaseAsync` into a
  teardown that has overrun sends another feed hold and soft reset, which cancels the retract
  the first one queued (rule `closing-the-port-does-not-stop-grbl`).
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

- **ui-text-is-a-constant** *(error)* — text the operator reads is named once in `constants.js`
  and referenced, never written at the point of use. A second copy of a label drifts from the
  first as soon as either is edited.
  _Check: `.architecture/rules/check-layering.sh`._

- **no-exception-text-on-screen** *(error)* — an exception message names files, offsets and
  types the operator cannot act on. It goes to the log, and the screen gets a sentence about
  what failed and what to do. Two paths answer a caught exception: `WriteFailure` for the
  browser and `MenuHelpers.ShowFailure` for the terminal, and both log it. `ControllerError`
  is not an exception - it is the run's own wording, written for the operator, and is shown
  as it stands.
  _Check: `.architecture/rules/check-layering.sh`, both languages; mutation-verified._

- **closing-the-port-does-not-stop-grbl** *(error)* — any path that drops the connection
  stops the machine first, writing the bytes straight to the stream, because the worker that
  drains the send queue is gone by then. Any path that ends a run converges on one teardown
  that stops the machine and then retracts: `ProbeController.CleanupAsync`. A retract queued
  before the soft reset is discarded by it, so the test asserts the order.
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
  `AppState.ReadUsableAutosave`, which reads it without adopting it. Removing a load from a
  GET also removes the checks that load carried, so reading and adopting are separate calls
  (`ReadUsableAutosave` and `EnsureProbeDataLoaded`) and the checks belong to the read. Never
  fall back to the raw file state.
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
  For any summary, check whether the data can change in a direction it cannot follow. The
  opposite also applies: two similar-sounding questions are not always one fact.
  `IsRunInProgress`
  ("a run owns the machine") and `MachineIsBeingDriven` ("a workflow is moving the tool now")
  differ exactly at the tool-change pause, where the operator is meant to jog. Before merging
  two predicates, name the case where their answers must differ; if there is one, both stay,
  each derived from the same state. A question is owned the way a field is: "does the
  operator have usable probe data" was answered by whether the autosave parses, by
  `AppState.ProbePoints`, and by a third computation in the web status, so the mill start
  check read the answer that said none while the probe screen read the one that said
  complete. `AppState.CurrentProbeGrid` is the single answer now, and every check and every
  display derives from it. A derived answer needs an owner as much as a field does. A
  question with more cases than the branches reading it fails the same way: GRBL's `Door` has
  four substates and about ten places branched two ways, so `Door:3` — restoring from the
  park, with the machine moving — read as "holding" on every screen.
  `MachineWait.GetDoorState` names the cases and every screen reads it. The remainder case
  is defined as the remainder (`IsDoorOpen`), so the three predicates always cover `Door`,
  and a substate GRBL adds falls into the case that prompts the operator.
  This applies to the constants a rule is expressed in and the operations that carry it out,
  or the duplicates move down a level where nothing reports them. One clearance height existed
  as three constants of the same value, and the move to it was written five times: the rule
  that a machine holding at the door must not be sent one was in two of them and missing from
  the third. `SafeClearanceZ` is the height and `ControllerBase.RetractToSafeZAsync` is the
  move. For each literal and each move, check whether it could legitimately differ from the
  others, and name it where it could not.
  _Check: `coppercli.Tests/DepthAdjustmentTests.cs`; `coppercli.Tests/ProbeControllerTests.cs`
  (`PhaseEnums_DoNotRestateTheRunLifecycle`); `coppercli.Tests/WebServerSequenceTests.cs`;
  `coppercli.Tests/MillingControllerTests.cs` (`StopAtDoor_QueuesNoRetract`)._
  _History: 2026-08-a-pause-flag-that-outlived-its-job,
  2026-09-phases-that-restated-the-lifecycle,
  2026-09-three-owners-of-do-i-have-probe-data,
  2026-09-a-two-state-door-in-ten-places,
  2026-09-one-height-under-three-names._

- **a-new-distinction-lands-with-its-callers** *(error)* — splitting one question into more
  cases moves every place that branched on the old question, in the same change.
  `MachineWait.IsDoorResuming` sat in Core for a round with no caller outside it while three
  UIs still branched the door two ways: it compiled, the suite stayed green, and the defect
  it was written to fix was still on every screen. A predicate, enum member or field with no
  reader outside the file that defines it is either unadopted or dead, and both are findings.
  Where the new cases belong to one state, the reader is one function returning that state,
  not a branch per screen.
  _Check: `.architecture/rules/check-layering.sh` (a `MachineWait` predicate with no caller
  outside `MachineWait.cs`); reader judgment of any case added to an existing question._
  _History: 2026-09-a-two-state-door-in-ten-places,
  2026-09-two-resume-windows-one-door._

- **one-owner-for-the-door-policy** *(error)* — which door states the operator can answer,
  how many refused releases are enough, and which states are waited out instead is
  `MachineWait.ClearDoorHoldAsync`, and nothing writes that loop again. Callers pass in how
  to ask and how to announce, so a run prompts and emits progress while a terminal screen
  draws an overlay. Owning `GetDoorState`, `CanReleaseDoorHold`, `GetDoorMessage` and
  `ReleaseDoorHoldAsync` is not owning the policy that composes them: the keyboard flush,
  the Escape poll and the withdrawn message belong to the loop, not to its parts. The
  browser's endpoint validates and releases once, because the page redraws every broadcast
  interval and the operator clicks again.
  _Check: `coppercli.Tests/DoorClearTests.cs` and `coppercli.Tests/ControllerBaseTests.cs`;
  both are mutation-verified._
  Every caller supplies a way out: a run its token, a screen an `onPoll`. The waits return at
  once on a cancelled token, so a loop with no way out spins instead of blocking.
  _History: 2026-09-one-door-policy-two-implementations,
  2026-09-a-retry-loop-with-nothing-to-wait-on._

- **a-loop-proves-its-own-end** *(error)* — a loop that re-derives its own work list needs a
  termination rule that does not depend on the work changing the state the list is derived
  from. `SessionRestore.AskPendingSteps` asks the next pending startup question after each
  answer, and three of the four questions are derived from state their answer leaves
  untouched: reloading the file stores the same path again, keeping the height map leaves the
  autosave on disk, and trusting the origin writes a different field from the one the
  question reads. Every answer defaulted to yes, so the first screen repeated forever. The
  set of topics already asked lives in that method, not in the caller, so a front end cannot
  leave it out.
  _Check: `coppercli.Tests/SessionRestoreTests.cs`
  (`TheStartupSequence_RunsOutWhateverTheAnswer`, a theory over both answers;
  mutation-verified against both the filter and the caller)._
  _History: 2026-09-a-prompt-loop-with-no-memory._

- **a-run-owns-the-file-it-is-streaming** *(error)* — nothing replaces the loaded G-code
  while a run is in progress. A run streams from `Machine.File` and tracks where it is by
  line number, so a new file resets that to the start and the job carries on from the top of
  the program. `Machine.SetFile` refuses only while `Mode` is `SendFile`, which a job paused
  at a tool change is not: the guard is `AppState.LoadGCodeIntoMachine`, the one way G-code
  reaches the machine, because that layer can see the controllers. Setting Z0 at a tool
  change reached it through re-applying the height map, so `HandleWorkZeroChange` leaves the
  map and the file alone during a run, and zeroing X or Y is refused outright.
  _Check: `coppercli.Tests/WebServerSequenceTests.cs`
  (`ARunInProgress_KeepsTheFileItIsStreaming`, `ZeroingDuringARun_KeepsTheMapTheRunIsCutting`);
  both are mutation-verified._
  _History: 2026-09-two-resume-windows-one-door._

- **a-discard-puts-the-original-back-first** *(error)* — dropping a height map reloads the
  original G-code before AppState forgets the map, and refuses when it cannot. Applying a map
  rewrites `Machine.File`; forgetting the map first and then failing to reload leaves the
  corrections in the G-code with nothing saying so, and the next apply doubles them.
  `AppState.DiscardProbeData` does the reload through `RemoveMapFromLoadedGCode` and returns
  `ErrorMapStuckInGCode` when the source file is gone or will not load.
  _Check: `coppercli.Tests/WebServerSequenceTests.cs`
  (`ZeroingXYWhenTheMapCannotComeOut_SaysSo`), mutation-verified._
  _History: 2026-09-two-resume-windows-one-door._

- **never-auto-clear-a-safety-gate** *(error)* — software never clears a state that exists
  to require human confirmation. The enclosure door blocks a job and only the operator
  resumes it. Homing is deliberately impossible to skip: without it, `G53` retracts have
  no reference to retract to. _Check: reader judgment; `coppercli.Tests/SafetyGuardTests.cs`._
  _History: 2026-08-software-clearing-safety-gates,
  2026-09-a-wait-that-bailed-on-the-state-it-was-waiting-out,
  2026-09-two-resume-windows-one-door._

- **no-cached-physical-measurement** *(error)* — never cache a measurement of a physical
  thing past any event where a human can silently change it. The tool-setter reference
  length is measured every time, never persisted. The event is not only the end of a session:
  the
  setter's trigger height was cached to rapid toward, but it is probed once with the old tool
  and once with the new, so the rapid always aimed one tool at another tool's height. Ask what
  the number describes and whether it still describes the thing about to move — not whether
  the line is reachable. _Check: reader judgment._
  _History: 2026-08-cached-reference-tool-length,
  2026-08-a-rapid-aimed-at-the-other-tools-trigger-height._

- **derived-artifact-records-its-context** *(error)* — an artifact computed from a setup
  carries that setup with it and is re-validated against it before use. A height map stores
  its `ProbeContext` (source file, work origin); a map with no recorded context is `Unknown`
  and is checked, never assumed usable. The check runs in one place,
  `AppState.ReadUsableAutosave`, so nothing can reach the file without it, and a map that
  names a source file must carry a readable origin: a non-finite origin compares false
  against every tolerance and would leave the map `Unknown`, which nothing refuses.
  _Check: `coppercli.Tests/ProbeContextTests.cs`; `coppercli.Tests/ProbeGridLoadTests.cs`._
  _History: 2026-08-probe-data-inferred-from-a-file-on-disk,
  2026-09-three-owners-of-do-i-have-probe-data._

- **a-loader-enforces-the-constructors-invariants** *(error)* — a file read back into an
  object that controls machine motion gets the same checks the constructor makes.
  `ProbeGrid.Load` refuses what `RequireUsableShape` refuses — extents that are not finite or
  not ordered, fewer than two nodes on an axis — plus point indices outside the grid and
  heights that are not numbers, because a loaded map sets the commanded Z of every cutting
  move. A field the applicability check depends on is required, not optional: see
  `derived-artifact-records-its-context` for the origin case.
  _Check: `coppercli.Tests/ProbeGridLoadTests.cs`._
  _History: 2026-09-three-owners-of-do-i-have-probe-data._

- **read-g54-explicitly** *(error)* — before any `G10 L2 P1`, query G54 itself
  (`RefreshWorkOffsetsAsync`); never use the combined `WorkOffset`, and never derive it from
  `MachinePosition − WorkPosition`. Undo an offset relatively, never by restoring an
  absolute snapshot; a tool change legitimately owns the same register.

  The depth adjustment is always measured from the zero the operator set: the baseline is
  `G54.Z` minus whatever an earlier run left outstanding, so asking for 0.05 gives 0.05
  however the run before it ended. A restore the machine refuses is reported by amount.
  _Check: `coppercli.Tests/DepthAdjustmentTests.cs`._  _History: 2026-08-workoffset-is-not-g54._

  **Decided, not open** (Thomer, 2026-09), so later passes do not re-raise them:
  - Minus is down and cuts deeper, matching the Z sign. The buttons stay `-` and `+`.
  - The outstanding amount lives on the milling controller and is deliberately temporary.
    It survives between runs in a session, not a restart or a disconnect; the operator is
    told to set Z zero again, which is the remedy.
  - The adjustment shifts the work origin rather than being baked into the streamed Z the
    way the height map is. The origin shift is the approach; do not replace it.

- **monotonic-time-and-event-counts** *(error)* — timeouts are measured on a clock that only
  moves forward: `Stopwatch` for an interval inside one method, `Environment.TickCount64` for
  a timestamp a second thread reads. Never `DateTime.Now`. A clock must only answer a question about time. "Is the peer still
  reporting?" is answered by counting events (`StatusReportCount`), not by timing them, and
  "is this probe reading usable?" is answered from the measurement — the height against its
  measured neighbours, `ProbeGrid.GetNeighbourDeviation` — not from how long the probe took.
  Before timing a code path, check what else is inside the interval: `RetractZAsync` and
  `MoveToPointAsync` deliberately return without awaiting so GRBL can buffer them, so a
  stopwatch around `G38.2` spanned the previous retract, the traverse, and the descent as
  well. `coppercli.Core` shows nothing, so it reads no wall clock at all.
  _Check: `.architecture/rules/check-layering.sh`; reader judgment of any `Stopwatch`
  that gates a decision._
  _History: 2026-08-synchronized-queues-and-wall-clock-deadlines,
  2026-09-a-stopwatch-timing-everything-but-the-probe._

- **no-live-collections-across-threads** *(error)* — never expose a live mutable collection
  to another thread; own it and hand out snapshots. `Queue.Synchronized` does not
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
  _Check: `.architecture/rules/check-layering.sh`._

- **machine-readiness-is-the-controllers** *(error)* — the controller answers whether the
  machine's own state allows a job, and no UI does. `MillingController` asks the operator about
  the enclosure, releases the hold, and settles the machine; a gate in front of it refuses a
  machine the controller would have recovered. The UIs keep `CheckMillCanStart`, which
  answers the different question of whether the *job* is fit to run — connection, file, height
  map — and `MenuHelpers.GetMachineBlocker` for the part that is about the machine rather
  than the job: an alarm to clear, or a machine asleep. Probing runs the same two checks, so
  the two menu entries cannot be enabled and refused on different grounds.
  _Check: `coppercli.Tests/WebServerSequenceTests.cs`
  (`ADoorHoldDoesNotBlockTheMill_TheControllerPromptsInstead`) is the guard — it drives
  the real HTTP API against a real `Machine` and fails on the gate however it is written;
  `.architecture/rules/check-layering.sh` catches the two known spellings, and
  `a-grep-is-not-the-guard` says why that alone is not enough._
  _History: 2026-09-a-readiness-gate-in-front-of-the-controller,
  2026-09-a-two-state-door-in-ten-places._

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
  `constants.js`. A value only one side needs is not published here: publishing it creates a
  second copy that nothing compares.
  _Check: reader judgment; `validateConstants()` reports at runtime._
  _History: 2026-09-a-prompt-id-that-could-not-stop-the-second-tap._

- **ws-message-types-updated-in-four-places** *(error)* — a new WebSocket message type is
  added to `WebConstants.cs`, `constants.js`, the `wsMessageTypes` object in
  `GetSharedConstants()`, and the validation list in `helpers.js`. Three out of four is a
  mismatch that only shows up at runtime. The same four points apply to the `WsCmd*`
  client→server commands under `commands`, and to the `WorkZeroOutcome` names under
  `heightMapOutcomes`. An unvalidated command breaks jogging on a rename, silently; an
  unvalidated outcome leaves the operator with no word on what became of the height map.
  _Check: `.architecture/rules/check-layering.sh`, which compares all four: the names
  `constants.js` declares against the ones `helpers.js` validates, and the count declared in
  `WebConstants.cs` against the count declared in `constants.js`._

- **api-paths-are-constants** *(error)* — no `fetch()` hardcodes an `/api` path and no
  handler hardcodes one; both sides use their named constant.
  _Check: `.architecture/rules/check-layering.sh`._

- **culture-invariant-gcode** *(error)* — every number sent to the machine is formatted
  through `GCodeFormat.Inv`. An interpolated string on a comma-decimal locale emits
  `Z-1,000`, which GRBL rejects.
  _Check: `coppercli.Tests/CultureInvariantGCodeTests.cs` pins the behaviour;
  `.architecture/rules/check-layering.sh` catches a coordinate formatted without the wrapper
  where G-code is built._

- **settings-rename-needs-migration** *(error)* — renaming a `MachineSettings` property
  adds a `SettingsMigrations` entry in `Persistence.cs`, with a version comment. Do not
  rely on backwards compatibility; migrate and use the new name everywhere.
  _Check: reader judgment of the diff._

- **resume-is-not-approval** *(error)* — a return value that decides whether to commit a
  suspect measurement must say *why* the run is continuing, which a boolean cannot. "Keep
  going" does not distinguish "the reading was within tolerance" from "the operator
  intervened and resumed". `RetractAndCheckHeightAsync` returned
  `!ct.IsCancellationRequested`, which the caller read as "record it", so a height taken
  before the operator cleared the debris went into the map and the autosave, and the grid
  reported itself complete. It is now the three-valued `HeightOutcome`
  (`Accepted` / `Remeasure` / `Cancelled`), and a resume re-probes the point. In general, an
  operator's resume returns the run to the step that raised the prompt, not past it. _Check: `coppercli.Tests/ProbeControllerTests.cs` — the locking test is
  mutation-verified; returning `Accepted` in place of `Remeasure` reproduces the defect._
  _History: 2026-09-a-stopwatch-timing-everything-but-the-probe,
  2026-09-two-resume-windows-one-door._

- **no-bail-out-on-the-awaited-state** *(error)* — a wait helper must not treat the state the
  caller is waiting to leave as a bail-out condition. Such a wait can never succeed, and it
  returns a plain `false` that the caller's retry turns into an apparent hang.
  `EnsureDoorClosedAsync` called `WaitForIdleAsync`, which counts Door as `IsUnavailable` and
  gave up on its first poll, so the door prompt reappeared milliseconds after being answered.
  Door is in `IsUnavailable` because most callers are not waiting it out; a caller that is
  gets its own wait — `WaitForDoorReleasedAsync`, and the catch-up inside
  `ReleaseDoorHoldAsync`. Read the set from `MachineWait.IsUnavailable`, and check it against
  the state you are starting from before reusing a wait.
  _Check: `coppercli.Tests/MachineWaitTests.cs`._
  _History: 2026-09-a-wait-that-bailed-on-the-state-it-was-waiting-out._

- **a-test-must-be-able-to-fail** *(error)* — a test is only worth keeping if some plausible
  defect makes it fail. Subscribing to an event without raising it, or passing an enum
  literal to `Enum.IsDefined`, asserts nothing; seventeen such tests were removed or replaced.
  Where a test guards a named rule, verify it by mutation. A guard test checks the *behavior*
  the rule is about, not the spelling it was written against —
  `PhaseEnums_DoNotRestateTheRunLifecycle` compared names by equality and let
  `WaitingForOperator` past `WaitingForUserInput`, missing both members that had actually
  caused a defect. And no test mutates process-wide state: xUnit runs classes in parallel and
  ignores an unresolvable `[Collection]` name without warning, so
  `CultureInfo.DefaultThreadCurrentCulture` set in one class decided whether tests in other
  classes passed. Scope it to the thread (`CultureInfo.CurrentCulture` flows across `await`).
  A test synchronizes on the event it is about, not on a delay or an earlier state: a door
  test that opened the enclosure after the run reached `Running` was handled by a different
  code path in 13 runs out of 13, so the branch it was written for could be deleted and it
  stayed green. It now opens the door from the stream's own `FilePositionChanged` at a named
  line. Which branch a test exercises is measured by deleting that branch and checking the
  test fails.
  _Check: reader judgment of new tests; `.architecture/rules/check-layering.sh`._
  _History: 2026-09-seventeen-tests-that-asserted-nothing,
  2026-08-a-test-suite-that-had-not-compiled-since-february,
  2026-09-a-two-state-door-in-ten-places._

- **an-element-the-code-writes-to-exists** *(error)* — every id the browser looks up is on
  `index.html`, because `getElementById` returns null otherwise. A write guarded with
  `if (el)` then does nothing silently; an unguarded one throws and stops every later line in
  the same handler. Six such writes existed at once. `check-layering.sh` reads
  `getElementById` and its `$` shorthand; `page.test.mjs` also covers the helpers that take
  an id (`setText`, `addClass`, `updateButtonState`) and the arrays of ids `jog.js` iterates.
  _Check: `.architecture/rules/check-layering.sh`, and
  `coppercli.Tests/browser/page.test.mjs`, whose stub page takes its ids from
  `index.html` and throws on one the page does not have._
  _History: 2026-09-a-view-model-the-browser-could-not-draw._

- **the-browser-draws-what-it-was-handed** *(error)* — a question about the machine is
  answered once, in Core, and sent as a value. `status` carries GRBL's raw word for display
  only. A UI that derives an answer from it holds a second definition, and the two then
  disagree: the door had three states in Core and two in the browser.
  _Check: `.architecture/rules/check-layering.sh` greps for the comparisons a browser would
  write. `WebServerSequenceTests.TheStatus_ReportsWhichControlsApply` and
  `MachineWaitTests.EveryActivity_HasControlAnswers` check the payload's values.
  `WebServerSequenceTests.EveryBrowserActivityName_MatchesTheEnum`
  checks the browser's copy of the activity names against the enum.
  `coppercli.Tests/browser/status.test.mjs` runs the real browser modules against a stub
  page and checks what they write to it._
  _History: 2026-09-a-view-model-the-browser-could-not-draw,
  2026-09-two-resume-windows-one-door._

- **a-grep-is-not-the-guard** *(error)* — a rule about behavior is guarded by a test that
  exercises the behavior; a grep in `check-layering.sh` catches one spelling of it and no
  more. Prove a new check by bypassing it before trusting it: the readiness gate
  came back past both of its greps as `if (MachineWait.IsUnavailable(machine)) return ...`,
  and what refuses that is
  `WebServerSequenceTests.ADoorHoldDoesNotBlockTheMill_TheControllerPromptsInstead`,
  driving the real API against a real `Machine` over a loopback `FakeGrbl`. A grep over a
  path that does not exist also finds nothing and reports success, so the script asserts
  every path it checks is present before it checks anything
  (`check-runs-at-the-repository-root`).
  _Check: bypass each new grep by hand; `.architecture/rules/check-layering.sh` exits
  non-zero outside the repository root._
  _History: 2026-09-a-two-state-door-in-ten-places._

- **fail-safe-on-uncertainty** *(error)* — when a safety-relevant step cannot be confirmed
  (a retract that GRBL rejected, a probe that did not report contact, a status that never
  arrived), the job stops rather than continuing. A rejected safety retract must never be
  swallowed, and a run whose final lift was not confirmed must never report itself finished.
  `ControllerBase.LiftAfterStopAsync` is where every controller decides that, so the three
  cannot answer it differently. A stop at the door never counts as confirmed: the soft reset
  clears the hold, so the tool's position is unknown.
  _Check: `coppercli.Tests/SafetyGuardTests.cs`
  (`AMillWhoseFinalRetractIsNotConfirmed_NeverReportsItFinished`) and
  `coppercli.Tests/ProbeControllerTests.cs`
  (`ATraceWhoseSafetyRetractIsNotConfirmed_NeverReportsItFinished`)._

- **an-undecided-choice-is-recorded-not-shipped** *(error)* — where two answers are both
  defensible and neither follows from the code, the change records the fork as a
  **GAP (undecided)** on the interface it belongs to, naming both answers and what each
  costs, and leaves the behavior as it stands. The owner picks. GRBL has one resume, so the
  cycle start that releases a door hold releases a feed hold with it and a run paused at the
  door carries on cutting while the screen reads Paused; re-asserting the hold after the
  release and ending the pause instead were both attempted and neither settled, so
  `controllers → machine` carries the question. What the operator is promised is the owner's
  call, and the contract holds the question until they make it.
  _Check: reader judgment; a GAP (undecided) names both answers and their cost._
  _History: 2026-09-a-two-state-door-in-ten-places._

- **fake-answers-like-the-machine** *(error)* — a test double reproduces the machine's
  observable answer to each command under test. `FakeMachine` reporting `Idle` after every
  pause line hid that GRBL answers M0/M1 with `Hold:0` while M6 never reaches it, so a gate
  that could not fire on hardware passed 338 green tests. One status
  hardcoded across a family of commands erases the distinction the code is deciding on, and
  the suite then agrees with the code because both read the same invention. The direction of
  the difference decides what it hides: a double more permissive than the machine hides the
  caller that needed refusing. `FakeMachine.SimulateMoveAsync` claimed `Run` for every move
  and so drove the tool through an open enclosure, and both doubles resumed `Door:0` straight
  to `Idle` where GRBL returns to the state the door interrupted — `Run` for a streaming job
  — which made a feed hold re-asserted after the release look like it worked. Ask what the
  machine refuses, not only what it answers.
  `coppercli.Tests/Fakes/DoorModel.cs` is GRBL's door rules for all three doubles, so a
  substate they must answer differently is written once.
  _Check: reader judgment of `coppercli.Tests/Fakes/`;
  `coppercli.Tests/FakeMachineDoorTests.cs`._
  _History: 2026-08-a-fake-that-answered-idle-to-every-pause,
  2026-08-a-test-suite-that-had-not-compiled-since-february,
  2026-09-a-two-state-door-in-ten-places,
  2026-09-two-resume-windows-one-door._

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
  `coppercli.Tests/ControllerBaseTests.cs` (`AnsweringAPrompt_PublishesTheNextBeforeItReturns`);
  reader judgment of any handler that both answers and redraws._
  _History: 2026-09-a-prompt-id-that-could-not-stop-the-second-tap,
  2026-09-two-resume-windows-one-door._

- **no-magic-values** *(error)* — every literal with semantic meaning is a named constant
  in the file that owns it: `coppercli.Core/Util/Constants.cs` (Core-wide),
  `coppercli/CliConstants.cs` (CLI/UI), `coppercli.Core/Util/GrblProtocol.cs` (GRBL wire),
  `coppercli/WebServer/WebConstants.cs` (HTTP/WS wire),
  `coppercli.Core/Controllers/ControllerConstants.cs` (controller prompts, errors, timeouts),
  `coppercli.Core/Settings/SettingRange.cs` (what a refused setting is told, and the name and
  unit of each one), `wwwroot/js/constants.js` (client).
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

_Beyond these there is no roadmap: zero `TODO`/`FIXME` markers in the tree, no design
doc, no diagrams, no reachable issue backlog. Direction is set per-session by the owner and
lives only in the prompt log, which is why this memory exists._

## Known gaps in the record

Each is something a human must supply.

- **The prompt log is not committed.** `prompts/` is gitignored, so the blunt record of
  *why* — the reversals, the rejected designs, the owner's steering — does not survive a
  fresh clone. `.architecture/history/` now carries what could be recovered from it.
- **`CLAUDE.md` still lags the tree, though less than it did.** Fixed 2026-09: the
  `MachineWait.HomeAsync` example points at the file rather than repeating a body that had
  drifted, and the reference to a `StatusHelpers.cs` that does not exist is gone. Still
  outstanding: `coppercli.Tests/` is absent from the project structure, and the document
  names none of `AtomicFile`, `ProbeContext`, `HomingOutcome`, `EtaEstimator`, `GCodeFormat`,
  `GrblRejection`, `SessionRestore`, `PassThrough` or `RequestGuard`.
- **The OpenCNCPilot reference implementation is unavailable here.** `CLAUDE.md` instructs
  agents to consult `~/src/OpenCNCPilot/` for GRBL and probing questions; that tree is not
  present, so some upstream semantics (e.g. `ProbeOptions.MaxDepth`) cannot be settled.
- **The only end-to-end user guide is off-repo** — <https://thomer.com/pcb-nomad3>,
  unversioned against the app. Nothing in the tree will catch it if it drifts.

---

**History:** `.architecture/history/` — one lesson per file, append-only. Read it before
changing an interface; the entries record approaches already tried and abandoned. Grep the `Touches:`
line at the foot of each entry for the interface, rule, or path you are about to touch.
