# Architecture Contract — v1

> Binding. The conforming pass treats every clause here as law. Changing a contract
> bumps its version and adds a History entry under `.architecture/history/`.

coppercli drives a GRBL CNC mill over a serial port to cut PCBs. Two constraints follow
from that: **a wrong move breaks a drill bit or ruins the copper**, so correctness outranks
convenience; and **the operator is standing at the machine**, often holding a phone, so
anything that costs them a keystroke at the wrong moment is a design defect.

Derived from [OpenCNCPilot](https://github.com/martin2250/OpenCNCPilot) — consult it for
GRBL communication and probing questions. It has no tool-change (M6) support, so
coppercli's tool-change logic has no reference implementation.

## Parts

- **core-comm** (`coppercli.Core/Communication/`) — holds the serial link and the machine's
  live state. `Machine` parses GRBL status reports, queues commands, streams files;
  `SerialProxy` forwards serial data to a TCP listener. References no UI type.
- **core-controllers** (`coppercli.Core/Controllers/`) — holds every multi-step machine
  workflow as an explicit FSM: `ProbeController`, `MillingController`,
  `ToolChangeController`, over `ControllerBase`. `MachineWait` holds every status predicate
  and every wait/poll loop. Controllers emit events; they never render.
- **core-gcode** (`coppercli.Core/GCode/`) — parses, models, and regenerates toolpaths.
  `GCodeParser`, `GCodeFile`, `ProbeGrid`, `ProbeContext`. Pure computation, no I/O.
- **core-util** (`coppercli.Core/Util/`) — `Constants`, `GrblProtocol`, `GCodeFormat`,
  `Vector3`/`Vector2`, `AtomicFile`, `GrblCodeTranslator`.
- **core-settings** (`coppercli.Core/Settings/`) — `MachineSettings`, `SessionState`, and
  `SettingRange`: what each numeric setting may be, and how to read and write it. A range
  is declared beside the setting it bounds, so the settings screen, the web API and the
  loader all check against one table. Nothing else here: the DTOs stay plain.
- **app-state** (`coppercli/AppState.cs`, `Persistence.cs`, `SessionRestore.cs`,
  `Program.cs`) — process-wide composition root: the live `Machine`, the lazily-built
  controllers, the loaded file and probe grid, and the only code that touches the app's
  own files on disk.
- **tui** (`coppercli/Menus/`, `coppercli/Helpers/`) — the Spectre.Console keyboard UI.
  Presentation and input only; delegates work to controllers.
- **macro** (`coppercli/Macro/`) — parses and runs `.cmacro` scripts, driving the TUI's
  own operations unattended.
- **web-server** (`coppercli/WebServer/CncWebServer.cs`, `WebConstants.cs`,
  `RequestPolicy.cs`, `MachineHold.cs`) — embedded `HttpListener` serving the browser UI,
  the `/api/*` endpoints, and the `/ws` socket. Presentation and transport; delegates work
  to controllers. `MachineHold` decides whether the server or a proxy terminal has the
  serial port (rule `server-holds-the-machine`).
  *Finding: `CncWebServer.cs` combines request routing, workflow setup, and client updates.
  See the GAP under `ui → controllers`.*
- **web-client** (`coppercli/WebServer/wwwroot/`) — vanilla ES-module browser UI, embedded
  in the assembly as a resource. No build step, no framework, no external CDN.
- **tests** (`coppercli.Tests/`) — two suites. xUnit drives Core through `IMachine` fakes,
  plus app-layer state via `InternalsVisibleTo`. `coppercli.Tests/browser/` runs the real
  `wwwroot/js` modules against a stub page under `node --test`, with no `package.json` and
  nothing to install; `dotnet test` does not see it, so CI runs it as its own step.
  `Fakes/FakeGrbl.cs` emulates GRBL on a loopback port that the real `Machine` connects to,
  so `WebServerFixture` starts the real server and controllers over it and drives `/api/*`
  as a browser does. What reached the machine is read back from `FakeGrbl.Received`, in
  order.

## Interfaces

### controllers → machine · v5 · kind: function · contract: `coppercli.Core/Communication/IMachine.cs` (law)
Every controller reaches the machine only through `IMachine`. That interface file is the
contract; do not restate it here. It exists so controllers are testable without hardware;
`coppercli.Tests/Fakes/` supplies the doubles.
- Status predicates (`IsIdle`, `IsAlarm`, `IsHold`, `IsDoor`, `IsUnavailable`) and every
  wait/poll loop live in `MachineWait`. A controller that spells out `machine.Status == "Idle"`
  or writes its own polling loop is a violation.
- **v2:** before reusing a wait, check which states make it return early.
  `MachineWait.IsUnavailable` includes alarm, the three door states, asleep and no link.
  Hold is *not* in it. So `WaitForIdleAsync` cannot wait out a door, and a caller that must
  wait one out gets its own wait (`WaitForDoorReleasedAsync`, and the report-counted
  catch-up inside `ReleaseDoorHoldAsync`). See rule `waits-do-not-abort-on-awaited-state`.
- **v3:** the door has three states, not two. `MachineWait.GetDoorState` returns which one
  as a `DoorState`; the three predicates behind it are private, so one function reads the
  partition.
- **v4:** every question a screen asks about the machine is answered in `MachineWait` and
  sent as a value: `GetActivity` returns a `MachineActivity`, and `IsResponding`,
  `NeedsAttention`, `CanPause`, `CanResume` and `IsUnavailable` are derived from it.
  `/api/status` sends `needsAttention`, `canPause` and `canResume` under their own names,
  `IsResponding` as `connected`, and `CanReleaseDoorHold` as `canReleaseDoor`.
  The terminal calls the same functions. The payload sends `activity.ToString()`, so the
  member names are part of the wire contract. GRBL's status word is still sent, for display
  only. See rule `browser-uses-core-status-values`.
- **v5:** whether a command ran is read from GRBL's answer to it, which
  `IMachine.SendAsync` returns as a `GrblReply`; `GrblAnswer` says what each answer means.
  `SendLine` remains for a caller that does not wait. See rule
  `grbl-answers-its-own-commands`.
- **GAP (undecided):** GRBL has one resume, so the cycle start that releases a door hold
  releases a feed hold with it. A run that was paused when the door opened therefore
  continues cutting while `ControllerState` still reads `Paused` and the screen still
  offers Resume. Two answers are open and neither is derivable from the code: re-assert
  the feed hold immediately after the release (the tool moves for GRBL's reaction time),
  or let the release end the pause, which is what the prompt the operator answered says it
  does. _Target: the owner picks one; the prompt wording follows it._
  `MachineWait.ReleaseDoorHoldAsync` is the only place coppercli releases a door hold (a
  client of `SerialProxy` writes its own bytes to the port and is outside this), and it sends
  the cycle start only on GRBL's own reading of the switch; `ControllerBase.EnsureDoorClosedAsync`
  is the only place a workflow asks the operator for it.
- `MachineWait.HomeAsync` is the only place `IsHomed` is set **true**, and it sets it on
  GRBL's `ok` for the `$H` — the one answer that means the cycle finished. It is set
  false only inside `Machine` itself, on connect, disconnect, and any reset — one coppercli
  sends, or one GRBL announces with its banner — the events after which the machine no longer
  has a valid reference frame. No UI assigns it.
- **GAP:** the `IMachine` interface stops at Core. `AppState.Machine`, `CncWebServer._machine`,
  `MachineCommands`, and `JogHelpers` are all typed to the concrete `Machine`, because
  `IMachine` was scoped to what controllers need — it lacks `Connect`/`Disconnect`,
  `SetFile`, `Jog`, `EnableAutoStateClear`, `FeedOverride*`. Core is testable,
  **both UIs are not**. Target: `IMachine` describes the transport contract, and the app
  layer holds an `IMachine`.
- **GAP (open, raised to the owner 2026-09-23):** the milling loop can stall with the
  spindle on after GRBL rejects a streamed line. Not yet investigated; the fix belongs in
  `MillingController` under rule `fail-safe-on-uncertainty`.
- **GAP:** `Machine` raises 17 events with a bare `action?.Invoke(...)` — no dispatcher, so
  every handler runs inline on the raising thread and blocks GRBL streaming.
  `Program.SetupEventHandlers` does Spectre console I/O from there, and its `LineReceived`
  handler calls `Environment.Exit(0)` on a proxy force-disconnect —
  terminating the process from inside the serial read loop, bypassing every `finally`
  including `Machine`'s own teardown. (The proxy does send feed-hold then soft reset on
  client disconnect, so the spindle is stopped by the other side; the process teardown is
  what is skipped.) Some events have no subscribers at all.

### ui → controllers · v6 · kind: function + event · contract: `coppercli.Core/Controllers/IController.cs` (law)
Both UIs start a workflow by configuring a controller, subscribing to its four events
(`StateChanged`, `ProgressChanged`, `UserInputRequired`, `ErrorOccurred`), and awaiting
`StartAsync`. Events are **synchronous** — the handler runs inline and the controller waits,
so a handler must not block on the UI thread's own input loop.
- The FSM's legal transitions are the `ValidTransitions` table at the top of
  `ControllerBase.cs`; that table is law. An illegal transition throws. Use
  `TryTransitionTo` where another thread may already have made the transition — an
  operator's pause arriving between a test and a `TransitionTo` would otherwise throw out
  of the run they were intervening in.
- **v6:** `StopAsync` on a run in progress cancels it and completes once `StartAsync` has
  finished, so the run's own teardown is the only one. It cleans up by itself only when no
  run is in progress.
- **v5:** `StartAsync` leaves the controller in a terminal state however the run ended, so
  the task completing and the run being over are one fact. `ReleaseAsync` is the only way
  back to Idle: it stops an unfinished run first, then resets. Every start and every stop
  calls it, and nothing calls `Reset` directly, because `Reset` refuses a controller that
  still claims a run. See rule
  `releaseasync-returns-controller-to-idle`.
- **v3:** a `*Phase` enum names only the step of work a run is on. Whether the run is
  paused, waiting on the operator, finishing, finished, canceled or failed is `ControllerState`,
  read through the `ControllerBase` predicates (`IsPaused`, `IsActive`, `HasFinished`,
  `IsWaitingForOperatorState`). A phase member answering a lifecycle question is a second
  copy written on a separate path — see rule `one-field-per-fact` and
  `phase-enums-restated-the-run-lifecycle`. Waiting out a pause is
  `ControllerBase.WaitWhilePausedAsync`, not a loop per call site.
- **v2:** a controller instance serves the whole session, so `ControllerBase` declares
  `protected abstract void ResetRunState()` and calls it at the start of every run as well
  as from `Reset()`. Implementing it is how a new controller states what belongs to a run —
  see rule `per-run-state-cleared-at-run-start`.
- **v4:** distinguish whether a run holds the machine from whether it is moving the tool.
  `ControllerBase.IsRunInProgress` answers "is a run holding the machine": it counts
  `WaitingForUserInput` and `Completing`, and it is what refuses a second start and holds the
  serial port. `CncWebServer.MachineIsBeingDriven` answers "is a workflow moving the tool
  now": it refuses a jog or a goto, and it permits both during the pause milling holds in
  for a tool change, because that is when the operator is asked to jog to the surface and
  set Z0. Both derive from `ControllerState`; neither is written in terms of the other.
  Every run, including an outline trace, transitions the FSM. Both checks miss a workflow
  that sets `Phase` while leaving `State` at `Idle`.
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
  `web → browser` and `prompt-id-did-not-stop-a-double-tap`.
- The M0/M1 pause leaves the tool where the hold left it and the spindle running, and the
  prompt says so. A feed hold resumes the buffered motion from wherever the machine is, so
  retracting and returning would require the same position to avoid shifting the cut.
  A tool change can retract because it stops the
  stream and restarts it.
- **GAP:** the "configure options from settings, load the grid/file,
  subscribe, run, unsubscribe" sequence is written out separately in
  `CncWebServer.cs`, in `Menus/ProbeMenu.cs` / `Menus/MillMenu.cs`, and in
  `Macro/MacroRunner.cs`. `ProbeOptions.FromSettings` and `ToolChangeOptions.FromSettings`
  are defined once in Core and every caller uses them; each front end repeats the surrounding
  setup sequence.
  Target: one orchestration entry point per workflow that both UIs call, leaving each UI
  with presentation only. The workflows run in Core, but the repeated setup sequence has
  already produced differences between the two UIs (see `stale-work-zero-and-height-map`).

### machine → GRBL · v4 · kind: serial wire protocol · contract: `coppercli.Core/Util/GrblProtocol.cs` (law)
Status strings, real-time bytes, and command words are named there and nowhere else.
Targets GRBL 1.1f; 0.8/0.9/1.0 are known-incompatible.
- Every number sent to the machine is formatted through `GCodeFormat.Inv`; see rule
  `culture-invariant-gcode`.
- **v2:** closing the port does not stop the machine - GRBL keeps working through its
  planner buffer. `Machine.WriteStopSequence(Stream)` is the one definition of the
  stop: feed hold, then soft reset, each given time to act.
  `SerialProxy.StopMachineAndClosePort` calls it too. `Machine.NeedsStopBeforeDisconnect` decides whether to send it, exempting a
  port that never answered as GRBL. See rule `closing-the-port-does-not-stop-grbl`.
- **v3:** GRBL reports the door as `Door:<n>`, and the number is what separates an open door
  from a closed one. `Machine` splits the two and `GrblProtocol` names both halves: `DoorSubStateClosed`,
  `Ajar`, `Retracting` and `Resuming`, plus `StatusJog`, `StatusHome`, `StatusCheck` and
  `StatusSleep` for the states coppercli does not drive. Nothing outside `GrblProtocol`
  contains a status word or a substate number.
- **v4:** GRBL answers the lines it is sent in the order it received them, so `Machine`
  matches each `ok` or `error:N` to the oldest line still unanswered. `BufferState` is
  computed from those unanswered lines rather than tracked beside them. GRBL's banner means
  it has restarted, whoever reset it: the lines it held are abandoned and `IsHomed` is
  cleared. After coppercli sends a soft reset, lines are held until that banner arrives or
  `Constants.ResetAnnounceTimeoutMs` passes, because GRBL drops a line it receives while
  restarting without answering it. An alarm abandons the lines GRBL held, and also the lines
  not yet sent unless they are held for the restart.

### proxy → TCP clients · v2 · kind: tcp · port 34000
`SerialProxy` re-exports the raw serial stream to one TCP client at a time, so a remote TUI
can drive the mill. Deliberately **unauthenticated** — it forwards raw bytes, and anything
that reaches the port can send arbitrary G-code. Documented as such in the README.
- When a session ends the proxy sends feed hold then soft reset, so a dropped connection
  cannot leave the spindle running. The stop is sent in one place,
  `StopMachineAndClosePort`, after both pump threads have joined and before the port
  closes; it takes the port with `Interlocked.Exchange`, so `Stop` and the session cannot
  both send it.
- **v2:** `MachineHold` owns the serial port in server mode (rule
  `server-holds-the-machine`). A terminal first sends `POST /api/terminal-takeover`
  (`ConnectionMenu.TryTakeOverFromServer`); the server refuses with `409`
  (`ErrorTakeoverWhileBusy`) while a run is in progress, and otherwise disconnects. The
  proxy then asks `TryClaimSerialPort` before admitting the terminal, and calls
  `ReleaseSerialPort` after its stop and port close; the server reconnects after that. A
  claim is granted once, and only while the server has yielded and is disconnected. If no
  terminal claims the port within `TerminalTakeoverWindowMs`, the server connects again.
- **v2:** while a session runs, the proxy refuses a second client rather than leaving it
  unanswered. The slot
  stays taken until the session's `finally` (stop, close, release) has finished, so a new
  client is never admitted while the previous session is still stopping the machine.
- A browser takeover (`/api/browser-takeover`) disconnects a proxy terminal even during a
  job. That is intended: the browser's operator is taking the machine, and the proxy's
  stop runs as for any other session end.
  See `server-let-go-of-the-machine`, `takeover-and-proxy-races`.
- **GAP (undecided):** a terminal takeover that arrives between a run start's connected
  check and its controller starting is not refused, because no run-start gate exists.
  Options: a gate that the run start and `TryYieldToTerminal` take under one lock (cost:
  every start path must go through it), or accept the window (current behavior; the
  proxy's stop still runs when the terminal leaves).
- **GAP (undecided):** the terminal computes the server's web port as proxy port + 1, so the
  takeover fails when `--web-port` is set to anything else. Options: publish the web port
  from the proxy, or add a terminal setting for it (cost: one more setting to keep in step).

### web → browser · v9 · kind: http + websocket · contract: `coppercli/WebServer/WebConstants.cs` (law)
Port 34001. Every path (`Api*`), every WebSocket message type (`WsMessageType*`), and every
socket command (`WsCmd*`) is a named constant there; the client's mirror is
`wwwroot/js/constants.js`. Neither side may hardcode a wire value.
- Admission is **`RequestPolicy.IsAllowed`, applied once in `HandleRequest` before any
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
     `Origin` check cannot see rebinding: the browser by then treats the request as
     same-origin;
  3. **`Origin`**, when present, must match the `Host` in host, port, and scheme;
  4. **`Sec-Fetch-Site`** must not be `cross-site` or `same-site`. **This check is inert on
     the configuration that ships and is not a defense.** Per W3C Fetch Metadata a browser
     attaches no `Sec-Fetch-*` header to a URL that is not potentially trustworthy, and a
     plain-http LAN address is not. It is kept because it works over `localhost` and would
     over TLS. Never state — in code, README, or release notes — that it blocks cross-site
     GETs. A raw socket can set the header; that proves only that the server reads it.
- **What this deliberately leaves open:** a cross-site GET carries no `Origin` and no
  `Sec-Fetch-Site`, so it is indistinguishable from the operator's own navigation and is
  admitted. Safe only while rule `no-side-effect-on-get` holds.
- Every response carries `X-Frame-Options: DENY`, CSP `frame-ancestors 'none'`, `nosniff`,
  and `no-referrer` (`ApplySecurityHeaders`). The frame restrictions prevent another page
  from displaying the UI inside a frame, where its buttons would send same-origin requests.
- **There is no login, token, password, or PIN, and none may be added** — see rule
  `web-ui-needs-no-typed-credential`, `web-access-token`, and
  `sec-fetch-site-header-absent-over-plain-http`.
- The socket is the live channel; `/api/*` is request/response.
- **v5:** nothing enforces a single browser. A second browser is offered a take-over and
  still works if it declines, no `/api/*` endpoint checks which client is calling, and two
  tabs of one browser share a cookie so they are not distinguishable. `IsSupersededClient`
  replaces a stored connection only when it is from the same browser and no longer open - a
  reconnect after a reload - so a second tab cannot take the first off the status stream
  while leaving it able to send commands. See
  `one-browser-client-was-never-enforced`.
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
  a handler — it refreshes the client's activity timestamp, which is what stops the
  stale-client cleanup from dropping a quiet browser. Every `WsCmd*` is validated by
  `validateConstants`, and every broadcast names its type constant.
- **v5:** the status reports `probing` and `tracingOutline` separately, both derived from
  `IProbeController`. An outline trace is a run like any other - it holds the machine and
  locks the screen - but it measures nothing, so the progress window reads `IsMeasuringGrid`.
  `/api/probe/status` reports the same under `active`.
- **v5:** `/api/probe/stop` answers whether it confirmed the machine stopped, and `500`
  with `CliConstants.StopTimedOutWarning` when it could not. The browser leaves the progress
  view up when the stop is not confirmed, rather than a setup screen that implies the run is
  over.
- **v8:** the browser asks the session questions through `/api/session/restore`, the same
  list the terminal asks: `GET` lists the pending ones, `POST {topic, detail, yes}` answers
  the one shown. It asks one at a time on page open, reconnect and G-code load, re-reading
  after each answer, never while another question is open. A missing field or unknown topic
  gets `400`; a question no longer pending as shown gets `409` (rule
  `session-questions-cleared-by-their-answer`). `/api/trust-work-zero` and the
  `hasStoredWorkZero` and `isWorkZeroSet` status fields, which the browser used to decide
  that question itself, are gone.
- **v7:** `/api/door/release` releases a door hold the operator confirmed in the browser's
  door overlay, and refuses while any run is in progress: a run holds the machine until it
  ends, and answering its own prompt is then the only way the cycle start is sent.
  `/api/resume` refuses at a door: a bare cycle start there restarts the spindle, so it goes
  through this path instead, where the click is the confirmation. `machineUnavailable` gates
  the jog controls, which `needsAttention` left live for a machine that had dropped off the
  link. The status
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
  that reason and its feed hold is not. See rule `browser-uses-core-status-values`.
- **v4 (prompts):** `PendingPrompt` holds the one prompt a run is waiting on. An answer must
  name that prompt's id **and** be one of the `Options` it offered, because answering resumes
  the run on the answering thread and the run can publish its next prompt before the answer
  returns. The id has to reach four places or it protects nothing: the broadcast,
  `DetectToolChange`/`DetectPendingPrompt` in the status, the client's `lastPrompt`, and the
  answer body. The id alone does not stop a double-tap — see rule
  `delay-input-after-prompt-redraw`.
- **v9:** the server holds the machine for its whole life (rule `server-holds-the-machine`),
  so a browser neither connects nor disconnects it. `/api/connect`, `/api/disconnect` and
  `/api/ports` are gone. `/api/force-disconnect` is now `/api/browser-takeover`: it closes
  every other page and any proxy terminal, and never disconnects the server.
  `/api/terminal-takeover` is new; its caller is the terminal, not the browser (see
  `proxy → TCP clients`).
- **v9:** `/api/probe/save` refuses to overwrite an existing file with `409`
  and `fileExists`, and the browser sends it again with `overwrite` set once the operator
  confirms. A finished browser probe opens the save screen with
  `Persistence.SuggestedProbeFileName`, the name the terminal suggests.
- **GAP (undecided):** machine errors are not shown in the browser. In server mode they go
  to the console's message list only. Options: a WebSocket message type for them (four-place
  update, rule `ws-message-types-updated-in-four-places`), or a field in `/api/status`.
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
  The one exception is a path on another host. `IsLocalPath` refuses any path that starts
  with two separators, in any mix, or with a separator and `?`, because on Windows opening
  a network share sends the login to that host. See `prompt-id-did-not-stop-a-double-tap`,
  `network-paths-missed-by-the-local-path-check`.

### shared constants → client · v2 · kind: http · `GET /api/constants`
Any value both C# and JavaScript need crosses here. `GetSharedConstants()` in
`CncWebServer.cs` serves it, `validateConstants()` in `helpers.js` compares it against the
client's own copy at startup and reports mismatches. A value duplicated between the two
languages without passing through `/api/constants` is a violation, not a shortcut.
- **v2:** a value published here that neither side reads and no check compares is deleted,
  not wired up. The `api` path group was two dozen paths stored twice with nothing comparing
  them, and a wrong path answers 404 anyway. Publish a value only when something consumes or
  verifies it.
- **GAP:** `constants.js` hardcodes every value, and **nothing in the UI reads a value *from*
  `/api/constants`**; a mismatch only produces a `console.warn`. Three groups are published
  and never compared: `probe`
  limits, `millGrid`, and `depthAdjustment`, and `index.html` hardcodes the very limits being
  published. `mill.js` reimplements `CncWebServer.MapToGrid` line for line, so the client both
  fetches grid cells and recomputes them. Target: the client *consumes* these values rather
  than restating and comparing them. `/api/config` already supplies values to the client.

### app → disk · v1 · kind: file · contract: `coppercli/Persistence.cs` (sole writer)
Settings, session state, and the probe autosave live under the OS app-data directory.
`Persistence` is the only code that reads or writes them; writes go through
`AtomicFile` so a power cut mid-write cannot leave a half-file. An unreadable file is
renamed with an `.unreadable` suffix and replaced with defaults rather than crashing the app.
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
- **v2:** a reading is checked before it is recorded. `ProbeGrid.GetNeighborDeviation`
  compares it against the mean of its measured orthogonal neighbors; past
  `ControllerConstants.ProbeHeightDeviationToleranceMm` the run retracts to the safe height,
  pauses for the operator, and re-probes the point on resume. Nothing suspect reaches the map
  or the autosave, and the point stays queued — see rule `remeasure-probe-point-after-operator-resume`.
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
- **GAP:** only `WebServer/wwwroot/**` is embedded. `machine-profiles.yaml`
  and `Resources/*.csv` (the GRBL error, alarm, and setting tables) are
  `CopyToOutputDirectory`, and the Unix tarball step archives only the single executable —
  so the macOS and Linux downloads ship without them, and **both loaders fail with no error
  reported** (`GrblCodeTranslator` returns null, `MachineProfiles` returns empty). The README
  advertises built-in machine profiles on all three platforms. The Windows installer copies
  `publish\*` recursively and is unaffected. Target: embed them, or archive the publish
  directory, and report a missing data file before use, as `error-before-use` requires.
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
  see `phase-enums-restated-the-run-lifecycle`.)
