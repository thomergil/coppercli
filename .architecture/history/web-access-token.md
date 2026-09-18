# A per-run access token made the web UI unreachable from a phone

**Problem:** The page shell loaded on a phone and then every `/api/*` call and the WebSocket
upgrade returned 401, leaving a dead UI that showed no error.

**Cause:** `CncWebServer.Run` generated a 32-hex-character token
(`Guid.NewGuid().ToString("N")`) at startup and appended it to every URL it printed.
`IsAuthorised` required it, as a `Bearer` header or a `?token=` query parameter, on `/api/*`
and on the `/ws` upgrade; static files were served without it. `auth.js` lifted the token out
of the query string into `sessionStorage`, stripped it from the address bar, and
monkey-patched `window.fetch` to attach the `Authorization` header to every `/api/` call. The
design assumed the operator would follow the printed link. The main use of the web UI is
walking up to the machine with a phone and typing a LAN address, and nobody types 32 hex
characters. Leaving static files unguarded made the failure silent. The evidence was already
in the tree: the README ships a phone jog screenshot, and `WebConstants` sizes the
idle-disconnect timeout around "phone screen went dark". The owner had already said "Web
exposure is fine" before the token was added.

**Fix:** The token was removed the same day, during the `4698964` sweep.
`RequestPolicy.IsAllowed` runs once in `HandleRequest` before any routing branch, so it covers
static files, the API and the socket alike, and refuses with 403 and a plain-language
message. `coppercli.Tests/RequestPolicyTests.cs` locks the regression; its first case is named
"the regression: a phone typing the bare address". `coppercli/WebServer/CncWebServer.cs`,
`coppercli/WebServer/wwwroot/js/auth.js`, `coppercli/WebServer/WebConstants.cs`; rules
`web-ui-needs-no-typed-credential`, `request-policy-checks-all-routes`; interface
`web → browser (HTTP/WS)`. The raw GRBL bridge on port 34000 has never had authentication and
still does not; that is a deliberate decision recorded on the `proxy → TCP clients` interface.

**Rejected:** An `Origin` check alone as the replacement. `Origin` stops a cross-site page
from driving the machine, but it does not stop DNS rebinding, where an attacker-controlled
name resolves to the LAN address and the browser then treats the requests as same-origin. A
`Host` check restricting the host to IP literals, `localhost` and `.local` names closes that,
because a rebinding attack needs a resolvable DNS name.
Two parts of the token design are worth keeping if authentication is ever revisited: it was
deliberately not a cookie, because cookies ride along on cross-site requests, which is what
makes a local server reachable from any page the operator visits; and the credential was
attached by a single `fetch` wrapper installed at module scope, so no new call site could
forget it. Neither changes the verdict that the credential itself was the mistake.

**Rule:** Keep the web UI reachable through a typed LAN address without a token, password or PIN. Check `Host` and `Origin` before routing any request, including static files. Explain a refusal in the response.