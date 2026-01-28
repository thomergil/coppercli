using System.Net.NetworkInformation;
using System.Net.Sockets;
using coppercli.Core.Communication;
using coppercli.Helpers;
using Spectre.Console;
using static coppercli.CliConstants;
using static coppercli.Helpers.DisplayHelpers;

namespace coppercli.Menus
{
    /// <summary>
    /// Proxy mode menu - allows coppercli to act as a serial-to-TCP bridge.
    /// </summary>
    internal static class ProxyMenu
    {
        private enum PortOption { UseSaved, Port, Manual, Back }

        // Message buffer for activity log
        private const int MaxMessages = 5;

        public static void Show()
        {
            var settings = AppState.Settings;
            string selectedPort;
            int selectedBaud;

            // If connected, offer to disconnect and use current settings
            if (AppState.Machine.Connected)
            {
                var currentPort = settings.SerialPortName;
                var currentBaud = settings.SerialPortBaud;

                AnsiConsole.MarkupLine($"[{ColorWarning}]Currently connected to {currentPort} @ {currentBaud}[/]");
                AnsiConsole.MarkupLine($"[{ColorDim}]Proxy will disconnect and take over the serial port.[/]");
                AnsiConsole.WriteLine();

                if (!MenuHelpers.Confirm("Disconnect and start proxy with current settings?"))
                {
                    return;
                }

                // Disconnect
                AppState.Machine.Disconnect();
                AppState.IsWorkZeroSet = false;

                selectedPort = currentPort;
                selectedBaud = currentBaud;
            }
            else
            {
                // Not connected - let user select port and baud

                // Get available serial ports
                string[] ports = Array.Empty<string>();
                AnsiConsole.Status()
                    .Start("Enumerating serial ports...", ctx =>
                    {
                        ports = ConnectionMenu.GetAvailablePorts();
                    });

                if (ports.Length == 0)
                {
                    MenuHelpers.ShowError("No serial ports found!");
                    return;
                }

                // Select serial port
                var portMenu = new MenuDef<PortOption>();

                // If we have saved settings, offer to use them
                if (!string.IsNullOrEmpty(settings.SerialPortName))
                {
                    portMenu.Add(new MenuItem<PortOption>(
                        $"Use saved ({settings.SerialPortName} @ {settings.SerialPortBaud})", 'u', PortOption.UseSaved));
                }

                for (int i = 0; i < ports.Length; i++)
                {
                    portMenu.Add(new MenuItem<PortOption>(ports[i], (char)('0' + ((i + 1) % 10)), PortOption.Port, i));
                }
                portMenu.Add(new MenuItem<PortOption>("Enter manually", 'm', PortOption.Manual));
                portMenu.Add(new MenuItem<PortOption>("Back", 'q', PortOption.Back));

                var portChoice = MenuHelpers.ShowMenu("Select serial port:", portMenu);

                if (portChoice.Option == PortOption.Back)
                {
                    return;
                }

                if (portChoice.Option == PortOption.UseSaved)
                {
                    selectedPort = settings.SerialPortName;
                    selectedBaud = settings.SerialPortBaud;
                }
                else
                {
                    if (portChoice.Option == PortOption.Manual)
                    {
                        selectedPort = MenuHelpers.Ask<string>("Enter port name:");
                    }
                    else
                    {
                        selectedPort = ports[portChoice.Data];
                    }

                    // Select baud rate
                    var baudOptions = CommonBaudRates.Select((b, i) => $"{i + 1}. {b}").ToArray();
                    int baudChoice = MenuHelpers.ShowMenu("Select baud rate:", baudOptions);
                    selectedBaud = CommonBaudRates[baudChoice];
                }
            }

            // Select TCP port
            int tcpPort = MenuHelpers.Ask("TCP port:", ProxyDefaultPort);

            // Start proxy
            using var proxy = new SerialProxy();

            // Subscribe to events for logging
            var messages = new List<string>();

            proxy.Info += msg =>
            {
                lock (messages)
                {
                    messages.Add($"{AnsiDim}{DateTime.Now:HH:mm:ss}{AnsiReset} {msg}");
                    while (messages.Count > MaxMessages)
                    {
                        messages.RemoveAt(0);
                    }
                }
            };

            proxy.Error += msg =>
            {
                lock (messages)
                {
                    messages.Add($"{AnsiDim}{DateTime.Now:HH:mm:ss}{AnsiReset} {AnsiError}{msg}{AnsiReset}");
                    while (messages.Count > MaxMessages)
                    {
                        messages.RemoveAt(0);
                    }
                }
            };

            try
            {
                proxy.Start(selectedPort, selectedBaud, tcpPort);
            }
            catch (Exception ex)
            {
                MenuHelpers.ShowError($"Failed to start proxy: {ex.Message}");
                return;
            }

            // Enter monitoring loop
            MonitorProxy(proxy, messages);
        }

