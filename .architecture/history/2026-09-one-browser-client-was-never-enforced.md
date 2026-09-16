# 2026-09 — "One browser client at a time" was never enforced

**Who:** Thomer, with Claude Opus 5, answering a question about the README: "You make it
sound like this is multi-user? Only one user is ever using this, ideally on a single page.
Isn't there some error mode that prevents multiple pages to operate it?"

**Tried:** the README said "Only one client can connect at a time", and the `web → browser`
entry in `ARCHITECTURE.md` said "One browser client at a time".

**Believed:** the server admitted one browser and refused the rest.

**Realized:** nothing enforced it. `HandleWebSocket` sends a `connection:error` to a second
browser and then adds the client and enters the receive loop anyway, so declining the
take-over prompt leaves both pages working. No `/api/*` endpoint checks which client is
calling. Two tabs of one browser share a cookie, so the server cannot tell them apart.

A second tab did have one effect: opening one evicted the first tab's socket while leaving
it able to send commands, so the operator was driving the machine from a page that no
longer received status. `IsSupersededClient` now replaces a stored connection only when it
is from the same browser **and** no longer open - a reconnect after a reload.

The README now says what is true: drive the machine from one page, and nothing enforces it.

**Lesson.** Refusing a second operator is a product decision, and it was never taken. A
phone that sleeps and wakes, a laptop that reconnects, and an operator who reloads
mid-probe all look like a second client, so refusing one would refuse those too. Do not
write a limit into the README or into `ARCHITECTURE.md` that no code applies; a reader who
trusts it stops looking for the races that limit would have prevented.

**Touches:** interface `web → browser` v5, `coppercli/WebServer/CncWebServer.cs`,
`coppercli.Tests/WebClientConnectionTests.cs`, `README.md`.
