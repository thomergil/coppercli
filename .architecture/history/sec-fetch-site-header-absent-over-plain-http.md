# Browsers omit Sec-Fetch-Site on plain HTTP LAN requests

**Problem:** The per-run token was replaced by a request guard. On the configuration that
ships — plain HTTP on a LAN address — the guard's `Sec-Fetch-Site` check never sees a header,
so it blocks nothing, and a cross-site GET is indistinguishable from the operator's own
navigation. An `<img>` or `<script>` on any page the operator visits reaches this server and
is allowed.

**Cause:** W3C Fetch Metadata says "If r's url is not a potentially trustworthy URL, return."
A plain-http LAN address is not potentially trustworthy, so a browser attaches no
`Sec-Fetch-*` header to `http://192.168.1.5:34001`. A raw-socket test misled us at first: a
raw socket can set the header, which proves only that the server reads it, never that a
browser sends it.

**Fix:** `RequestPolicy.IsAllowed` runs once at the top of `HandleRequest`, before any routing
branch, and admits a request only when all four hold: the peer's source address is local
(`NetworkHelpers.IsLocalPeer` — loopback, RFC 1918/3927/6598, IPv6 link-local or ULA, or
sharing a subnet with a live interface); `Host` is an address literal, a single-label name, or
a single-label `name.local`; `Sec-Fetch-Site` is neither `cross-site` nor `same-site`; and
`Origin`, when present, matches host, port and scheme. Refusal is 403, JSON on the API and
socket, plain text for a page load. Every response carries `X-Frame-Options: DENY`, CSP
`frame-ancestors 'none'`, `nosniff` and `no-referrer`. The `Sec-Fetch-Site` check is kept
because it works over `localhost` and would over TLS, but do not claim in code, README or
release notes that it blocks cross-site GETs.
The peer address is checked first and separately because it is the only input in the request
the caller cannot write; checking `Host` alone let anyone who could route to port 34001 send
`Host: 127.0.0.1` and drive the mill. `IsLocalPeer` cannot see through a locally terminating
tunnel or proxy (`ssh -R`, ngrok, nginx), where the peer becomes `127.0.0.1`.
`ServeStaticFile` used to write `_pendingClients[...]` on every page fetch, so a cross-site
`<img>` aimed at any extension-less path minted phantom pending clients; the operator's own
WebSocket then saw "already connected", skipped `_machine.Connect()`, and offered a
force-disconnect that can drop the serial port mid-cut. The reservation now lives only in
`HandleWebSocket`, whose upgrade always carries an `Origin`.
A `finally { response.Close(); }` around a handler runs before the enclosing `catch`, so
writing a 500 from that outer catch hits a closed response and degrades silently to an empty
200; failures are answered in a `catch` inside the `finally`'s `try`.
`GET /api/probe/status` used to adopt the autosave as a side effect; that is closed —
`ReadUsableAutosave` reads without adopting, and only the probe start paths call
`EnsureProbeDataLoaded` (`usable-probe-data-computed-in-three-places.md`).
Still open: about 28 endpoints in `HandleApi` answer a wrong-method request with an empty 200
rather than 405, and `ApiProbeApply` is the only one that answers correctly.
`NetworkHelpers.GetLocalIPAddresses` still filters on raw `"127."` and `"169.254."` string
literals and walks the interfaces a second time; it is display-only and is not a security
input, unlike `IsLocalPeer`.
`coppercli/WebServer/RequestPolicy.cs`, `coppercli/WebServer/CncWebServer.cs`,
`coppercli/Helpers/NetworkHelpers.cs`, `coppercli.Tests/RequestPolicyTests.cs`,
`coppercli.Tests/LocalPeerTests.cs`, `coppercli.Tests/RequestPolicyListenerTests.cs`; rules
`no-side-effect-on-get`, `web-ui-needs-no-typed-credential`; interface
`web → browser (HTTP/WS)` v2 → v3.

**Rejected:** A PIN and a full revert were both offered to the owner and refused. LAN peers
are deliberately unauthenticated and the plain typed address must keep working with zero
keystrokes. Multi-label host names are refused on purpose: a single label cannot be delegated
in public DNS, so only this network can answer for it, which is why `cnc` and `cnc.local`
work while `mill.lan`, `mill.home.arpa` and any AD or search-domain name do not.
`host.zone.local` is refused because mDNS answers for only one label before `.local`.
Accepting a multi-label name is what makes DNS rebinding possible. The usability cost is
known and accepted.

**Rule:** Use request data that browsers send over plain HTTP. Keep every GET free of machine motion, file writes, state loads and client reservation; use POST for those changes.
