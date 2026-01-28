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