        /// <summary>
        /// Runs proxy with interactive TUI display (for --proxy command line flag).
        /// </summary>
        public static void RunInteractive(string serialPort, int baudRate, int tcpPort)
        {
            using var proxy = new SerialProxy();

            // Subscribe to events for logging
            var messages = new List<string>();

            proxy.Info += msg =>
            {
                lock (messages)
                {
                    messages.Add($"{AnsiDim}{DateTime.Now:HH:mm:ss}{AnsiReset} {msg}");
                    while (messages.Count > MaxMessages)
                    {
                        messages.RemoveAt(0);
                    }
                }
            };

            proxy.Error += msg =>
            {
                lock (messages)
                {
                    messages.Add($"{AnsiDim}{DateTime.Now:HH:mm:ss}{AnsiReset} {AnsiError}{msg}{AnsiReset}");
                    while (messages.Count > MaxMessages)
                    {
                        messages.RemoveAt(0);
                    }
                }
            };

            try
            {
                proxy.Start(serialPort, baudRate, tcpPort);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to start proxy: {ex.Message}");
                Environment.Exit(1);
            }

            // Enter monitoring loop
            MonitorProxy(proxy, messages);
        }

        private static void MonitorProxy(SerialProxy proxy, List<string> messages)
        {
            Console.Clear();
            Console.CursorVisible = false;

            try
            {
                while (true)
                {
                    DrawProxyStatus(proxy, messages);

                    // Check for exit key
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(true);
                        if (InputHelpers.IsExitKey(key))
                        {
                            break;
                        }
                    }

                    Thread.Sleep(ProxyStatusUpdateIntervalMs);
                }
            }
            finally
            {
                proxy.Stop();
                Console.CursorVisible = true;
            }
        }

