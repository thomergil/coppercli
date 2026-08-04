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
  app-layer state via `InternalsVisibleTo`.

## Seams

### controllers → machine · v1 · kind: function · contract: `coppercli.Core/Communication/IMachine.cs` (law)
Every controller reaches the machine only through `IMachine`. That interface file is the
contract; do not restate it here. It exists so controllers are testable without hardware,
`coppercli.Tests/Fakes/` supplies the doubles.
- Status predicates (`IsIdle`, `IsAlarm`, `IsHold`, `IsDoor`, `IsProblematic`) and every
  wait/poll loop live in `MachineWait`. A controller that spells out `machine.Status == "Idle"`
  or writes its own polling loop is a violation.
- `MachineWait.HomeAsync` is the only place `IsHomed` is set **true**. It is set false only
  inside `Machine` itself, on connect, disconnect, and soft reset — the three events after
  which the machine has forgotten its reference frame. No UI assigns it.
- **GAP (sharp target):** the seam stops at Core. `AppState.Machine`, `CncWebServer._machine`,
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

### ui → controllers · v2 · kind: function + event · contract: `coppercli.Core/Controllers/IController.cs` (law)
Both UIs start a workflow by configuring a controller, subscribing to its four events
(`StateChanged`, `ProgressChanged`, `UserInputRequired`, `ErrorOccurred`), and awaiting
`StartAsync`. Events are **synchronous** — the handler runs inline and the controller waits,
so a handler must not block on the UI thread's own input loop.
- The FSM's legal transitions are the `ValidTransitions` table at the top of
  `ControllerBase.cs`; that table is law. An illegal transition throws.
  Per-workflow phases are documented on each `*Phase` enum.
- **v2:** a controller instance serves the whole session, so `ControllerBase` declares
  `protected abstract void ResetRunState()` and calls it at the start of every run as well
  as from `Reset()`. Implementing it is how a new controller states what belongs to a run —
  see rule `per-run-state-cleared-at-run-start`.
- **GAP:** the M0/M1 operator pause landed incomplete. The client never names
  `WaitingForUserInput`, so a job parked mid-cut reports "Milling complete!"; the prompt
  overlay is erased by the next status broadcast; and the pause neither retracts nor stops
  the spindle. See `2026-08-a-fake-that-answered-idle-to-every-pause`.
- **GAP (sharp target):** the "configure options from settings, load the grid/file,
  subscribe, run, unsubscribe" ritual is written out separately in
  `CncWebServer.cs` and in `Menus/ProbeMenu.cs` / `Menus/MillMenu.cs`
  (`ProbeOptions.FromSettings` and `ToolChangeOptions.FromSettings` appear on both sides).
  Target: one orchestration entry point per workflow that both UIs call, leaving each UI
  with presentation only. Honored-with-debt until then — the *workflows* are correctly in
  Core; the *wiring* is duplicated, and duplicated wiring is how the two UIs
  drifted apart before (see `2026-08-stale-work-zero-and-height-map`).

### machine → GRBL · v1 · kind: serial wire protocol · contract: `coppercli.Core/Util/GrblProtocol.cs` (law)
Status strings, real-time bytes, and command words are named there and nowhere else.
Targets GRBL 1.1f; 0.8/0.9/1.0 are known-incompatible.
- Every number sent to the machine is formatted through `GCodeFormat.Inv`. A comma decimal
  separator is a GRBL rejection, and on a comma-locale machine an interpolated string
  produces one silently.

### proxy → TCP clients · v1 · kind: tcp · port 34000
`SerialProxy` re-exports the raw serial stream to one TCP client at a time, so a remote TUI
can drive the mill. Deliberately **unauthenticated** — it is a byte bridge, and anything
that reaches the port can send arbitrary G-code. Documented as such in the README.
- On client disconnect the proxy sends feed-hold then soft reset, so a dropped connection
  cannot leave the spindle running.
- Only one owner of the serial port may exist: `IsSerialPortInUse` lets the proxy refuse a
  TUI client while the web server holds the `Machine` connection.

