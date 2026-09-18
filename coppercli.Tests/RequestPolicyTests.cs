using coppercli.WebServer;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// The web UI has no login: it is reached by typing this machine's address on the local
    /// network, from a phone at the mill. A bare LAN address must therefore be allowed, while
    /// a page on another site and a rebound DNS name must not; refusing a hand-typed address
    /// loads the page and then fails every API call.
    /// </summary>
    public class RequestPolicyTests
    {
        [Theory]
        [InlineData("192.168.1.5:34001", null, null)]       // an address typed by hand sends no Origin
        [InlineData("192.168.1.5:34001", "", null)]
        [InlineData("[::1]:34001", null, null)]
        [InlineData("[fe80::1]:34001", null, null)]
        [InlineData("localhost:34001", null, null)]
        [InlineData("127.0.0.1:34001", null, null)]
        [InlineData("cnc.local:34001", null, null)]
        [InlineData("CNC.LOCAL:34001", null, null)]         // mDNS suffix match is case-insensitive
        [InlineData("cnc.local.:34001", null, null)]        // the absolute form of the same name
        [InlineData("localhost.:34001", null, null)]
        [InlineData("cnc:34001", null, null)]               // a single-label name from the router
        [InlineData("192.168.1.5:34001", "http://192.168.1.5:34001", null)]
        [InlineData("192.168.1.5", null, null)]             // Host may omit the port and still parse
        [InlineData("192.168.1.5:34001", null, "same-origin")]
        [InlineData("192.168.1.5:34001", null, "none")]     // "none" is a URL typed into the address bar
        public void RequestsAddressedToThisServer_AreAllowed(string host, string? origin, string? site)
        {
            Assert.True(RequestPolicy.IsAllowed(host, origin, site));
        }

        [Theory]
        [InlineData("192.168.1.5:34001", "http://evil.com", null)]
        [InlineData("192.168.1.5:34001", "http://192.168.1.5:8080", null)]
        [InlineData("192.168.1.5:34001", "https://192.168.1.5:34001", null)]  // an origin includes the scheme
        [InlineData(null, null, null)]
        [InlineData("", null, null)]
        [InlineData("   ", null, null)]
        [InlineData("192.168.1.5:34001", "not-a-url", null)]
        [InlineData("192.168.1.5:34001", "null", null)]     // what a sandboxed iframe sends
        public void RequestsFromAnotherSiteOrMalformed_AreRefused(string? host, string? origin, string? site)
        {
            Assert.False(RequestPolicy.IsAllowed(host, origin, site));
        }

        [Theory]
        [InlineData("cross-site")]
        [InlineData("same-site")]
        public void BrowserLabelledCrossSiteRequest_IsRefused_EvenWithoutOrigin(string site)
        {
            // Browsers omit Origin on a cross-site GET, so Sec-Fetch-Site is the only header
            // separating an img or script tag from a same-origin navigation. Browsers send it
            // only to a potentially-trustworthy URL, so this refusal covers localhost and TLS
            // while the plain-http LAN case depends on GET requests changing nothing (see
            // RequestPolicy's summary).
            Assert.False(RequestPolicy.IsAllowed("192.168.1.5:34001", null, site));
        }

        [Fact]
        public void DnsRebinding_IsRefusedEvenWhenOriginAgreesWithHost()
        {
            // Host and Origin agree, so the Origin check passes. Only the Host check
            // catches it - see RequestPolicy's summary for why.
            Assert.False(RequestPolicy.IsAllowed("evil.com:34001", "http://evil.com:34001", null));
        }

        [Theory]
        [InlineData("attacker.evil.local:34001")]
        [InlineData("a.b.c.local:34001")]
        [InlineData(".local:34001")]
        public void MultiLabelNameUnderLocal_IsRefused(string host)
        {
            // mDNS resolves one label before ".local". Anything deeper is ordinary unicast
            // DNS that the zone's owner controls, so a bare suffix test would admit the
            // rebinding case.
            Assert.False(RequestPolicy.IsAllowed(host, null, null));
        }

        [Theory]
        [InlineData("2130706433:34001")]                // decimal 127.0.0.1
        [InlineData("0x7f000001:34001")]                // hex
        [InlineData("[0:0:0:0:0:0:0:1]:34001")]         // uncompressed IPv6 loopback
        [InlineData("[::ffff:192.168.1.5]:34001")]      // IPv4-mapped IPv6
        public void EveryFormOfAnAddressLiteral_IsAccepted(string host)
        {
            // Uri normalizes each of these to an address literal. A literal cannot be pointed
            // at this machine by anyone else, so only the Origin check applies.
            Assert.True(RequestPolicy.IsAllowed(host, null, null));
        }

        [Theory]
        [InlineData("[0:0:0:0:0:0:0:1]:34001", "http://[::1]:34001")]
        [InlineData("[::1]:34001", "http://[0:0:0:0:0:0:0:1]:34001")]
        public void IPv6OriginMatchesHostAcrossSpellings(string host, string origin)
        {
            Assert.True(RequestPolicy.IsAllowed(host, origin, null));
        }

        [Theory]
        [InlineData("evil.com@192.168.1.5:34001")]      // userinfo hiding the real name
        [InlineData("192.168.1.5:34001/evil.com")]      // a path appended to the authority
        [InlineData("192.168.1.5:34001?x=1")]
        [InlineData("192.168.1.5:34001#x")]
        public void HostHeaderWithExtraContent_IsRefused(string host)
        {
            Assert.False(RequestPolicy.IsAllowed(host, null, null));
        }
    }
}
