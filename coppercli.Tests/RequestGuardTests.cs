using coppercli.WebServer;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// The web UI has no login: it is meant to be reached by typing this machine's address
    /// on the local network, from a phone while standing at the mill.
    ///
    /// The regression this pins: a per-run access token was carried in the printed link, so
    /// an address typed by hand loaded the page and then met a refusal on every API call.
    /// These tests hold the replacement to its side of the bargain - a bare LAN address
    /// works, while a page on another site and a rebound DNS name do not.
    /// </summary>
    public class RequestGuardTests
    {
        [Theory]
        [InlineData("192.168.1.5:34001", null, null)]       // a phone typing the bare address
        [InlineData("192.168.1.5:34001", "", null)]
        [InlineData("[::1]:34001", null, null)]
        [InlineData("[fe80::1]:34001", null, null)]
        [InlineData("localhost:34001", null, null)]
        [InlineData("127.0.0.1:34001", null, null)]
        [InlineData("cnc.local:34001", null, null)]
        [InlineData("CNC.LOCAL:34001", null, null)]         // mDNS suffix match is case-insensitive
        [InlineData("cnc.local.:34001", null, null)]        // the absolute form of the same name
        [InlineData("localhost.:34001", null, null)]
        [InlineData("cnc:34001", null, null)]               // a hostname handed out by the router
        [InlineData("192.168.1.5:34001", "http://192.168.1.5:34001", null)]
        [InlineData("192.168.1.5", null, null)]             // Host may omit the port and still parse
        [InlineData("192.168.1.5:34001", null, "same-origin")]
        [InlineData("192.168.1.5:34001", null, "none")]     // typed into the address bar
        public void RequestsAddressedToThisServer_AreAllowed(string host, string? origin, string? site)
        {
            Assert.True(RequestGuard.IsAllowed(host, origin, site));
        }

        [Theory]
        [InlineData("192.168.1.5:34001", "http://evil.com", null)]
        [InlineData("192.168.1.5:34001", "http://192.168.1.5:8080", null)]
        [InlineData("192.168.1.5:34001", "https://192.168.1.5:34001", null)]  // an origin is scheme too
        [InlineData(null, null, null)]
        [InlineData("", null, null)]
        [InlineData("   ", null, null)]
        [InlineData("192.168.1.5:34001", "not-a-url", null)]
        [InlineData("192.168.1.5:34001", "null", null)]     // what a sandboxed iframe sends
        public void RequestsFromAnotherSiteOrMalformed_AreRefused(string? host, string? origin, string? site)
        {
            Assert.False(RequestGuard.IsAllowed(host, origin, site));
        }

        [Theory]
        [InlineData("cross-site")]
        [InlineData("same-site")]
        public void BrowserLabelledCrossSiteRequest_IsRefused_EvenWithoutOrigin(string site)
        {
            // Browsers omit Origin entirely on a cross-site GET, so an img or script tag
            // aimed at the machine reaches the Origin check looking exactly like a
            // same-origin navigation. Sec-Fetch-Site is the only thing that would tell them
            // apart - but browsers send it only to a potentially-trustworthy URL, so over
            // plain http it never arrives and this refusal never fires. What is locked here
            // is the localhost and TLS case; the plain-http LAN case is covered instead by
            // the rule that no GET changes anything. See RequestGuard's summary.
            Assert.False(RequestGuard.IsAllowed("192.168.1.5:34001", null, site));
        }

        [Fact]
        public void DnsRebinding_IsRefusedEvenWhenOriginAgreesWithHost()
        {
            // Host and Origin agree, so the Origin check passes. Only the Host check
            // catches it - see RequestGuard's summary for why.
            Assert.False(RequestGuard.IsAllowed("evil.com:34001", "http://evil.com:34001", null));
        }

        [Theory]
        [InlineData("attacker.evil.local:34001")]
        [InlineData("a.b.c.local:34001")]
        [InlineData(".local:34001")]
        public void MultiLabelNameUnderLocal_IsRefused(string host)
        {
            // mDNS answers for one label before ".local". Anything deeper is ordinary
            // unicast DNS, resolvable by whoever runs the zone - so a bare suffix test
            // would hand the rebinding case straight back.
            Assert.False(RequestGuard.IsAllowed(host, null, null));
        }

        [Theory]
        [InlineData("2130706433:34001")]                // decimal 127.0.0.1
        [InlineData("0x7f000001:34001")]                // hex
        [InlineData("[0:0:0:0:0:0:0:1]:34001")]         // uncompressed IPv6 loopback
        [InlineData("[::ffff:192.168.1.5]:34001")]      // IPv4-mapped IPv6
        public void EveryFormOfAnAddressLiteral_IsAccepted(string host)
        {
            // Uri normalises all of these to a literal, and a literal cannot be aimed here
            // by anyone else, so the Origin check remains the only gate that matters.
            Assert.True(RequestGuard.IsAllowed(host, null, null));
        }

        [Theory]
        [InlineData("[0:0:0:0:0:0:0:1]:34001", "http://[::1]:34001")]
        [InlineData("[::1]:34001", "http://[0:0:0:0:0:0:0:1]:34001")]
        public void IPv6OriginMatchesHostAcrossSpellings(string host, string origin)
        {
            Assert.True(RequestGuard.IsAllowed(host, origin, null));
        }

        [Theory]
        [InlineData("evil.com@192.168.1.5:34001")]      // userinfo hiding the real name
        [InlineData("192.168.1.5:34001/evil.com")]      // a path smuggled into the authority
        [InlineData("192.168.1.5:34001?x=1")]
        [InlineData("192.168.1.5:34001#x")]
        public void HostCarryingAnythingBeyondAnAuthority_IsRefused(string host)
        {
            Assert.False(RequestGuard.IsAllowed(host, null, null));
        }
    }
}
