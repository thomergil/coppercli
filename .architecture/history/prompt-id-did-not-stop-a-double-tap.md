# A prompt id did not stop the second tap

**Problem:** The M0/M1 operator pause reaches the browser as an overlay with a Continue
button, and a double-tap on that button answered two questions. The operator answers a
question they never read.

**Cause:** Each question was given an id: `PendingPrompt` holds the one question a run is
waiting on, and an answer must name that question and be one of the options it offered. The
answer resumes the run on the answering thread, and the run publishes its next question
before the answer returns —
`ControllerBaseTests.AnsweringAPrompt_PublishesTheNextBeforeItReturns` holds that ordering.
By the time a second tap lands, the browser has redrawn the button for the new question, and
the tap carries that question's own valid id, so the server has no grounds to refuse it.

**Fix:** A freshly drawn prompt refuses to be answered for `PROMPT_SETTLE_MS` (600 ms, longer
than `DOUBLE_TAP_DELAY_MS`), the guard `probe.js` already used for its STOP button. Both
guards are kept: the id refuses a stale answer or one from a second device, the settle
refuses the second tap of a double-tap. The id has to reach four places — the broadcast,
`DetectToolChange`/`DetectOperatorPause` in the status, the client's `lastPrompt` and the
answer body — and a new prompt path must carry it through all four.
Settled in the same sweep (after `d53653c`, worked from the gaps `ARCHITECTURE.md` named on
`ui → controllers`, `web → browser` and `shared constants → client`):

- `ControllerBase.EmitError(Exception)` decides once, for all three UIs, whether an
  exception's text reaches the operator. A workflow's own refusal, an
  `InvalidOperationException` or a `TimeoutException`, goes through; the new
  `InvalidControllerStateException` and `ObjectDisposedException` do not, because their text
  names states and objects. No other exception's text reaches the operator either.
- `ProbeController.TraceOutlineAsync` transitions the FSM like any other run. It used to set
  only `Phase`, leaving the controller `Idle`, so every "is the machine busy" predicate
  returned false while a trace was running.
- The `DirectCommand` table in `CncWebServer` is the one place an HTTP endpoint and a
  WebSocket command meet, so the two entry points cannot drift apart. Each entry carries a
  `DuringRun` flag saying whether that command may be sent while a job drives the machine.
- `ping` is `WsCmdPing`, handled in the server switch; every `WsCmd*` is validated by
  `validateConstants`; `BroadcastStatusLoop` sends `WsMessageTypeStatus` instead of the
  literal `"status"`.
- Two predicates that sound alike are kept separate. `ControllerBase.IsRunInProgress` ("a run
  owns the machine") blocks a second start and keeps the serial port open; it counts
  `WaitingForUserInput` and `Completing`, because a job parked at a prompt has the tool in
  the work. `CncWebServer.MachineIsBeingDriven` ("a workflow is moving the tool now") blocks
  a jog or a goto; it excludes the pause a milling run holds during a tool change, because
  that is when the operator is asked to jog to the surface and set Z0. Both derive from
  controller state and neither is a copy of the other. Collapsing them would either lock the
  operator out of the tool change or let a jog land in the middle of a pass.
- The web file browsers can reach the whole filesystem, and that is accepted.
  `/api/probe/save` writes to a path the client chooses, and `/api/files` and
  `/api/file/load` enumerate and read anywhere the process can reach. The operator picks a
  board file from wherever it sits, the same as in the terminal UI, and the web API is
  deliberately unauthenticated on a trusted LAN (rule `web-ui-needs-no-typed-credential`).
  The save path is forced to the `.pgrid` extension. This is a standing decision, not work
  waiting to be done.

`coppercli/WebServer/PendingPrompt.cs`, `coppercli/WebServer/CncWebServer.cs`,
`coppercli/WebServer/WebConstants.cs`,
`coppercli.Core/Controllers/InvalidControllerStateException.cs`,
`coppercli.Core/Controllers/ControllerBase.cs`,
`coppercli.Core/Controllers/ProbeController.cs`,
`coppercli.Core/Controllers/MillingController.cs`,
`coppercli/WebServer/wwwroot/js/constants.js`, `coppercli/WebServer/wwwroot/js/helpers.js`,
`coppercli/WebServer/wwwroot/js/mill.js`, `coppercli.Tests/PendingPromptTests.cs`,
`coppercli.Tests/DirectCommandTableTests.cs`, `coppercli.Tests/ControllerBaseTests.cs`;
rules `a-redrawn-control-settles-before-it-answers`, `one-field-per-fact`,
`shared-constants-flow-through-api`, `ws-message-types-updated-in-four-places`; interfaces
`ui → controllers` v3 → v4, `web → browser` v3 → v4, `shared constants → client` v1 → v2.

**Rejected:** Wiring up the `api` path group in `GetSharedConstants()`. It was deleted
instead: nothing read it and nothing checked it, so it was two dozen paths stored twice with
no way to notice a disagreement. A wrong path answers 404 and someone notices; two copies of
a path that differ report nothing.

**Rule:** An id does not make a control safe to tap twice when the answer redraws the
control; ask what the control will be showing when the second event arrives. Before merging
two predicates that sound alike, name the case where the answers must differ.
