# 2026-09 — A prompt id that could not stop the second tap

**Who:** Thomer, with Claude Opus 5, in a defect sweep of the web layer after `d53653c`.
The sweep worked from the gaps `ARCHITECTURE.md` already named on `ui → controllers`,
`web → browser`, and `shared constants → client`.

**Tried:** the M0/M1 operator pause reaches the browser as an overlay with a Continue
button, and a double-tap on that button answered twice. The fix was to give each question
an id. `PendingPrompt` holds the one question a run is waiting on; an answer must name
that question and must be one of the options it offered.

**Believed:** an answer that names its question cannot answer a different one, so the id
closes the double-tap.

**Realized:** it does not. The answer resumes the run on the answering thread, and the run
publishes its next question before the answer returns.
`ControllerBaseTests.AnsweringOnePromptPublishesTheNextBeforeItReturns` holds that
ordering. By the time a second tap lands, the browser has redrawn the button for the new
question. The tap carries that question's own valid id, so the server has no grounds to
refuse it. The operator answers a question they never read.

A settle delay closes it. A freshly drawn prompt refuses to be answered for
`PROMPT_SETTLE_MS` (600 ms, longer than `DOUBLE_TAP_DELAY_MS`) — the same guard `probe.js`
already used for its STOP button. Both guards are kept because they cover different cases:
the id refuses a stale answer or one from a second device; the settle refuses the second
tap of a double-tap. The id still has to reach four places to work: the broadcast,
`DetectToolChange`/`DetectOperatorPause` in the status, the client's `lastPrompt`, and the
answer body. A new prompt path must carry it through all four.

**Two predicates that sound alike and are not one fact.** "A run owns the machine" is
`ControllerBase.IsRunInProgress`. It blocks a second start and keeps the serial port open.
It counts `WaitingForUserInput` and `Completing`, because a job parked at a prompt has the
tool in the work. "A workflow is moving the tool now" is
`CncWebServer.MachineIsBeingDriven`. It blocks a jog or a goto. It excludes the pause a
milling run holds in during a tool change, because that is when the operator is asked to
jog to the surface and set Z0. Both derive from controller state; neither is a copy of the
other. Collapsing them would either lock the operator out of the tool change or let a jog
land in the middle of a pass.

**Also settled in the sweep:**
- `ControllerBase.EmitError(Exception)` now decides once, for all three UIs, whether an
  exception's text reaches the operator. A workflow's own refusal — an
  `InvalidOperationException` or a `TimeoutException` — goes through. The new
  `InvalidControllerStateException` and `ObjectDisposedException` do not, because their
  text names states and objects. No other exception's text reaches the operator either.
- `ProbeController.TraceOutlineAsync` transitions the FSM like any other run. It used to
  set only `Phase`, leaving the controller `Idle`, so every "is the machine busy"
  predicate returned false while a trace was running.
- The `DirectCommand` table in `CncWebServer` is the one place an HTTP endpoint and a
  WebSocket command meet, so the two entry points cannot drift apart. Each entry carries a
  `DuringRun` flag saying whether that command may be sent while a job drives the machine.
- `ping` is now `WsCmdPing`, handled in the server switch, and every `WsCmd*` is validated
  by `validateConstants`. `BroadcastStatusLoop` sends `WsMessageTypeStatus` instead of the
  literal `"status"`.

**Decision: a published constant nothing reads is deleted, not fixed.** The `api` path
group was removed from `GetSharedConstants()` rather than wired up. Nothing read it and
nothing checked it, so it was two dozen paths stored twice with no way to notice a
disagreement. A wrong path answers 404 and someone notices; two copies of a path that
differ report nothing.

**Decision: the web file browsers can reach the whole filesystem, and that is accepted.**
`/api/probe/save` writes to a path the client chooses, and `/api/files` / `/api/file/load`
enumerate and read anywhere the process can reach. The operator picks a board file from
wherever it sits, the same as in the terminal UI. The web API is deliberately
unauthenticated on a trusted LAN (rule `web-ui-needs-no-typed-credential`). The save path
is forced to the `.pgrid` extension. This is a standing decision; it is not work waiting to
be done.

**Lesson → new rule `a-redrawn-control-settles-before-it-answers`, and
`one-field-per-fact` extended.** An id does not make a control safe to tap twice when the
answer redraws the control. Ask what the control will be showing when the second event
arrives, not whether the first event was addressed correctly. Two predicates that sound
alike are not one fact. Before merging two predicates, name the case where the answers
must differ. For these two it is the tool change, where the operator is meant to jog.

**Touches:** `ui → controllers` (v3 → v4), `web → browser` (v3 → v4),
`shared constants → client` (v1 → v2), rules
`a-redrawn-control-settles-before-it-answers`, `one-field-per-fact`,
`shared-constants-flow-through-api`, `ws-message-types-updated-in-four-places`,
`coppercli/WebServer/PendingPrompt.cs`, `coppercli/WebServer/CncWebServer.cs`,
`coppercli/WebServer/WebConstants.cs`,
`coppercli.Core/Controllers/InvalidControllerStateException.cs`,
`coppercli.Core/Controllers/ControllerBase.cs`,
`coppercli.Core/Controllers/ProbeController.cs`,
`coppercli.Core/Controllers/MillingController.cs`,
`coppercli/WebServer/wwwroot/js/constants.js`,
`coppercli/WebServer/wwwroot/js/helpers.js`,
`coppercli/WebServer/wwwroot/js/mill.js`,
`coppercli.Tests/PendingPromptTests.cs`, `coppercli.Tests/DirectCommandTableTests.cs`,
`coppercli.Tests/ControllerBaseTests.cs`.
