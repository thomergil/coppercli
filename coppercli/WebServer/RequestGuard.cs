using System.Diagnostics.CodeAnalysis;
using System.Net;
using coppercli.Helpers;
using static coppercli.WebServer.WebConstants;

namespace coppercli.WebServer;

/// <summary>
/// Decides whether a request may reach the machine.
///
/// There is no login. The web UI is meant to be opened by typing this machine's address
/// on the local network, from a phone while standing at the mill, so any check costing
/// the operator a keystroke is the wrong check. Every device on the network is trusted.
/// A browser pointed at another site is not, and neither is a name that merely resolves
/// here. Three headers separate those cases, none of which a page can set:
///
///   Host           - the address the request was sent to. A name the wider internet can
///                    resolve means the browser was told to reach us through a domain the
///                    caller owns. That is DNS rebinding: the attacker's page re-resolves
///                    its own name to this address and inherits same-origin standing. The
///                    Origin check cannot see it, because by then the browser genuinely
///                    believes it is same-origin.
///   Origin         - the site the request was issued from. A mismatch means a page the
///                    operator happens to be visiting is trying to drive the mill. Present
///                    on everything that can change state, absent on every GET.
///   Sec-Fetch-Site - the browser's own account of that relationship, and the only thing
///                    that would distinguish a cross-site GET. Read where it arrives, but
///                    it is NOT a defence here: browsers attach the Sec-Fetch-* family
///                    only to a potentially-trustworthy URL, so a plain-http LAN address
///                    never receives it. It works over localhost, and would if this were
///                    ever served over TLS.
///
/// None of the three is evidence of where the request came from - every one is chosen by
/// the caller. So the peer's own address is checked first and separately: it is the only
/// thing here the caller cannot write, and without it "trusted on this network" would
/// mean "trusted from anywhere that can reach the port".
///
/// What that leaves open, deliberately: a cross-site GET carries no Origin and no
/// Sec-Fetch-Site, so it is indistinguishable from the operator's own navigation and is
/// allowed. It is harmless only while no GET changes anything worth protecting, which is
/// a rule the rest of the server has to keep - see the Origin branch below.
/// </summary>
internal static class RequestGuard
{
    /// <summary>A Host header carries an authority and nothing else. These would smuggle a
    /// path, query, or userinfo into the value compared against Origin.</summary>
    private static readonly char[] NonAuthorityChars = { '/', '?', '#', '@' };

    public static bool IsAllowed(HttpListenerRequest request)
    {
        var peer = request.RemoteEndPoint?.Address;

        if (peer == null || !NetworkHelpers.IsLocalPeer(peer))
        {
            return false;
        }

        return IsAllowed(request.UserHostName,
                         request.Headers[HeaderOrigin],
                         request.Headers[HeaderSecFetchSite]);
    }

    /// <param name="hostHeader">Host header, such as "192.168.1.5:8080".</param>
    /// <param name="originHeader">Origin header, absent for a non-browser caller.</param>
    /// <param name="secFetchSite">Sec-Fetch-Site header, absent for a non-browser caller.</param>
    public static bool IsAllowed(string? hostHeader, string? originHeader, string? secFetchSite)
    {
        if (!TryParseAuthority(hostHeader, out var host) || !IsLocalAddress(host))
        {
            return false;
        }

        if (IsAnotherSite(secFetchSite))
        {
            return false;
        }

        if (string.IsNullOrEmpty(originHeader))
        {
            // Three callers arrive here and cannot be told apart: a non-browser client, the
            // operator's own navigation, and a cross-site GET - an <img> or <script> on
            // another site pointed at this port. Browsers send no Origin on any of them.
            //
            // So this branch is safe only while no GET changes anything worth protecting.
            // Every endpoint in HandleApi that moves the machine, starts a job, or writes a
            // file is behind POST today, and any endpoint added later has to be too.
            return true;
        }

        return Uri.TryCreate(originHeader, UriKind.Absolute, out var origin)
               && origin.Scheme == Uri.UriSchemeHttp
               && string.Equals(origin.Host, host.Host, StringComparison.OrdinalIgnoreCase)
               && origin.Port == host.Port;
    }

    /// <summary>
    /// True if the Host names this server directly rather than through a name someone
    /// outside could aim here: an address literal, or a single-label name. A single label
    /// cannot be delegated in public DNS, so only this network can answer for it. That
    /// covers the mDNS "name.local" form too, whose one label is exactly what mDNS
    /// answers for - a deeper "host.zone.local" is ordinary unicast DNS, resolvable by
    /// whoever runs the zone, which is the rebinding case this check exists to stop.
    /// </summary>
    private static bool IsLocalAddress(Uri host)
    {
        if (host.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            return true;
        }

        // A trailing dot is the absolute form of the same name.
        string name = host.Host.TrimEnd('.');

        if (name.EndsWith(HostMdnsSuffix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^HostMdnsSuffix.Length];
        }

        return name.Length > 0 && !name.Contains('.');
    }

    /// <summary>True if the browser reports the caller as some origin other than ours.
    /// Absent means no browser, which the Origin check handles instead.</summary>
    private static bool IsAnotherSite(string? secFetchSite)
    {
        return string.Equals(secFetchSite, SecFetchSiteCrossSite, StringComparison.OrdinalIgnoreCase)
               || string.Equals(secFetchSite, SecFetchSiteSameSite, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads a "host:port" authority. Borrowing <see cref="Uri"/> rather than
    /// splitting on ':' gets bracketed IPv6 ("[::1]:8080") right.</summary>
    private static bool TryParseAuthority(string? authority, [NotNullWhen(true)] out Uri? parsed)
    {
        parsed = null;

        return !string.IsNullOrWhiteSpace(authority)
               && authority.IndexOfAny(NonAuthorityChars) < 0
               && Uri.TryCreate($"{Uri.UriSchemeHttp}://{authority}", UriKind.Absolute, out parsed)
               && !string.IsNullOrEmpty(parsed.Host);
    }
}
