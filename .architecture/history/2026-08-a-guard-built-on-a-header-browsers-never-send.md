# 2026-08 — A guard built on a header browsers never send

**Who:** Thomer, with Claude Opus 5, finishing the removal begun in `2026-08-web-access-token`.

**Shipped:** the per-run token is gone. `RequestGuard.IsAllowed` runs once at the top of
`HandleRequest`, before any routing branch, and admits a request only if all four hold: the
peer's source address is local (`NetworkHelpers.IsLocalPeer` — loopback, RFC 1918/3927/6598,
IPv6 link-local/ULA, or sharing a subnet with a live interface); `Host` is an address
literal, a single-label name, or a single-label `name.local`; `Sec-Fetch-Site` is neither
`cross-site` nor `same-site`; and `Origin`, when present, matches host, port, and scheme.
Refusal is `403` — JSON on the API and socket, plain text for a page load, so a person
reading it gets a sentence. Every response carries `X-Frame-Options: DENY`, CSP
`frame-ancestors 'none'`, `nosniff`, and `no-referrer`.

**Realized — the `Sec-Fetch-Site` check is inert on the configuration that ships.** W3C
Fetch Metadata says: "If r's url is not a potentially trustworthy URL, return." A plain-http
LAN address is not potentially trustworthy, so a browser attaches no `Sec-Fetch-*` header to
`http://192.168.1.5:34001`. The check is kept because it does work over `localhost`
and would over TLS, but it defends nothing in the shipping case. A raw-socket test misled us
at first: a raw socket can set the header, which proves only that the server reads it, never
that a browser sends it. **Do not claim** — in code, README, or release notes — that it
blocks cross-site GETs.

**Realized (the consequence) — a cross-site GET is indistinguishable from the operator's own
navigation.** No `Origin`, no `Sec-Fetch-Site`: an `<img>` or `<script>` on any page the
operator visits reaches this server and is allowed. That is safe only while no GET changes
anything worth protecting — an invariant now load-bearing and enforced by nothing. It is
already frayed: `GET /api/probe/status` calls `AppState.EnsureProbeDataLoaded()`, which
mutates probe state. It is tolerable only because nothing there moves the machine.

**Realized (the bug that proved it) — a GET must never reserve the single client slot.**
`ServeStaticFile` used to write `_pendingClients[...]` on every page fetch. A cross-site
`<img>` aimed at any extension-less path therefore minted phantom pending clients; the
operator's own WebSocket then saw "already connected", skipped `_machine.Connect()`, and
offered a force-disconnect that can drop the serial port mid-cut. The reservation now lives
only in `HandleWebSocket`, whose upgrade always carries an `Origin`.

**Realized — the shape of `Host` is not evidence of where a request came from.** Checking
`Host` alone meant anyone who could route to port 34001 could send `Host: 127.0.0.1` and
drive the mill. The peer's source address is the only input in the request the caller cannot
write, so it is checked first and separately. Its limit: it cannot see through a
locally-terminating tunnel or proxy — `ssh -R`, ngrok, nginx — where the peer becomes
`127.0.0.1`.

**Realized — a `finally { response.Close(); }` around a handler runs before the enclosing
`catch`.** Writing a 500 from that outer catch hits a closed response and degrades silently
to an empty 200. Failures are answered in a `catch` *inside* the `finally`'s `try`. This is
the same silent-empty-200 shape as the wrong-method gap below; they are one failure mode,
not two.

**Owner's decisions — settled, do not re-litigate.** LAN peers are deliberately
unauthenticated: the owner was offered a PIN and a full revert, and took neither. The plain
typed address must keep working with zero keystrokes. Single-label host names are allowed
and dotted ones refused on purpose — a single label cannot be delegated in public DNS, so
only this network can answer for it, which is why `cnc` and `cnc.local` work. `mill.lan`,
`mill.home.arpa`, and any AD or search-domain name are refused, because accepting a
multi-label name is what makes DNS rebinding possible. `host.zone.local` is refused for the
same reason: mDNS answers for only one label before `.local`. The usability cost is known
and accepted.

**Still open, surfaced to the owner and not fixed:** about 28 endpoints in `HandleApi`
answer a wrong-method request with an empty 200 rather than 405 — `ApiProbeApply` is the
only one that does it right. And `NetworkHelpers.GetLocalIPAddresses` still filters on raw
`"127."`/`"169.254."` string literals and walks the interfaces a second time. It is
display-only and must not be mistaken for a security input, unlike its hardened neighbour
`IsLocalPeer`.

**Lesson → rule `no-side-effect-on-get`.** Guard with what the browser supplies for free,
then write down exactly what that leaves open. Here it is one sentence: a GET changes
nothing — no motion, no file written, no state loaded, no client slot reserved. Everything
that changes state is POST and stays POST.

**Touches:** seam `web → browser (HTTP/WS)` (v2 → v3), rule `no-side-effect-on-get`, rule
`web-ui-needs-no-typed-credential`, `coppercli/WebServer/RequestGuard.cs`,
`coppercli/WebServer/CncWebServer.cs`, `coppercli/Helpers/NetworkHelpers.cs`,
`coppercli.Tests/RequestGuardTests.cs`, `coppercli.Tests/LocalPeerTests.cs`,
`coppercli.Tests/RequestGuardListenerTests.cs`.
