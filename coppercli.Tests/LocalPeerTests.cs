using System.Net;
using coppercli.Helpers;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// The web server authenticates nothing, so IsLocalPeer is the only check on who may
    /// drive the machine. An address admitted wrongly puts a running spindle on the public
    /// internet, and one refused wrongly locks the operator's own phone out. The addresses
    /// here are the first and last of each private block, where an off-by-one in a mask
    /// produces both faults at once.
    /// </summary>
    public class LocalPeerTests
    {
        [Theory]
        [InlineData("192.168.1.42")]
        [InlineData("192.168.0.1")]
        [InlineData("10.0.0.5")]
        [InlineData("10.255.255.254")]
        [InlineData("172.16.0.1")]
        [InlineData("172.31.255.254")]
        [InlineData("169.254.3.4")]      // link-local, assigned when DHCP does not respond
        [InlineData("100.64.0.1")]       // carrier-grade NAT, used by VPN meshes
        [InlineData("100.127.255.254")]
        public void AddressesOnAPrivateNetwork_AreLocal(string address)
        {
            Assert.True(NetworkHelpers.IsPrivateAddress(IPAddress.Parse(address)));
            Assert.True(NetworkHelpers.IsLocalPeer(IPAddress.Parse(address)));
        }

        [Theory]
        [InlineData("172.15.255.255")]   // one below 172.16/12
        [InlineData("172.32.0.0")]       // one above it
        [InlineData("100.63.255.255")]   // one below 100.64/10
        [InlineData("100.128.0.0")]      // one above it
        [InlineData("9.255.255.255")]
        [InlineData("11.0.0.0")]
        [InlineData("8.8.8.8")]
        [InlineData("2001:4860:4860::8888")]
        public void AddressesOutsideAPrivateBlock_AreNotPrivate(string address)
        {
            // IsLocalPeer may still admit one of these when it shares a subnet with a real
            // interface on the host, so only the block test is deterministic here.
            Assert.False(NetworkHelpers.IsPrivateAddress(IPAddress.Parse(address)));
        }

        [Theory]
        [InlineData("203.0.113.7")]        // TEST-NET-3
        [InlineData("198.51.100.7")]       // TEST-NET-2
        [InlineData("2001:db8::1")]        // IPv6 documentation range
        public void AddressesReservedForDocumentation_AreRefused(string address)
        {
            // Nothing routes these ranges, so no interface on the host can share a subnet
            // with one and make the result ambiguous.
            Assert.False(NetworkHelpers.IsLocalPeer(IPAddress.Parse(address)));
        }

        [Theory]
        [InlineData("127.0.0.1")]
        [InlineData("127.1.2.3")]
        [InlineData("::1")]
        [InlineData("::ffff:127.0.0.1")] // IPv4-mapped loopback, as a dual-stack socket reports it
        public void Loopback_IsLocal(string address)
        {
            Assert.True(NetworkHelpers.IsLocalPeer(IPAddress.Parse(address)));
        }

        [Theory]
        [InlineData("fe80::1")]          // IPv6 link-local
        [InlineData("fd00::1")]          // IPv6 unique local
        [InlineData("fdff:ffff::1")]
        public void PrivateIPv6_IsLocal(string address)
        {
            Assert.True(NetworkHelpers.IsPrivateAddress(IPAddress.Parse(address)));
            Assert.True(NetworkHelpers.IsLocalPeer(IPAddress.Parse(address)));
        }

        [Fact]
        public void IPv4MappedPrivateAddress_IsLocal()
        {
            // A dual-stack listener reports an IPv4 peer in this form, so the mapping has to
            // be unwrapped before the blocks are consulted or every LAN phone is refused.
            Assert.True(NetworkHelpers.IsLocalPeer(IPAddress.Parse("::ffff:192.168.1.42")));
        }
    }
}