### web → browser · v3 · kind: http + websocket · contract: `coppercli/WebServer/WebConstants.cs` (law)
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
- One browser client at a time. The socket is the live channel; `/api/*` is request/response.
- Server→client frames are `{type, data:{...}}`; client→server are `{type, ...fields}`.
- **GAP:** about 28 POST-only cases in `HandleApi` are `if (method == MethodPost) { ... }`
  with no `else`, so a wrong-method request writes nothing and the guard's
  `finally { response.Close(); }` turns that into an **empty 200** — a success the UI cannot
  distinguish from a real one. `ApiProbeApply` is the sole case that answers `405`, leaving
  `ErrorMethodNotAllowed` a near-dead constant. Target: one rejection path for every
  unmatched method.
- **GAP:** failure signalling is inconsistent. `/api/mill/start` always returns
  `{success:true}` and reports real failures later over the socket; `/api/mill/pause`
  returns 400 on illegal state while `/api/probe/pause` returns 200 with `{success:false}`.
- **GAP (security-relevant, given the seam is deliberately unauthenticated):**
  `/api/probe/save` writes to a client-chosen path with no containment check, and
  `/api/files` / `/api/file/load` enumerate and read anywhere on disk. `/api/file/upload`
  in the same file does this correctly (`Path.GetFileName` + `IsContainedIn`). Same class of
  operation, opposite rigor. Target: every path from a request goes through the containment
  helper.
- **GAP:** the `ping` client→server message is a bare string literal in `websocket.js`,
  absent from all four points of `ws-message-types-updated-in-four-places`, and unhandled by
  the server `switch` — yet it is the **sole defence against the 30 s stale-client reaper**,
  working only as a side effect of touching `_clientLastActivity`. `BroadcastStatusLoop`
  likewise hardcodes `"status"` rather than `WsMessageTypeStatus`, and the `WsCmd*`
  client→server commands are published but never validated, so a rename silently breaks
  jogging. The 12 server→client types *do* honor the rule in full.

### shared constants → client · v1 · kind: http · `GET /api/constants`
Any value both C# and JavaScript need crosses here. `GetSharedConstants()` in
`CncWebServer.cs` serves it, `validateConstants()` in `helpers.js` compares it against the
client's own copy at startup and reports mismatches. A value duplicated between the two
languages without passing through this seam is a violation, not a shortcut.
- **GAP (sharp target):** today this is a lint pass, not a data channel. `constants.js`
  hardcodes every value and **nothing in the UI ever reads a value *from* the endpoint** —
  the mismatch is only `console.warn`ed. Half the payload is unread (`api`, `commands`,
  `probe` limits, `millGrid`, `depthAdjustment`; `index.html` hardcodes the very limits
  being published). Worse, `mill.js` reimplements `CncWebServer.MapToGrid` line for line, so
  the client both fetches grid cells and recomputes them. Target: the client *consumes*
  these values rather than restating and comparing them. The genuine data channel is
  `/api/config`.

### app → disk · v1 · kind: file · contract: `coppercli/Persistence.cs` (sole writer)
Settings, session state, and the probe autosave live under the OS app-data directory.
`Persistence` is the only code that reads or writes them; writes go through
`AtomicFile` so a power cut mid-write cannot leave a half-file. An unreadable file is
quarantined and replaced with defaults rather than crashing the app.
- Renaming a `MachineSettings` property requires an entry in the `SettingsMigrations` array
  in the same file. Migrations are idempotent and rewrite the file once.

### probe data lifecycle · v1 · kind: state model · contract: the `<remarks>` block atop `coppercli.Core/Controllers/ProbeController.cs` (law)
The four states (`none` / `ready` / `partial` / `complete`), what each offers the operator,
and the autosave rules are documented there. Do not restate or re-derive them; UI state is
computed from grid progress via `ComputeProbeState`, and Save-vs-Clear from
`Persistence.GetProbeState()`.
- A height map is only trusted against the `ProbeContext` saved with it: the source file
  and work origin it was measured against. Existence of a file is never the answer to
  "do I have probe data?".
- **GAP (sharp target):** the same four-state table is written out a second time as a
  comment block above `updateProbeButtonsFromState` in `wwwroot/js/probe.js`, which calls
  *itself* "the single source of truth for button states". Two documents claim to be the
  single source of truth for one fact; they agree today and nothing keeps them agreeing.
  Target: the C# `<remarks>` block is the only copy, and the JS comment points at it.
  The same pattern afflicts the tool-change FSM, documented four times
  (`ToolChangeController.cs`, `ToolChangePhase.cs`, `CncWebServer.cs`, `mill.js`).

