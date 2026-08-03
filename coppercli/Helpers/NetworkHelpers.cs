using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace coppercli.Helpers
{
    /// <summary>
    /// Helper methods for network operations.
    /// </summary>
    internal static class NetworkHelpers
    {
        /// <summary>
        /// Address blocks no host on the public internet can be reached at, so a peer
        /// inside one shares a network with us: RFC 1918 private use, RFC 3927 link-local,
        /// and RFC 6598 carrier-grade NAT, which VPN meshes hand out.
        /// </summary>
        private static readonly (byte[] Prefix, int Bits)[] PrivateIPv4Blocks =
        {
            (new byte[] { 10, 0, 0, 0 }, 8),
            (new byte[] { 172, 16, 0, 0 }, 12),
            (new byte[] { 192, 168, 0, 0 }, 16),
            (new byte[] { 169, 254, 0, 0 }, 16),
            (new byte[] { 100, 64, 0, 0 }, 10),
        };

        /// <summary>
        /// True if <paramref name="address"/> is on a network this machine is also on.
        /// Being able to reach us is not the same as being local: a forwarded port or a
        /// globally routable IPv6 address carries requests here from anywhere, and the web
        /// UI trusts whoever it lets in. Everyone genuinely sharing the network is trusted,
        /// which is the owner's decision - a hotspot or a cafe LAN counts as sharing it.
        /// </summary>
        public static bool IsLocalPeer(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            return IPAddress.IsLoopback(address)
                   || IsPrivateAddress(address)
                   || SharesSubnetWithLocalInterface(address);
        }

        /// <summary>True if the address falls in a block reserved for private networks.</summary>
        public static bool IsPrivateAddress(IPAddress address)
        {
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
            }

            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                return false;
            }

            var bytes = address.GetAddressBytes();

            foreach (var (prefix, bits) in PrivateIPv4Blocks)
            {
                if (MatchesPrefix(bytes, prefix, bits))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True if the address sits in the same subnet as one of this machine's own
        /// interfaces. This is what admits a LAN peer holding a globally routable IPv6
        /// address, which no fixed list of private blocks can recognise.
        /// </summary>
        private static bool SharesSubnetWithLocalInterface(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            NetworkInterface[] interfaces;

            try
            {
                interfaces = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch (NetworkInformationException ex)
            {
                Logger.Log("IsLocalPeer: cannot enumerate interfaces, refusing {0}: {1}", address, ex.Message);
                return false;
            }

            foreach (var iface in interfaces)
            {
                // Scoped to one interface: an adapter going down mid-scan - a VPN, a
                // docker bridge, Wi-Fi roaming - must not decide the question for the
                // others, or a peer this is meant to admit is refused at random.
                try
                {
                    if (iface.OperationalStatus != OperationalStatus.Up)
                    {
                        continue;
                    }

                    foreach (var local in iface.GetIPProperties().UnicastAddresses)
                    {
                        if (local.Address.AddressFamily != address.AddressFamily)
                        {
                            continue;
                        }

                        if (MatchesPrefix(bytes, local.Address.GetAddressBytes(), local.PrefixLength))
                        {
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log("IsLocalPeer: skipping interface {0}: {1}", iface.Name, ex.Message);
                }
            }

            return false;
        }

        /// <summary>Compares the leading <paramref name="bits"/> of two addresses.</summary>
        private static bool MatchesPrefix(byte[] address, byte[] prefix, int bits)
        {
            // A zero-length prefix would match every address on earth. An interface that
            // reports one tells us nothing about who is local, so it vouches for nobody.
            if (address.Length != prefix.Length || bits <= 0 || bits > address.Length * 8)
            {
                return false;
            }

            for (int i = 0; i < address.Length; i++)
            {
                int significant = Math.Min(8, bits - (i * 8));

                if (significant <= 0)
                {
                    return true;
                }

                int mask = 0xFF << (8 - significant);

                if ((address[i] & mask) != (prefix[i] & mask))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Gets the local IPv4 addresses for display to the user.
        /// Filters out loopback and link-local addresses.
        /// </summary>
        public static List<string> GetLocalIPAddresses()
        {
            var addresses = new List<string>();

            try
            {
                foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    // Skip loopback, down interfaces, and virtual adapters
                    if (iface.OperationalStatus != OperationalStatus.Up)
                    {
                        continue;
                    }
                    if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    var props = iface.GetIPProperties();
                    foreach (var addr in props.UnicastAddresses)
                    {
                        // Only IPv4 addresses
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                        {
                            continue;
                        }

                        var ip = addr.Address.ToString();

                        // Skip loopback and link-local
                        if (ip.StartsWith("127.") || ip.StartsWith("169.254."))
                        {
                            continue;
                        }

                        if (!addresses.Contains(ip))
                        {
                            addresses.Add(ip);
                        }
                    }
                }
            }
            catch
            {
                // If we can't enumerate interfaces, try the simpler approach
                try
                {
                    var hostName = System.Net.Dns.GetHostName();
                    var hostEntry = System.Net.Dns.GetHostEntry(hostName);
                    foreach (var addr in hostEntry.AddressList)
                    {
                        if (addr.AddressFamily == AddressFamily.InterNetwork)
                        {
                            var ip = addr.ToString();
                            if (!ip.StartsWith("127.") && !ip.StartsWith("169.254.") && !addresses.Contains(ip))
                            {
                                addresses.Add(ip);
                            }
                        }
                    }
                }
                catch
                {
                    // Give up - caller will handle empty list
                }
            }

            return addresses;
        }
    }
}
