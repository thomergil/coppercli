# 2026-08 — Per-run access token on the web UI

**Who:** Thomer, with Claude Opus 4.8, during the safety and correctness overhaul (`4698964`).

**Tried:** A per-run access token. `CncWebServer.Run` generated `Guid.NewGuid().ToString("N")`
(32 hex characters) at startup and appended it to every URL it printed:
`http://192.168.1.x:34001/?token=<32 hex>`. `IsAuthorised` required the token — as a
`Bearer` header or a `?token=` query parameter — on `/api/*` and on the `/ws` upgrade.
Static files were served without it. Browser-side, `auth.js` lifted the token out of the
query string into `sessionStorage`, stripped it from the address bar, and monkey-patched
`window.fetch` to attach the `Authorization` header to every `/api/` call.

**Believed:** The server can move a spinning cutter and binds `http://+:<port>/` on every
interface, so anything that changes machine state should present a credential. The token
was thought to cost the operator nothing, because the operator would *follow the printed
link*. The commit filed it under "Security" alongside the zeroing and command-injection
guards. The owner had already said "Web exposure is fine" — the token was added anyway.

**Realized:** Following the printed link is a desktop assumption. The primary use of the
web UI is walking up to the machine with a phone and **typing** a LAN address into the
browser, and nobody types 32 hex characters. Because static files were left unguarded, the
failure was silent and confusing: the page shell loaded fine on the phone, then
every `/api/*` call and the WebSocket upgrade returned 401, leaving a dead UI that showed
no error. The token did not make the product safer; it made it unreachable from
the one client it was built for. The README ships a phone jog screenshot; `WebConstants`
sizes the idle-disconnect timeout around "phone screen went dark". The evidence that this
was a phone-first surface was already in the tree.

**Realized (second order):** An `Origin` check alone is not a sufficient replacement.
`Origin` stops a cross-site page from driving the machine, but it does not stop DNS
rebinding, where an attacker-controlled name resolves to the LAN address and the browser
then treats the requests as same-origin. A `Host` check restricting the host to IP
literals, `localhost`, and `.local` names closes that, because a rebinding attack
needs a resolvable DNS name.

**Lesson → rule `web-ui-needs-no-typed-credential`.** Do not reintroduce a token, password,
PIN, or any other secret the operator must carry in the URL or type by hand. LAN-peer
access to this server is deliberately unauthenticated; the owner made that call explicitly
("Web exposure is fine"), *before* the token was added. Guard it only with checks the
browser supplies for free — `Origin` and `Host` — and if a guard ever rejects a request,
the UI must say so out loud rather than fail silently.

**Corollary → rule `guard-covers-whole-surface`.** Any guard added to `/api/*` or `/ws`
must also cover static files, or the failure mode is a page that loads and then does
nothing.

**Resolution:** the token was removed the same day. `RequestGuard.IsAllowed` now runs once
in `HandleRequest` before any routing branch, so it covers static files, the API, and the
socket alike, and refuses with `403` and a plain-language message.
`coppercli.Tests/RequestGuardTests.cs` locks the regression — its first case is named
"the regression: a phone typing the bare address".

**Two things worth keeping from the token design, should authentication ever be
revisited:** it was deliberately *not* a cookie, because cookies ride along on cross-site
requests and that is exactly what makes a local server reachable from any page the operator
visits; and the credential was attached by a single `fetch` wrapper installed at module
scope, so no new call site could forget it. Neither changes the verdict — the credential
itself was the mistake.

**Scope note:** the raw GRBL bridge on port 34000 has never had any authentication and
still does not. That is a documented, deliberate choice, not an oversight — see the
`proxy → TCP clients` seam.

**Touches:** seam `web → browser (HTTP/WS)`, rule `web-ui-needs-no-typed-credential`,
rule `guard-covers-whole-surface`, `coppercli/WebServer/CncWebServer.cs`,
`coppercli/WebServer/wwwroot/js/auth.js`, `coppercli/WebServer/WebConstants.cs`.