- **GAP:** the macro engine is a third front end and the least governed one — it drives
  `Machine` directly rather than through the controllers, and re-implements
  `MachineWait.WaitForIdleAsync`. `MacroMenu.RunMacroFromPath` discards the runner's
  success flag, so a failed macro still exits 0.

## Rules

- **web-ui-needs-no-typed-credential** *(error)* — the web UI must remain reachable by typing
  a bare LAN address (`http://192.168.1.5:34001`) into a phone browser, with nothing to
  enter and no secret in the URL. No token, password, PIN, or key the operator must carry
  or type. LAN peers are deliberately trusted; the owner made that call explicitly, and
  re-declined a PIN when the token came out. Protection is limited to checks that cost the
  operator zero keystrokes: the peer's source address, plus the `Host` and `Origin` headers
  a browser sends unasked. (`Sec-Fetch-Site` is checked too but arrives only over
  `localhost` or TLS — see the `web → browser (HTTP/WS)` entry.)
  _Check: `.architecture/rules/check-layering.sh`; `coppercli.Tests/RequestPolicyTests.cs`._
  _History: web-access-token,
  sec-fetch-site-header-absent-over-plain-http._

- **releaseasync-returns-controller-to-idle** *(error)* — the controller records whether a run is
  active. A task handle is used to await cleanup. `StartAsync` always ends in a terminal
  state, and only `ReleaseAsync` returns the controller to Idle. Callers must not use their
  own `Reset` checks. A stop path awaits the run task before calling `ReleaseAsync`: a second
  `ReleaseAsync` into a teardown that has overrun sends another feed hold and soft reset,
  which cancels the retract the first one queued (rule
  `closing-the-port-does-not-stop-grbl`).
  _Check: `.architecture/rules/check-layering.sh`; `coppercli.Tests/ControllerBaseTests.cs`
  (mutation: dropping the terminal-state guarantee fails two tests)._
  _History: two-records-of-whether-a-run-is-over,
  usable-probe-data-computed-in-three-places._

