using System.Diagnostics.CodeAnalysis;
using System.Net;
using coppercli.Helpers;
using static coppercli.WebServer.WebConstants;

namespace coppercli.WebServer;

/// <summary>
/// Decides whether a request may reach the machine.
///
/// The web UI has no login. Operators open it by typing the machine's local address on a
/// phone, and every device on that network is trusted. Requests from other sites and
/// requests addressed through public domain names are refused. Three request headers
/// help distinguish them; browser pages cannot set these headers:
///
///   Host           - the address the request was sent to. A name the wider internet can
///                    resolve means the browser reached us through a domain the caller
///                    controls. That is DNS rebinding: the attacker's page re-resolves
///                    its own name to this address and inherits same-origin standing. The
///                    Origin check does not catch it, because by then the browser treats
///                    the request as same-origin.
///   Origin         - the site the request was issued from. A mismatch means a page the
///                    operator happens to be visiting is trying to drive the mill. Present
///                    on everything that can change state, absent on every GET.
///   Sec-Fetch-Site - the browser's own account of that relationship, and the only header
///                    that would distinguish a cross-site GET. It is read where it arrives,
///                    but it is no defense here: browsers attach the Sec-Fetch-* family
///                    only to a potentially-trustworthy URL, so a plain-http LAN address
///                    never receives it. It does arrive over localhost, and would arrive
///                    if this were ever served over TLS.
///
/// None of the three is evidence of where the request came from - every one is chosen by
/// the caller. So the peer's own address is checked first and separately: it is the only
/// value here the caller cannot write, and without it "trusted on this network" would
/// mean "trusted from anywhere that can reach the port".
///
/// A cross-site GET without Origin or Sec-Fetch-Site cannot be distinguished from
/// navigation and is allowed. Keep GET handlers free of machine and file changes.
/// </summary>
internal static class RequestPolicy
{
    /// <summary>Host must contain only an authority; these characters could add a path,
    /// query, fragment, or user info to the value compared with Origin.</summary>
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
            // A non-browser client, the operator's navigation, and a cross-site GET can
            // all omit Origin. Keep GET handlers read-only; routes that move the machine,
            // start a job, or write a file must use POST.
            return true;
        }

        return Uri.TryCreate(originHeader, UriKind.Absolute, out var origin)
               && origin.Scheme == Uri.UriSchemeHttp
               && string.Equals(origin.Host, host.Host, StringComparison.OrdinalIgnoreCase)
               && origin.Port == host.Port;
    }

    /// <summary>
    /// Accept an address literal, a single-label name, or mDNS "name.local" because
    /// public DNS cannot delegate those names. Refuse deeper names such as
    /// "host.zone.local", which unicast DNS can redirect here.
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

    /// <summary>Reads a "host:port" authority. Parsing with <see cref="Uri"/> rather than
    /// splitting on ':' handles bracketed IPv6 ("[::1]:8080").</summary>
    private static bool TryParseAuthority(string? authority, [NotNullWhen(true)] out Uri? parsed)
    {
        parsed = null;

        return !string.IsNullOrWhiteSpace(authority)
               && authority.IndexOfAny(NonAuthorityChars) < 0
               && Uri.TryCreate($"{Uri.UriSchemeHttp}://{authority}", UriKind.Absolute, out parsed)
               && !string.IsNullOrEmpty(parsed.Host);
    }
}