### build → release artifact · v1 · kind: ci/shell · contract: `.github/workflows/release.yml`
A tag `v*` builds self-contained single-file executables for `win-x64`, `osx-arm64`,
`osx-x64`, `linux-x64`. Every file the app loads at runtime must be either an
`EmbeddedResource` or present in the shipped artifact.
- **GAP (live defect):** only `WebServer/wwwroot/**` is embedded. `machine-profiles.yaml`
  and `Resources/*.csv` (the GRBL/uCNC error, alarm, and setting tables) are
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
- **GAP (the contract is law and the code does not honor it):** two documented workflows
  cannot work. `load [name:file]` roots the placeholder *before* substitution, producing
  `<macroDir>//home/you/back.ngc` and failing with "File not found". `probe z` waits on
  `AppState.SingleProbeCallback`, which **nothing in the tree ever invokes**, so it always
  spins to timeout — and it appears both in the guide's flagship example and in the shipped
  `macros/etch_and_drill.cmacro`. Also undocumented: `wait <anything>` ignores its argument,
  and `jog` / `probe grid` / `mill` open the **interactive** TUI menus rather than running
  headless, which matters to anyone writing an unattended macro.
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
  `localhost` or TLS — see the seam.)
  _Check: `.architecture/rules/check-layering.sh`; `coppercli.Tests/RequestGuardTests.cs`._
  _History: 2026-08-web-access-token,
  2026-08-a-guard-built-on-a-header-browsers-never-send._

- **guard-covers-whole-surface** *(error)* — a check that admits or refuses a request runs
  once, before any routing branch, and covers static files as well as `/api/*` and `/ws`.
  A guard on the API alone produces a page that loads and then silently does nothing.
  _Check: reader judgment of `HandleRequest`._  _History: 2026-08-web-access-token._

- **no-side-effect-on-get** *(error)* — a GET changes nothing: no machine motion, no file
  written, no state loaded, no client slot reserved. This is load-bearing, not hygiene. A
  cross-site GET carries no `Origin` and, on plain http, no `Sec-Fetch-Site`, so
  `RequestGuard` cannot tell an `<img>` on someone else's page from the operator's own
  navigation and admits it; the only thing making that safe is that GETs do nothing.
  Everything that changes state is POST and stays POST. Reserving the single client slot
  from `ServeStaticFile` once let a cross-site `<img>` mint phantom pending clients, so the
  operator's own socket skipped `Connect()` and offered a force-disconnect that can drop the
  serial port mid-cut. Known fray: `GET /api/probe/status` calls `EnsureProbeDataLoaded()`.
  _Check: reader judgment of `HandleApi`; `coppercli.Tests/RequestGuardTests.cs`._
  _History: 2026-08-a-guard-built-on-a-header-browsers-never-send._

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
  `_depthAdjustmentApplied` while the earlier shift stayed baked into G54.
  _Check: `coppercli.Tests/DepthAdjustmentTests.cs`._
  _History: 2026-08-a-pause-flag-that-outlived-its-job._

- **never-auto-clear-a-safety-gate** *(error)* — software never clears a state that exists
  to require human confirmation. The enclosure door blocks a job and only the operator
  resumes it. Homing is deliberately impossible to skip: without it, `G53` retracts have
  no reference to retract to. _Check: reader judgment; `coppercli.Tests/SafetyGuardTests.cs`._
  _History: 2026-08-software-clearing-safety-gates._

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
  and is questioned, never assumed usable.
  _Check: `coppercli.Tests/ProbeContextTests.cs`._
  _History: 2026-08-probe-data-inferred-from-a-file-on-disk._

- **read-g54-explicitly** *(error)* — before any `G10 L2 P1`, query G54 itself
  (`RefreshWorkOffsetsAsync`); never use the combined `WorkOffset`, and never derive it from
  `MachinePosition − WorkPosition`. Undo an offset relatively, never by restoring an
  absolute snapshot; a tool change legitimately owns the same register.
  _Check: `coppercli.Tests/DepthAdjustmentTests.cs`._  _History: 2026-08-workoffset-is-not-g54._