- **one-handler-per-control** *(error)* — register one handler per control.
  `onclick` and `addEventListener` are separate slots and both fire, so a control bound
  through each runs its handler twice on one tap. Handle state changes inside that handler.
  _Check: `.architecture/rules/check-layering.sh` (names the doubled element)._
  _History: two-records-of-whether-a-run-is-over._

- **ui-text-is-a-constant** *(error)* — text the operator reads is named once in `constants.js`
  and referenced, never written at the point of use. A second copy of a label drifts from the
  first as soon as either is edited.
  _Check: `.architecture/rules/check-layering.sh`._

- **no-exception-text-on-screen** *(error)* — exception messages can name files, offsets and
  types the operator cannot act on. It goes to the log, and the screen gets a sentence about
  what failed and what to do. Two paths answer a caught exception: `WriteFailure` for the
  browser and `MenuHelpers.ShowFailure` for the terminal, and both log it. `ControllerError`
  is text the run produced for the operator and is shown unchanged.
  _Check: `.architecture/rules/check-layering.sh`, both languages; mutation-verified._

- **closing-the-port-does-not-stop-grbl** *(error)* — any path that drops the connection
  stops the machine first, writing the bytes straight to the stream, because the worker that
  drains the send queue is gone by then. Any path that ends a run converges on one teardown
  that stops the machine and then retracts: `ProbeController.CleanupAsync`. A retract queued
  before the soft reset is discarded by it, so the test asserts the order.
  _Check: `coppercli.Tests/SafetyCheckTests.cs`; `coppercli.Tests/ProbeControllerTests.cs`
  (mutation: reordering or removing either half fails)._
  _History: closing-the-port-does-not-stop-the-machine._

