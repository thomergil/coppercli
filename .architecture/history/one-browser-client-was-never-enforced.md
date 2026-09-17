# "One browser client at a time" was never enforced

**Problem:** The README said "Only one client can connect at a time" and the `web → browser`
entry in `ARCHITECTURE.md` said "One browser client at a time". Nothing enforced it. Opening
a second tab evicted the first tab's socket while leaving it able to send commands, so the
operator was driving the machine from a page that no longer received status.

**Cause:** `HandleWebSocket` sends a `connection:error` to a second browser and then adds the
client and enters the receive loop anyway, so declining the take-over prompt leaves both pages
working. No `/api/*` endpoint checks which client is calling. Two tabs of one browser share a
cookie, so the server cannot tell them apart.

**Fix:** `IsSupersededClient` replaces a stored connection only when it is from the same
browser and no longer open — a reconnect after a reload. The README now says to drive the
machine from one page, and that nothing enforces it. `coppercli/WebServer/CncWebServer.cs`,
`coppercli.Tests/WebClientConnectionTests.cs`, `README.md`; interface `web → browser` v5.

**Rejected:** Refusing a second client. A phone that sleeps and wakes, a laptop that
reconnects and an operator who reloads mid-probe all look like a second client, so refusing
one would refuse those too. It is a product decision that was never taken.

**Rule:** Do not write a limit into the README or into `ARCHITECTURE.md` that no code
applies; a reader who trusts it stops looking for the races that limit would have prevented.
