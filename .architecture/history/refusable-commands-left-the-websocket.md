# A command the server can refuse is sent over HTTP only

**Date:** 2026-09-25

**Problem:** Home, Unlock and Resume were sent over the WebSocket. When GRBL refused one,
for example with `error:13` at an open door, the operator was not told why.

**Cause:** A command run from the socket has no caller to return a refusal to, so
`RunDirectCommand` only logs it.

**Fix:** Home, Unlock and Resume are HTTP-only (`WsCommand` is null in `DirectCommands`).
`WsCmdHome`, `WsCmdUnlock` and `WsCmdResume` were removed, along with `commands.home`,
`commands.unlock` and `commands.resume` in `GetSharedConstants` and their `CMD_*` in
`constants.js` and `helpers.js`. `CncWebServer.FindHttpCommand` looks a path up in the
table. `helpers.js` `postOrShowError` is the one browser helper that POSTs and shows the
server's reason. `HandleApi` runs an HTTP direct command with `Task.Run`, because the accept
loop calls `HandleRequest` without awaiting it, and a synchronous homing request stalled
every other request until the cycle ended.
`WebServerSequenceTests.OtherRequests_AreAnswered_WhileHomeRuns` fails without the
`Task.Run`.

**Lesson:** A refusal can reach the operator only on the channel the request came in on. If
the server can refuse a command, send it over HTTP. A long command run inside the request
handler blocks the whole server.

**Interface:** `web → browser` v10.

Touches: `DirectCommands`, `RunDirectCommand`, `FindHttpCommand`, `HandleApi`,
`GetSharedConstants`, `constants.js`, `helpers.js`, `web → browser`,
`ws-message-types-updated-in-four-places`.