- **request-policy-checks-all-routes** *(error)* — a check that admits or refuses a request runs
  once, before any routing branch, and covers static files as well as `/api/*` and `/ws`.
  Checking only the API produces a page that loads and then does nothing, with no error
  reported.
  _Check: reader judgment of `HandleRequest`._  _History: web-access-token._

- **no-side-effect-on-get** *(error)* — a GET changes nothing: no machine motion, no file
  written, no state loaded, no client slot reserved. A cross-site GET has no `Origin`
  and, on plain HTTP, no `Sec-Fetch-Site`, so
  `RequestPolicy` cannot tell an `<img>` on someone else's page from the operator's own
  navigation and admits it; the only thing making that safe is that GETs do nothing.
  Everything that changes state is POST and stays POST. Reserving the single client slot
  from `ServeStaticFile` once let a cross-site `<img>` create phantom pending clients, so the
  operator's own socket skipped `Connect()` and offered a force-disconnect that can drop the
  serial port mid-cut. `GET /api/probe/status` reports the autosave through
  `AppState.ReadUsableAutosave`, which reads it without adopting it. Removing a load from a
  GET also removes the checks that load carried, so reading and adopting are separate calls
  (`ReadUsableAutosave` and `EnsureProbeDataLoaded`) and the checks belong to the read. Never
  fall back to the raw file state.
  _Check: reader judgment of `HandleApi`; `coppercli.Tests/RequestPolicyTests.cs`;
  `coppercli.Tests/WebServerSequenceTests.cs`._
  _History: sec-fetch-site-header-absent-over-plain-http,
  usable-probe-data-computed-in-three-places._

- **machine-state-single-writer** *(error)* — a fact about the machine is defined by
  `Machine` (or derived from the controller that defines it) and assigned in exactly one
  place; no UI keeps its own mirror. `IsHomed` is set true only in `MachineWait.HomeAsync`
  and false only inside `Machine`; `AppState.IsProbing` is derived, not stored; work-zero
  invalidation happens on the connection-state event, not a menu action. **Scope:** this
  governs machine state. Operator assertions about the workpiece, such as `IsWorkZeroSet`
  and whether a height map still applies, are session state and stay in `AppState`.
  _Check: reader judgment; grep for assignments._
  _History: stale-work-zero-and-height-map, work-zero-deliberately-stays-in-appstate._

- **server-holds-the-machine** *(error)* — in server mode only `MachineHold` connects or
  disconnects the machine. It connects at startup and stays connected for the server's
  life; a browser opening, closing or going idle never changes the connection. It disconnects
  only for a terminal takeover, which is refused while a run is in progress, and connects
  again once the proxy returns the port. Each hand-off step (check-and-set of the yield,
  claim, release) is atomic. Homing and work zero are still cleared on every disconnect
  (rule `machine-state-single-writer`); this rule exists so that disconnects happen only
  when a terminal asks for the machine or the server stops.
  _Check: `coppercli.Tests/MachineHoldTests.cs`, `coppercli.Tests/WebServerMachineHoldTests.cs`;
  reader judgment (grep `CncWebServer.cs` for `Connect(`/`Disconnect(` outside
  `ConnectMachine` and `MachineHold`)._
  _History: server-let-go-of-the-machine, takeover-and-proxy-races._

