# A prompt id did not stop the second tap

**Problem:** A double-tap on Continue in the M0/M1 pause dialog answered two successive
questions. The operator had not read the second question.

**Cause:** `PendingPrompt` accepts an answer only when its id and option match the current
question. Answering resumes the run on the same thread, which can publish the next question
before the call returns (`ControllerBaseTests.AnsweringAPrompt_PublishesTheNextBeforeItReturns`).
The browser redraws Continue for that question before the second tap, so the second request
has a valid id and option.

**Fix:** A freshly drawn prompt refuses to be answered for `PROMPT_SETTLE_MS` (600 ms, longer
than `DOUBLE_TAP_DELAY_MS`), as `probe.js` already did for Stop. The id rejects stale
answers and answers from another device; the delay rejects a second tap. The id must appear
in four places: the broadcast,
`DetectToolChange`/`DetectOperatorPause` in the status, the client's `lastPrompt` and the
answer body. New prompt paths must update all four.
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
- HTTP endpoints and WebSocket commands use the same `DirectCommand` table in
  `CncWebServer`. Each entry has a `DuringRun` flag that says whether it may be sent
  during a job.
- `ping` is `WsCmdPing`, handled in the server switch; every `WsCmd*` is validated by
  `validateConstants`; `BroadcastStatusLoop` sends `WsMessageTypeStatus` instead of the
  literal `"status"`.
- Two predicates answer different questions. `ControllerBase.IsRunInProgress` ("a run
  owns the machine") blocks a second start and keeps the serial port open; it counts
  `WaitingForUserInput` and `Completing`, because a job parked at a prompt has the tool in
  the work. `CncWebServer.MachineIsBeingDriven` ("a workflow is moving the tool now") blocks
  a jog or a goto; it excludes the pause a milling run holds during a tool change, because
  that is when the operator is asked to jog to the surface and set Z0. Both derive from
  controller state and neither is a copy of the other. Combining them would either prevent
  jogging during a tool change or allow jogging during a cut.
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
rules `delay-input-after-prompt-redraw`, `one-field-per-fact`,
`publish-shared-constants-through-api`, `ws-message-types-updated-in-four-places`; interfaces
`ui → controllers` v3 → v4, `web → browser` v3 → v4, `shared constants → client` v1 → v2.

**Rejected:** Wiring up the `api` path group in `GetSharedConstants()`. It was deleted
instead: nothing read it and nothing checked it, so it was two dozen paths stored twice with
no way to notice a disagreement. A wrong path answers 404 and someone notices; two copies of
a path that differ report nothing.

**Rule:** Delay input after redrawing a prompt so a second tap cannot answer the next question. Keep separate predicates when a known state requires different answers.
