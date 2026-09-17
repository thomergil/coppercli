using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace coppercli.Helpers
{
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
        /// True if <paramref name="address"/> is on a network this machine is also on, which
        /// is narrower than being able to reach us: a forwarded port or a globally routable
        /// IPv6 address carries requests here from anywhere. Everyone on the same network is
        /// trusted by the web UI, a hotspot or a cafe LAN included.
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
        /// This is what admits a LAN peer holding a globally routable IPv6 address, which no
        /// fixed list of private blocks can recognize.
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
                // The try covers one interface, because an adapter going down mid-scan - a
                // VPN, a docker bridge, Wi-Fi roaming - must not end the scan of the rest,
                // which would refuse a peer meant to be admitted.
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

        private static bool MatchesPrefix(byte[] address, byte[] prefix, int bits)
        {
            // A zero-length prefix matches every address, so an interface reporting one
            // would admit every peer.
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

        public static List<string> GetLocalIPAddresses()
        {
            var addresses = new List<string>();

            try
            {
                foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
                {
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
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                        {
                            continue;
                        }

                        var ip = addr.Address.ToString();

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
                    // The caller handles an empty list.
                }
            }

            return addresses;
        }
    }
}