- **per-run-state-cleared-at-run-start** *(error)* — controllers are session-lifetime
  singletons, so every field describing the current run is declared in
  `ControllerBase.ResetRunState()` and cleared when a run *starts*, not only on `Reset()`:
  abort paths do not all reach a reset. The method is `abstract` so a new controller must
  answer the question. Anything derivable from `State` is not stored — `IsPaused` is
  `State == Paused`, as `IsActive` already was. **Scope:** state the machine or the operator
  holds outlives the run and must survive the reset — the shift still sitting in GRBL's G54
  (`_outstandingDepthAdjustment`), the probe grid and its progress index, which let an
  interrupted board resume. Ask which of the two a new field is before adding it.
  _Check: `coppercli.Tests/ControllerBaseTests.cs`._
  _History: pause-flag-duplicated-controller-state._

- **one-field-per-fact** *(error)* — a boolean saying "X is outstanding" and a separate field
  saying "how much X" are one fact and must be one field, 0 meaning none. Held apart, they
  drift: a later run with adjustment 0 passed the restore's tolerance check and cleared
  `_depthAdjustmentApplied` while the earlier shift remained in G54. A duplicate need
  not be a field: an enum member that answers a question another type defines is one
  (`MillingPhase.Paused` beside `ControllerState.Paused`), and so is a running aggregate
  kept beside the collection it summarizes. `ProbeGrid.MinHeight`/`MaxHeight` were widened
  by each measurement and so could never narrow, leaving a re-probed node's stale extreme
  for `InterpolateZ` to clamp a cut depth against; both are now derived from the points.
  For any summary, check whether the data can change in a direction it cannot follow. The
  opposite also applies: two similar-sounding questions are not always one fact.
  `IsRunInProgress`
  ("is a run holding the machine") and `MachineIsBeingDriven` ("is a workflow moving the
  tool now") differ exactly at the tool-change pause, where the operator is meant to jog.
  Before merging
  two predicates, name the case where their answers must differ; if there is one, both stay,
  each derived from the same state. A question is defined in one place the way a field is:
  "does the operator have usable probe data" was answered by whether the autosave parses, by
  `AppState.ProbePoints`, and by a third computation in the web status, so the mill start
  check read the answer that said none while the probe screen read the one that said
  complete. `AppState.CurrentProbeGrid` is the single answer now, and every check and every
  display derives from it. A derived answer needs one definition as much as a field does. A
  question with more cases than the branches reading it fails the same way: GRBL's `Door` has
  four substates and about ten places branched two ways, so `Door:3` — restoring from the
  park, with the machine moving — read as "holding" on every screen.
  `MachineWait.GetDoorState` names the cases and every screen reads it. The remainder case
  is defined as the remainder (`IsDoorOpen`), so the three predicates always cover `Door`,
  and a substate GRBL adds falls into the case that prompts the operator.
  This also applies to constants and operations that implement a rule,
  or the duplicates move down a level where nothing reports them. One clearance height existed
  as three constants of the same value, and the move to it was written five times: the rule
  that a machine holding at the door must not be sent one was in two of them and missing from
  the third. `SafeClearanceZ` is the height and `ControllerBase.RetractToSafeZAsync` is the
  move. For each literal and each move, check whether it could legitimately differ from the
  others, and name it where it could not.
  _Check: `coppercli.Tests/DepthAdjustmentTests.cs`; `coppercli.Tests/ProbeControllerTests.cs`
  (`PhaseEnums_ExcludeControllerStates`); `coppercli.Tests/WebServerSequenceTests.cs`;
  `coppercli.Tests/MillingControllerTests.cs` (`StopAtDoor_QueuesNoRetract`)._
  _History: pause-flag-duplicated-controller-state,
  phase-enums-restated-the-run-lifecycle,
  usable-probe-data-computed-in-three-places,
  door-state-checked-in-ten-places,
  one-clearance-height-under-three-names._

- **new-state-cases-update-callers** *(error)* — update every caller that branches on a
  state when that state gains a case.
  `MachineWait.IsDoorResuming` was added in Core without a caller outside it while three
  UIs still branched the door two ways: it compiled, the suite stayed green, and the defect
  it was written to fix was still on every screen. A predicate, enum member or field with no
  reader outside the file that defines it is either unadopted or dead, and both are findings.
  Where the new cases belong to one state, the reader is one function returning that state,
  not a branch per screen.
  _Check: `.architecture/rules/check-layering.sh` (a `MachineWait` predicate with no caller
  outside `MachineWait.cs`); reader judgment of any case added to an existing question._
  _History: door-state-checked-in-ten-places,
  two-resume-windows-for-one-door._

- **door-policy-defined-in-machinewait** *(error)* — `MachineWait.ClearDoorHoldAsync` defines
  which door states the operator can answer, how many refused releases stop the attempt, and
  which states require waiting. Callers supply prompts and progress messages; a terminal
  screen also draws an overlay. The method defines the keyboard flush, Escape poll, and
  removal of the door message alongside its calls to `GetDoorState`,
  `CanReleaseDoorHold`, `GetDoorMessage`, and `ReleaseDoorHoldAsync`. The
  browser's endpoint validates and releases once, because the page redraws every broadcast
  interval and the operator clicks again.
  _Check: `coppercli.Tests/DoorClearTests.cs` and `coppercli.Tests/ControllerBaseTests.cs`;
  both are mutation-verified._
  Every caller supplies a way out: a run its token, a screen an `onPoll`. The waits return at
  once on a canceled token, so a loop with no way out spins instead of blocking.
  _History: one-door-policy-two-implementations,
  door-retry-loop-spun-on-a-canceled-token._

- **session-questions-cleared-by-their-answer** *(error)* — each answer, yes or no, makes
  its question's condition in `SessionRestore.GetPendingSteps` false. That condition is the
  only record of what was asked, so no front end re-reading the list asks again a question
  answered successfully. `SessionRestore.Answer` acts only on a pending question with the
  topic and detail shown, under one lock, so a second screen cannot undo the first and a
  stale "no" cannot delete a different map. It does not reuse `PendingPrompt`'s stored id,
  because a session question is rebuilt from state on every read, so its topic and detail
  are its identity. A failed answer stays pending for the next pass; the current pass skips
  it so it does not hold back the rest. Never keep a list of asked topics beyond one pass:
  the browser cannot see it, and it drifts from the conditions. The browser's `showConfirm`
  resolves `null` for a question another dialog replaced before it was answered, and the
  browser sends nothing. A front end's own question about the same map comes after the pass
  and asks only about what the pass leaves (the File menu's apply question,
  `HasCompleteMapNotApplied`).
  _Check: `coppercli.Tests/SessionRestoreTests.cs` (`EveryAnswer_ClearsItsOwnQuestion`,
  `DecliningTheSavedMap_ClearsItsQuestion`, `AnAnswerToAQuestionAlreadySettled_IsRefused`,
  `AnAnswerToAQuestionThatChanged_IsRefused`,
  `AFailedAnswer_IsSkippedForThePass_AndTheOthersAreAsked`,
  `RestoreQuestions_EndAfterEachTopicForEitherAnswer`);
  `coppercli.Tests/browser/session-restore.test.mjs`._
  _History: startup-prompt-loop-never-terminated, session-questions-asked-twice-by-the-browser,
  terminal-apply-question-repeated-the-session-pass._

- **loaded-gcode-unchanged-during-run** *(error)* — do not replace the loaded G-code while a
  run is in progress. A run streams from `Machine.File` and tracks where it is by
  line number, so a new file resets that to the start and the job continues from the top of
  the program. `Machine.SetFile` refuses only while `Mode` is `SendFile`, which a job paused
  at a tool change is not: the guard is `AppState.LoadGCodeIntoMachine`, the one way G-code
  reaches the machine, because that layer can see the controllers. Setting Z0 at a tool
  change reached it through re-applying the height map, so `HandleWorkZeroChange` leaves the
  map and the file alone during a run, and zeroing X or Y is refused outright.
  _Check: `coppercli.Tests/WebServerSequenceTests.cs`
  (`MillRun_RejectsLoadedFileReplacement`, `ZeroDuringRun_PreservesAppliedMapAndLoadedFile`);
  both are mutation-verified._
  _History: two-resume-windows-for-one-door._

- **reload-original-gcode-before-discarding-map** *(error)* — reload the original G-code
  before `AppState` discards a height map. If the reload fails, retain the map and report the
  error. Applying a map rewrites `Machine.File`; clearing the map first and then failing to
  reload leaves the corrections in the G-code without a record of them, so the next apply
  doubles them.
  `AppState.DiscardProbeData` does the reload through `RemoveMapFromLoadedGCode` and returns
  `ErrorMapStuckInGCode` when the source file is gone or will not load.
  _Check: `coppercli.Tests/WebServerSequenceTests.cs`
  (`ZeroingXYWhenTheMapCannotComeOut_SaysSo`), mutation-verified._
  _History: two-resume-windows-for-one-door._

- **manual-door-release-and-required-homing** *(error)* — only the operator releases an
  enclosure door hold. Require homing before a job because `G53` retracts need a known
  machine origin. _Check: reader judgment; `coppercli.Tests/SafetyCheckTests.cs`._
  _History: automatic-door-release-and-unverified-homing,
  wait-helper-aborted-on-door-state,
  two-resume-windows-for-one-door._

- **no-cached-physical-measurement** *(error)* — never cache a measurement of a physical
  thing past any event where a human can change it without the software seeing. The
  tool-setter reference length is measured every time, never persisted. The event is not
  only the end of a session: the
  setter's trigger height was cached for a rapid move, but it is measured once with the old
  tool and once with the new. Before reusing a measurement, check whether the tool or setter
  could have changed since it was taken. _Check: reader judgment._
  _History: cached-reference-tool-length,
  cached-tool-setter-height-from-previous-tool._

- **derived-artifact-records-its-context** *(error)* — an artifact computed from a setup
  stores that setup and is checked against it before use. A height map stores its
  `ProbeContext` (source file, work origin); a map with no recorded context is `Unknown`,
  which `IsUsable` accepts because older maps did not record their setup, and the saved map
  file is offered only with a known context (`ReadSavedProbeGridForLoadedFile`). The check
  runs in one place, `AppState.UsableForThisJob`, and both the autosave and the saved map
  file are read through it. A map that names a source file must store a finite origin: a
  non-finite origin compares false against every tolerance and would leave the map
  `Unknown`, which nothing refuses. The current origin with no machine is
  `Vector3.MinValue`, which is finite, so `GetApplicability` reads such a map as
  `OriginMoved` or `DifferentFile`, not `Unknown`.
  _Check: `coppercli.Tests/ProbeContextTests.cs`; `coppercli.Tests/ProbeGridLoadTests.cs`._
  _History: probe-data-inferred-from-a-file-on-disk,
  usable-probe-data-computed-in-three-places, unknown-origin-is-a-finite-sentinel._

- **validate-loaded-grid** *(error)* — validate a loaded grid before it controls machine
  motion, using the same checks as its constructor.
  `ProbeGrid.Load` calls `ValidateLoadedGridGeometry` to reject non-finite or unordered
  extents and fewer than two nodes per axis. It also rejects point indices outside the
  grid and heights that are not numbers; loaded heights set the Z of cutting moves.
  Fields used by the applicability check are required: see
  `derived-artifact-records-its-context` for the origin case.
  _Check: `coppercli.Tests/ProbeGridLoadTests.cs`._
  _History: usable-probe-data-computed-in-three-places._

- **read-g54-explicitly** *(error)* — before any `G10 L2 P1`, query G54 itself
  (`RefreshWorkOffsetsAsync`); never use the combined `WorkOffset`, and never derive it from
  `MachinePosition − WorkPosition`. Undo an offset relatively, never by restoring an
  absolute snapshot; a tool change legitimately writes the same register.

  The depth adjustment is always measured from the zero the operator set: the baseline is
  `G54.Z` minus whatever an earlier run left outstanding, so asking for 0.05 gives 0.05
  however the run before it ended. A restore the machine refuses is reported by amount.
  _Check: `coppercli.Tests/DepthAdjustmentTests.cs`._  _History: workoffset-is-not-g54._

  **Decided, not open** (Thomer, 2026-09), so later passes do not re-raise them:
  - Minus is down and cuts deeper, matching the Z sign. The buttons stay `-` and `+`.
  - The outstanding amount lives on the milling controller and is deliberately temporary.
    It survives between runs in a session, not a restart or a disconnect; the operator is
    told to set Z zero again, which is the remedy.
  - The adjustment shifts the work origin rather than changing each streamed Z the
    way the height map is. The origin shift is the approach; do not replace it.

- **monotonic-time-and-event-counts** *(error)* — timeouts are measured on a clock that only
  moves forward: `Stopwatch` for an interval inside one method, `Environment.TickCount64` for
  a timestamp a second thread reads. Never use `DateTime.Now` for a timeout. Count
  `StatusReportCount` events to detect whether GRBL is reporting. Compare a probe height
  with measured neighbors through `ProbeGrid.GetNeighborDeviation` to decide whether to
  accept it; elapsed probe time does not answer that question.
  Before timing a code path, check what else is inside the interval: `RetractZAsync` and
  `MoveToPointAsync` deliberately return without awaiting so GRBL can buffer them, so a
  stopwatch around `G38.2` spanned the previous retract, the traverse, and the descent as
  well. `coppercli.Core` shows nothing, so it reads no wall clock at all.
  _Check: `.architecture/rules/check-layering.sh`; reader judgment of any `Stopwatch`
  that gates a decision._
  _History: synchronized-queues-and-wall-clock-deadlines,
  stopwatch-timed-more-than-the-probe._

- **grbl-answers-its-own-commands** *(error)* — whether a command ran is read from GRBL's
  answer to it, never from `Status`. GRBL answers no status query while it is busy —
  the whole homing cycle, because GRBL drives it from a loop that never services one, and
  the reboot after a soft reset — so a `Status` read after a command can be older than the
  command, and a machine part-way through a cycle, one that never started and one that
  finished all read the same. `IMachine.SendAsync` carries that answer, matched to its line
  by queue order; matching by text cannot tell two identical lines apart, and a line GRBL
  abandons would hand its answer to the next one. Where no answer is needed, send a command
  that is harmless when it is not: a stop sends `$X` unconditionally, because GRBL ignores
  it on a machine that is not alarmed and nothing then has to decide whether it is.
  `Status` is for saying what the machine is doing. Waiting for the status to catch up
  is allowed only for the door switch, which is not a command
  (`MachineWait`'s use of `DoorReadingCatchUpReports`).
  _Check: `coppercli.Tests/FakeGrblTests.cs`
  (`AHomingCycle_IsNotReadAsARefusal_WhenOneReportWasStillInFlight`,
  `AStopDuringHoming_ClearsTheAlarmItRaised_SoTheRetractIsAccepted`); reader judgment of any
  `Status` read that follows a command on the same path._
  _History: automatic-door-release-and-unverified-homing, status-read-as-a-command-answer._

- **no-live-collections-across-threads** *(error)* — never expose a live mutable collection
  to another thread; own it and hand out snapshots. `Queue.Synchronized` does not
  make a check-then-take safe. Cleanup in a `finally` must never throw over the error that
  caused it. _Check: reader judgment; `coppercli.Tests/ProbeGridTests.cs`._
  _History: live-queue-shared-between-ui-and-worker,
  synchronized-queues-and-wall-clock-deadlines._

- **error-before-use** *(error)* — before the app relies on a file, resource table, or
  profile it ships with, it asserts that file, table, or profile is present and fails with one
  clean line naming what is missing. A missing `machine-profiles.yaml` or GRBL code table
  currently returns empty/null and the feature disappears with no error reported. _Check: reader judgment of the load paths._

- **workflows-live-in-controllers** *(error)* — every multi-step machine operation is an
  FSM in `coppercli.Core/Controllers/`. `CncWebServer.cs` and the TUI menus may configure,
  subscribe, start, and render — never decide the sequence of machine moves. Single-shot
  commands from a UI go through `coppercli/Helpers/MachineCommands.cs`;
  no menu and no HTTP handler calls `SendLine` directly. Honored today: zero `SendLine` calls
  outside Core and `MachineCommands`.
  _Check: `.architecture/rules/check-layering.sh`._

- **controllers-check-machine-readiness** *(error)* — controllers check whether the machine's
  state permits a job to start. `MillingController` asks the operator about
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
  `behavior-rules-require-behavior-tests` says why that alone is not enough._
  _History: readiness-check-refused-closed-door-hold,
  door-state-checked-in-ten-places._

- **controllers-never-render** *(error)* — controllers emit events and return values; they
  never touch the console, `AnsiConsole`, `HttpListener`, or a socket.
  _Check: dependency direction; grep Core for UI types._

- **core-is-platform-independent** *(error)* — `coppercli.Core` never references the
  `coppercli` project and never imports a UI or web-host type. Dependencies flow one way.
  Shared constants used by Core live in `coppercli.Core/Util/Constants.cs`.
  _Check: `.architecture/rules/check-layering.sh`._

- **publish-shared-constants-through-api** *(error)* — a value both the server and the browser
  need is served by `GetSharedConstants()` and verified by `validateConstants()`. Never
  write the same literal into both `CliConstants.cs`/`Constants.cs`/`GrblProtocol.cs` and
  `constants.js`. A value only one side needs is not published here: publishing it creates a
  second copy that nothing compares.
  _Check: reader judgment; `validateConstants()` reports at runtime._
  _History: prompt-id-did-not-stop-a-double-tap._

- **ws-message-types-updated-in-four-places** *(error)* — a new WebSocket message type is
  added to `WebConstants.cs`, `constants.js`, the `wsMessageTypes` object in
  `GetSharedConstants()`, and the validation list in `helpers.js`. Three out of four is a
  mismatch that only shows up at runtime. The same four points apply to the `WsCmd*`
  client→server commands under `commands`, and to the `WorkZeroOutcome` names under
  `heightMapOutcomes`. An unvalidated command breaks jogging on a rename, with no error
  reported; an unvalidated outcome leaves the operator with no word on what became of the
  height map.
  _Check: `.architecture/rules/check-layering.sh`, which compares all four: the names
  `constants.js` declares against the ones `helpers.js` validates, and the count declared in
  `WebConstants.cs` against the count declared in `constants.js`._

- **api-paths-are-constants** *(error)* — no `fetch()` hardcodes an `/api` path and no
  handler hardcodes one; both sides use their named constant.
  _Check: `.architecture/rules/check-layering.sh`._

- **culture-invariant-gcode** *(error)* — every number sent to the machine is formatted
  through `GCodeFormat.Inv`. An interpolated string on a comma-decimal locale emits
  `Z-1,000`, which GRBL rejects.
  _Check: `coppercli.Tests/CultureInvariantGCodeTests.cs` pins the behavior;
  `.architecture/rules/check-layering.sh` catches a coordinate formatted without the wrapper
  where G-code is built._

- **settings-rename-needs-migration** *(error)* — renaming a `MachineSettings` property
  adds a `SettingsMigrations` entry in `Persistence.cs`, with a version comment. Do not
  rely on backwards compatibility; migrate and use the new name everywhere.
  _Check: reader judgment of the diff._

- **remeasure-probe-point-after-operator-resume** *(error)* — distinguish an accepted probe
  reading from a point the operator must measure again. "Keep
  going" does not distinguish "the reading was within tolerance" from "the operator
  intervened and resumed". `RetractAndCheckHeightAsync` returned
  `!ct.IsCancellationRequested`, which the caller read as "record it", so a height taken
  before the operator cleared the debris went into the map and the autosave, and the grid
  reported itself complete. It is now the three-valued `HeightOutcome`
  (`Accepted` / `Remeasure` / `Cancelled`), and a resume re-probes the point. In general, an
  operator's resume returns the run to the step that raised the prompt, not past it. _Check: `coppercli.Tests/ProbeControllerTests.cs` — the locking test is
  mutation-verified; returning `Accepted` in place of `Remeasure` reproduces the defect._
  _History: stopwatch-timed-more-than-the-probe,
  two-resume-windows-for-one-door._

- **waits-do-not-abort-on-awaited-state** *(error)* — a wait helper must not treat the state the
  caller is waiting to leave as a bail-out condition. Such a wait can never succeed, and it
  returns a plain `false` that the caller's retry turns into an apparent hang.
  `EnsureDoorClosedAsync` called `WaitForIdleAsync`, which counts Door as `IsUnavailable` and
  gave up on its first poll, so the door prompt reappeared milliseconds after being answered.
  Door is in `IsUnavailable` because most callers are not waiting it out; a caller that is
  gets its own wait — `WaitForDoorReleasedAsync`, and the catch-up inside
  `ReleaseDoorHoldAsync`. Read the set from `MachineWait.IsUnavailable`, and check it against
  the state you are starting from before reusing a wait.
  _Check: `coppercli.Tests/MachineWaitTests.cs`._
  _History: wait-helper-aborted-on-door-state._

- **tests-detect-plausible-defects** *(error)* — a test is only worth keeping if some plausible
  defect makes it fail. Subscribing to an event without raising it, or passing an enum
  literal to `Enum.IsDefined`, asserts nothing; seventeen such tests were removed or replaced.
  Where a test checks a named rule, verify it by mutation. The test checks the *behavior*
  the rule is about, not the spelling it was written against —
  `PhaseEnums_ExcludeControllerStates` compared names by equality and let
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
  _History: seventeen-tests-that-could-not-fail,
  test-project-stopped-compiling-and-no-ci-ran-it,
  door-state-checked-in-ten-places._

- **browser-target-ids-exist** *(error)* — every id the browser looks up is on
  `index.html`, because `getElementById` returns null otherwise. A write guarded with
  `if (el)` then does nothing and reports nothing; an unguarded one throws and stops every
  later line in the same handler. Six such writes existed at once. `check-layering.sh` reads
  `getElementById` and its `$` shorthand; `page.test.mjs` also covers the helpers that take
  an id (`setText`, `addClass`, `updateButtonState`) and the arrays of ids `jog.js` iterates.
  _Check: `.architecture/rules/check-layering.sh`, and
  `coppercli.Tests/browser/page.test.mjs`, whose stub page takes its ids from
  `index.html` and throws on one the page does not have._
  _History: browser-status-handler-threw-on-every-message._

- **browser-uses-core-status-values** *(error)* — Core computes machine status values and
  sends them to the browser. The `status` field contains GRBL's raw word for display only.
  A UI that derives an answer from it defines the same answer again, and the two can
  disagree: the door had three states in Core and two in the browser.
  _Check: `.architecture/rules/check-layering.sh` greps for the comparisons a browser would
  write. `WebServerSequenceTests.TheStatus_ReportsWhichControlsApply` and
  `MachineWaitTests.EveryActivity_HasControlAnswers` check the payload's values.
  `WebServerSequenceTests.EveryBrowserActivityName_MatchesTheEnum`
  checks the browser's copy of the activity names against the enum.
  `coppercli.Tests/browser/status.test.mjs` runs the real browser modules against a stub
  page and checks what they write to it._
  _History: browser-status-handler-threw-on-every-message,
  two-resume-windows-for-one-door._

- **behavior-rules-require-behavior-tests** *(error)* — test a behavior requirement by
  exercising the behavior; a grep in `check-layering.sh` catches only specified syntax.
  Verify a new check by bypassing it: the readiness check
  came back past both of its greps as `if (MachineWait.IsUnavailable(machine)) return ...`,
  and the test that catches it is
  `WebServerSequenceTests.ADoorHoldDoesNotBlockTheMill_TheControllerPromptsInstead`,
  which drives the real API against a real `Machine` over a loopback `FakeGrbl`. A grep over a
  path that does not exist also finds nothing and reports success, so the script asserts
  every path it checks is present before it checks anything
  (`check-runs-at-the-repository-root`).
  _Check: bypass each new grep by hand; `.architecture/rules/check-layering.sh` exits
  non-zero outside the repository root._
  _History: door-state-checked-in-ten-places._

- **fail-safe-on-uncertainty** *(error)* — when a safety-relevant step cannot be confirmed
  (a retract that GRBL rejected, a probe that did not report contact, a status that never
  arrived), the job stops rather than continuing. A rejected safety retract must never be
  swallowed, and a run whose final lift was not confirmed must never report itself finished.
  All three controllers use `ControllerBase.LiftAfterStopAsync` for that decision.
  A stop at the door never counts as confirmed: the soft reset
  clears the hold, so the tool's position is unknown.
  _Check: `coppercli.Tests/SafetyCheckTests.cs`
  (`AMillWhoseFinalRetractIsNotConfirmed_NeverReportsItFinished`) and
  `coppercli.Tests/ProbeControllerTests.cs`
  (`ATraceWhoseSafetyRetractIsNotConfirmed_NeverReportsItFinished`)._

- **record-unresolved-design-decisions** *(error)* — record an unresolved choice as a
  **GAP (undecided)** on the relevant interface. State each option and its cost, and keep
  current behavior until the owner decides. GRBL has one resume, so releasing a door hold
  also releases a feed hold: a run paused at the door continues cutting while the screen
  reads Paused. Reasserting the hold after release and ending the pause were both tried
  without a decision. The `controllers → machine` interface records both options.
  _Check: reader judgment; a GAP (undecided) names both answers and their cost._
  _History: door-state-checked-in-ten-places._

- **test-doubles-reproduce-grbl-responses** *(error)* — a test double reproduces the machine's
  observable answer to each command under test. `FakeMachine` reporting `Idle` after every
  pause line hid that GRBL answers M0/M1 with `Hold:0` while M6 never reaches it, so a gate
  that could not fire on hardware passed 338 tests. One status
  hardcoded across a family of commands erases the distinction the code is deciding on, and
  the suite then agrees with the code because both use the same incorrect response. A double
  more permissive than the machine hides commands the machine would refuse.
  `FakeMachine.SimulateMoveAsync` reported `Run` for every move
  and so drove the tool through an open enclosure, and both doubles resumed `Door:0` straight
  to `Idle` where GRBL returns to the state the door interrupted — `Run` for a streaming job
  — which made a feed hold reasserted after the release appear effective. Check both the
  machine's response and the commands it refuses.
  `coppercli.Tests/Fakes/DoorModel.cs` is GRBL's door rules for all three doubles, so a
  substate they must answer differently is written once.
  A double must also answer the *status poll*, not only report when its own state changes:
  counting those answers is how coppercli tells a machine that is working from one that has
  gone quiet, and a double that never produces them makes every such wait run to its timeout
  and no test able to tell the two apart. `coppercli.Tests/Fakes/StatusPoll.cs` is that
  model for all three, including going quiet where GRBL does.
  _Check: reader judgment of `coppercli.Tests/Fakes/`;
  `coppercli.Tests/FakeMachineDoorTests.cs`._
  _History: fake-machine-reported-idle-for-every-pause,
  test-project-stopped-compiling-and-no-ci-ran-it,
  door-state-checked-in-ten-places,
  two-resume-windows-for-one-door._

- **delay-input-after-prompt-redraw** *(error)* — a control redrawn with a new question in
  the same place refuses input for `PROMPT_SETTLE_MS`. An id alone does not prevent a second
  tap from answering the new question. The answer resumes
  the run on the answering thread, and the run publishes its next question before the answer
  returns, so the second tap includes the **new** question's valid id and the server has no
  grounds to refuse it. The two guards cover different cases: the id (`PendingPrompt`, which
  also requires the answer to be one of the question's `Options`) refuses a stale answer or
  one from a second device; the settle refuses the second tap of a double-tap, and so is set
  longer than `DOUBLE_TAP_DELAY_MS`. Check which prompt the control displays when a second
  input event arrives.
  _Check: `coppercli.Tests/PendingPromptTests.cs`;
  `coppercli.Tests/ControllerBaseTests.cs` (`AnsweringAPrompt_PublishesTheNextBeforeItReturns`);
  reader judgment of any handler that both answers and redraws._
  _History: prompt-id-did-not-stop-a-double-tap,
  two-resume-windows-for-one-door._

- **no-magic-values** *(error)* — every literal with semantic meaning is a named constant
  in the file that owns it: `coppercli.Core/Util/Constants.cs` (Core-wide),
  `coppercli/CliConstants.cs` (CLI/UI), `coppercli.Core/Util/GrblProtocol.cs` (GRBL wire),
  `coppercli/WebServer/WebConstants.cs` (HTTP/WS wire),
  `coppercli.Core/Controllers/ControllerConstants.cs` (controller prompts, errors, timeouts),
  `coppercli.Core/Settings/SettingRange.cs` (what a refused setting is told, and the name and
  unit of each one), `wwwroot/js/constants.js` (client).
  Logging strings are exempt. _Check: reader judgment; see `CLAUDE.md` for the grep recipes._

## Intent

Planned changes. This section records intended work and does not set current contracts.

- **shared workflow orchestration** *(part, planned)* — one entry point per workflow that
  both the TUI and the web server call, so the mill/probe/tool-change start sequences leave
  `CncWebServer` entirely. The owner prioritized this change and deferred it for a separate
  pass. **Current state:** both UIs repeat the setup sequence described under `ui → controllers`.
- **`CncWebServer` split** *(part, planned)* — separate the ~3.7k-line static class into
  request routing, workflow setup, and client updates. It is currently one file and one
  static class.

_No other planned changes are recorded here. The tree has no `TODO` or `FIXME` markers,
design document, diagram, or reachable issue backlog. The owner records further decisions
in the prompt log._

## Owner's standing positions

Stated repeatedly by the owner; apply them without asking.

- Every fact is defined in one place, and every other value is derived from it. No second
  copy, however convenient.
- Plain, brief English in code, comments, names and documents. No mannered prose.
- Runs, tests and subagents are bounded in time.

## Known gaps in the record

Each is something a human must supply.

- **The prompt log is not committed.** `prompts/` is gitignored, so the record of
  *why* — the reversals, the rejected designs, the owner's steering — does not survive a
  fresh clone. `.architecture/history/` now carries what could be recovered from it.
- **`CLAUDE.md` still lags the tree, though less than it did.** Fixed 2026-09: the
  `MachineWait.HomeAsync` example points at the file rather than repeating a body that had
  drifted, and the reference to a `StatusHelpers.cs` that does not exist is gone. Still
  outstanding: `coppercli.Tests/` is absent from the project structure, and the document
  names none of `AtomicFile`, `ProbeContext`, `HomingOutcome`, `EtaEstimator`, `GCodeFormat`,
  `GrblRejection`, `SessionRestore`, `PassThrough` or `RequestPolicy`.
- **The OpenCNCPilot reference implementation is unavailable here.** `CLAUDE.md` instructs
  agents to consult `~/src/OpenCNCPilot/` for GRBL and probing questions; that tree is not
  present, so some upstream semantics (e.g. `ProbeOptions.MaxDepth`) cannot be settled.
- **The only end-to-end user guide is off-repo** — <https://thomer.com/pcb-nomad3>,
  unversioned against the app. Nothing in the tree will catch it if it drifts.

---

**History:** `.architecture/history/` — one lesson per file, append-only. Read it before
changing an interface; the entries record approaches already tried and abandoned. Grep the `Touches:`
line at the foot of each entry for the interface, rule, or path you are about to touch.