- **monotonic-time-and-event-counts** *(error)* — timeouts use `Stopwatch`, never
  `DateTime.Now`. "Is the peer still talking?" is answered by counting events
  (`StatusReportCount`), never by timing them. A clock step must not be able to answer a
  safety question. _Check: grep for `DateTime.Now` in wait paths._
  _History: 2026-08-synchronized-queues-and-wall-clock-deadlines._

- **no-live-collections-across-seams** *(error)* — never expose a live mutable collection
  across a UI/worker boundary; own it and hand out snapshots. `Queue.Synchronized` does not
  make a check-then-take safe. Cleanup in a `finally` must never throw over the error that
  caused it. _Check: reader judgment; `coppercli.Tests/ProbeGridTests.cs`._
  _History: 2026-08-live-queue-across-the-ui-worker-seam,
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
  `constants.js`. _Check: reader judgment; `validateConstants()` reports at runtime._

- **ws-message-types-updated-in-four-places** *(error)* — a new WebSocket message type is
  added to `WebConstants.cs`, `constants.js`, the `wsMessageTypes` object in
  `GetSharedConstants()`, and the validation list in `helpers.js`. Three out of four is a
  mismatch that only shows up at runtime.
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
  written out on both sides. This is the GAP on the `ui → controllers` seam.
- **`CncWebServer` split** *(part, planned)* — the ~3.7k-line static class separated into
  request routing, workflow orchestration, and broadcast/lifecycle. **Delta:** one file,
  one static class.
- **uCNC firmware support** *(seam, latent)* — `MachineSettings.FirmwareType` defaults to
  `"Grbl"`, `GrblCodeTranslator` reads it, and uCNC error/alarm/setting tables ship in
  `coppercli.Core/Resources/`. **Delta:** no UI or CLI path ever sets it. Inherited
  scaffolding. Decide it or delete it; do not leave a third piece of dead fork
  (see `2026-08-dead-subsystems-carried-from-the-fork`).

_Beyond these there is no roadmap: zero `TODO`/`FIXME` markers in ~14k lines, no design
doc, no diagrams, no reachable issue backlog. Direction is set per-session by the owner and
lives only in the prompt log, which is why this memory exists._

## Known gaps in the record

Each is something a human must supply.

- **The prompt log is not committed.** `prompts/` is gitignored, so the blunt record of
  *why* — the reversals, the rejected designs, the owner's steering — does not survive a
  fresh clone. `.architecture/history/` now carries what could be recovered from it.
- **`CLAUDE.md` is the one document that has gone stale.** Its flagship example,
  `MachineWait.HomeAsync`, is shown returning `Task<bool>` with a three-line body; it
  actually returns `Task<HomingOutcome>` with rejection-listening and
  `StatusReportCount`-based liveness. It also points at `StatusHelpers.cs`, which does not
  exist, omits `coppercli.Tests/` from the project structure, and predates `AtomicFile`,
  `ProbeContext`, `HomingOutcome`, `EtaEstimator`, `GCodeFormat`, `GrblRejection`,
  `SessionRestore`, `PassThrough`, and `RequestGuard`.
- **The OpenCNCPilot reference implementation is unavailable here.** `CLAUDE.md` instructs
  agents to consult `~/src/OpenCNCPilot/` for GRBL and probing questions; that tree is not
  present, so some upstream semantics (e.g. `ProbeOptions.MaxDepth`) cannot be settled.
- **The only end-to-end user guide is off-repo** — <https://thomer.com/pcb-nomad3>,
  unversioned against the app. Nothing in the tree will catch it if it drifts.
- **Eight tests document coverage that does not exist** — `SetupGrid_WhenNotIdle_Throws`
  asserts nothing, `M6InFile_EmitsToolChangeEvent` asserts the opposite of its name, and six
  `*_EventCanBeSubscribed` tests assert a just-initialized null is still null. Names in a
  test suite are a contract too.

---

**History:** `.architecture/history/` — one lesson per file, append-only. Read it before
changing a seam; the entries record approaches already tried and abandoned. Grep the `Touches:`
line at the foot of each entry for the seam, rule, or path you are about to touch.