        private static void DrawProxyStatus(SerialProxy proxy, List<string> messages)
        {
            var (winWidth, _) = GetSafeWindowSize();

            // Reset cursor to top-left for flicker-free redraw
            Console.SetCursorPosition(0, 0);

            // Header
            string header = $"{AnsiPrompt}Proxy{AnsiReset}";
            int headerPad = Math.Max(0, (winWidth - 10) / 2);
            WriteLineTruncated(new string(' ', headerPad) + header, winWidth);
            WriteLineTruncated("", winWidth);

            // Connection info
            WriteLineTruncated($"  Serial Port:    {AnsiInfo}{proxy.SerialPortName}{AnsiReset}", winWidth);
            WriteLineTruncated($"  Baud Rate:      {AnsiInfo}{proxy.BaudRate}{AnsiReset}", winWidth);

            // Show local IP addresses for easy client connection
            var localIps = GetLocalIPAddresses();
            if (localIps.Count > 0)
            {
                var ipList = string.Join(", ", localIps.Select(ip => $"{AnsiBoldGreen}{ip}:{proxy.TcpPort}{AnsiReset}"));
                WriteLineTruncated($"  Connect to:     {ipList}", winWidth);
            }
            else
            {
                WriteLineTruncated($"  TCP Port:       {AnsiInfo}{proxy.TcpPort}{AnsiReset}", winWidth);
            }

            WriteLineTruncated("", winWidth);

            // Client status
            if (proxy.HasClient)
            {
                WriteLineTruncated($"  Client:         {AnsiSuccess}{proxy.ClientAddress}{AnsiReset}", winWidth);
                if (proxy.ClientConnectedTime.HasValue)
                {
                    var duration = DateTime.Now - proxy.ClientConnectedTime.Value;
                    WriteLineTruncated($"  Connected:      {AnsiSuccess}{FormatDuration(duration)}{AnsiReset}", winWidth);
                }
            }
            else
            {
                WriteLineTruncated($"  Client:         {AnsiWarning}{ProxyNoClientStatus}{AnsiReset}", winWidth);
                WriteLineTruncated("", winWidth);
            }

            // Traffic statistics
            WriteLineTruncated($"  Bytes to client:   {AnsiDim}{proxy.BytesToClient,10:N0}{AnsiReset}", winWidth);
            WriteLineTruncated($"  Bytes from client: {AnsiDim}{proxy.BytesFromClient,10:N0}{AnsiReset}", winWidth);
            WriteLineTruncated("", winWidth);

            // Recent messages
            WriteLineTruncated($"  {AnsiInfo}Recent activity:{AnsiReset}", winWidth);
            lock (messages)
            {
                for (int i = 0; i < MaxMessages; i++)
                {
                    if (i < messages.Count)
                    {
                        WriteLineTruncated($"    {messages[i]}", winWidth);
                    }
                    else
                    {
                        WriteLineTruncated("", winWidth);
                    }
                }
            }
            WriteLineTruncated("", winWidth);

            // Instructions
            WriteLineTruncated($"  {AnsiDim}Press {AnsiReset}{AnsiInfo}q{AnsiReset}{AnsiDim} or {AnsiReset}{AnsiInfo}Escape{AnsiReset}{AnsiDim} to stop proxy{AnsiReset}", winWidth);
        }

        /// <summary>
        /// Runs proxy mode headless (for --proxy command line flag).
        /// Uses provided settings without prompts.
        /// </summary>
        public static void RunHeadless(string serialPort, int baudRate, int tcpPort)
        {
            using var proxy = new SerialProxy();

            // Subscribe to events for console output
            proxy.Info += msg => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
            proxy.Error += msg => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ERROR: {msg}");
            proxy.ClientConnected += () => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Client connected: {proxy.ClientAddress}");
            proxy.ClientDisconnected += () => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Client disconnected");

            try
            {
                proxy.Start(serialPort, baudRate, tcpPort);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to start proxy: {ex.Message}");
                Environment.Exit(1);
            }

            // Show connection info
            var localIps = GetLocalIPAddresses();
            if (localIps.Count > 0)
            {
                Console.WriteLine($"Connect to: {string.Join(" or ", localIps.Select(ip => $"{ip}:{tcpPort}"))}");
            }
            Console.WriteLine("Press Ctrl+C to stop");
            Console.WriteLine();

            // Handle Ctrl+C gracefully
            var exitEvent = new ManualResetEvent(false);
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                exitEvent.Set();
            };

            // Wait for exit signal
            exitEvent.WaitOne();

            Console.WriteLine();
            Console.WriteLine("Stopping proxy...");
            proxy.Stop();
        }

        /// <summary>
        /// Gets the local IPv4 addresses for display to the user.
        /// Filters out loopback and link-local addresses.
        /// </summary>
        private static List<string> GetLocalIPAddresses()
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

                        addresses.Add(ip);
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
                            if (!ip.StartsWith("127.") && !ip.StartsWith("169.254."))
                            {
                                addresses.Add(ip);
                            }
                        }
                    }
                }
                catch
                {
                    // Give up - will show just the port
                }
            }

            return addresses;
        }
    }
}
