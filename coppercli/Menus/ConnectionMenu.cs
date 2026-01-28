// Extracted from Program.cs

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using coppercli.Core.Communication;
using coppercli.Core.GCode;
using coppercli.Core.Settings;
using coppercli.Helpers;
using Spectre.Console;
using static coppercli.CliConstants;
using static coppercli.Core.Util.GrblProtocol;

namespace coppercli.Menus
{
    /// <summary>
    /// Connection menu for connecting/disconnecting from the CNC machine.
    /// </summary>
    internal static class ConnectionMenu
    {
        public enum ConnectionResult
        {
            Success,
            Timeout,
            PortNotOpened,
            Error
        }

        // Menu option types
        private enum ConnType { Serial, Ethernet, Back }
        private enum PortOption { Reconnect, AutoDetect, Port, Manual }
        private enum EthernetOption { Reconnect, AutoDetect, Manual }

        // Connection type menu definition
        private static readonly MenuDef<ConnType> ConnTypeMenu = new(
            new MenuItem<ConnType>("Serial", 's', ConnType.Serial),
            new MenuItem<ConnType>("Network (TCP/IP) [experimental]", 'n', ConnType.Ethernet),
            new MenuItem<ConnType>("Back", 'q', ConnType.Back)
        );

        public static void Show()
        {
            var machine = AppState.Machine;
            var settings = AppState.Settings;

            if (machine.Connected)
            {
                if (MenuHelpers.Confirm("Disconnect from machine?"))
                {
                    machine.Disconnect();
                    AppState.IsWorkZeroSet = false;
                    AnsiConsole.MarkupLine($"[yellow]{StatusDisconnected}[/]");
                }
            }
            else
            {
                var connChoice = MenuHelpers.ShowMenu("Connection type:", ConnTypeMenu);

                if (connChoice.Option == ConnType.Back)
                {
                    return;
                }

                if (connChoice.Option == ConnType.Serial)
                {
                    settings.ConnectionType = ConnectionType.Serial;

                    // List available ports
                    string[] ports = Array.Empty<string>();
                    AnsiConsole.Status()
                        .Start("Enumerating serial ports...", ctx =>
                        {
                            ports = GetAvailablePorts();
                        });

                    if (ports.Length == 0)
                    {
                        AnsiConsole.MarkupLine("[red]No serial ports found![/]");
                        return;
                    }

                    // Build dynamic port menu
                    var portMenu = new MenuDef<PortOption>();

                    // Add Reconnect option if we have saved settings
                    if (!string.IsNullOrEmpty(settings.SerialPortName))
                    {
                        portMenu.Add(new MenuItem<PortOption>(
                            $"Reconnect ({settings.SerialPortName} @ {settings.SerialPortBaud})", 'r', PortOption.Reconnect));
                    }

                    portMenu.Add(new MenuItem<PortOption>("Auto-detect (scan all ports)", 'a', PortOption.AutoDetect));
                    for (int i = 0; i < ports.Length; i++)
                    {
                        portMenu.Add(new MenuItem<PortOption>(ports[i], (char)('0' + ((i + 2) % 10)), PortOption.Port, i));
                    }
                    portMenu.Add(new MenuItem<PortOption>("Enter manually", 'm', PortOption.Manual));

                    var selected = MenuHelpers.ShowMenu("Select serial port:", portMenu);

                    if (selected.Option == PortOption.Reconnect)
                    {
                        ConnectWithCurrentSettings();
                        return;
                    }

                    if (selected.Option == PortOption.AutoDetect)
                    {
                        if (AutoDetectSerial(ports))
                        {
                            Persistence.SaveSettings();
                        }
                        else
                        {
                            AnsiConsole.MarkupLine("[red]No GRBL device found on any port.[/]");
                        }
                        return;
                    }

                    string selectedPort;
                    if (selected.Option == PortOption.Manual)
                    {
                        selectedPort = MenuHelpers.Ask<string>("Enter port name:", settings.SerialPortName);
                    }
                    else
                    {
                        selectedPort = ports[selected.Data];
                    }

                    settings.SerialPortName = selectedPort;

                    var baudOptions = CommonBaudRates.Select((b, i) => $"{i + 1}. {b}").ToArray();
                    int baudChoice = MenuHelpers.ShowMenu("Select baud rate:", baudOptions);
                    settings.SerialPortBaud = CommonBaudRates[baudChoice];
                }
                else if (connChoice.Option == ConnType.Ethernet)
                {
                    settings.ConnectionType = ConnectionType.Ethernet;

                    // Build dynamic Ethernet menu
                    var ethMenu = new MenuDef<EthernetOption>();

                    // Add Reconnect option if we have saved settings
                    if (!string.IsNullOrEmpty(settings.EthernetIP))
                    {
                        ethMenu.Add(new MenuItem<EthernetOption>(
                            $"Reconnect ({settings.EthernetIP}:{settings.EthernetPort})", 'r', EthernetOption.Reconnect));
                    }

                    ethMenu.Add(new MenuItem<EthernetOption>("Auto-detect (scan local network)", 'a', EthernetOption.AutoDetect));
                    ethMenu.Add(new MenuItem<EthernetOption>("Enter manually", 'm', EthernetOption.Manual));

                    var selected = MenuHelpers.ShowMenu("Network connection:", ethMenu);

                    if (selected.Option == EthernetOption.Reconnect)
                    {
                        ConnectWithCurrentSettings();
                        return;
                    }

                    if (selected.Option == EthernetOption.AutoDetect)
                    {
                        // Show local IPs to help user understand the scan range
                        var localIPs = GetLocalIPAddresses();
                        if (localIPs.Count > 0)
                        {
                            var sampleIP = localIPs[0].Split('.');
                            AnsiConsole.MarkupLine($"[dim]Your IP: {localIPs[0]}[/]");
                            AnsiConsole.MarkupLine($"[dim]  /24 = {sampleIP[0]}.{sampleIP[1]}.{sampleIP[2]}.* (254 hosts)[/]");
                            AnsiConsole.MarkupLine($"[dim]  /16 = {sampleIP[0]}.{sampleIP[1]}.*.* (65534 hosts)[/]");
                        }

                        int mask = MenuHelpers.Ask("Subnet mask:", 24);
                        mask = Math.Clamp(mask, 16, 24);
                        int scanPort = MenuHelpers.Ask("Port to scan for:", ProxyDefaultPort);

                        if (AutoDetectEthernet(scanPort, mask))
                        {
                            Persistence.SaveSettings();
                        }
                        else
                        {
                            MenuHelpers.ShowError($"No device found on port {scanPort}.");
                        }
                        return;
                    }

                    // Manual entry
                    settings.EthernetIP = MenuHelpers.Ask("IP address:", settings.EthernetIP);
                    settings.EthernetPort = MenuHelpers.Ask("Port:", settings.EthernetPort);
                }

                ConnectWithCurrentSettings();
            }
        }

        /// <summary>
        /// Quick connect using saved settings (called on startup).
        /// </summary>
        public static void QuickConnect()
        {
            var settings = AppState.Settings;
            if (settings.ConnectionType == ConnectionType.Serial)
            {
                AnsiConsole.MarkupLine($"[blue]Connecting to {settings.SerialPortName} @ {settings.SerialPortBaud}...[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[blue]Connecting to {settings.EthernetIP}:{settings.EthernetPort}...[/]");
            }
            ConnectWithCurrentSettings();
        }

        /// <summary>
        /// Attempts to connect using the current settings and shows post-connection offers.
        /// </summary>
        private static void ConnectWithCurrentSettings()
        {
            var machine = AppState.Machine;

            try
            {
                ConnectionResult result = ConnectionResult.Error;
                string? message = null;

                // Suppress errors during connection (initial status parsing may produce transient errors)
                AppState.SuppressErrors = true;
                AnsiConsole.Status()
                    .Start("Connecting...", ctx =>
                    {
                        (result, message) = TryConnect(ConnectionTimeoutMs + GrblResponseTimeoutMs);
                    });
                AppState.SuppressErrors = false;

                switch (result)
                {
                    case ConnectionResult.Success when message != null:
                        // Don't announce Alarm state - it will be cleared silently
                        if (message != StatusIdle && !message.StartsWith(StatusAlarm))
                        {
                            AnsiConsole.MarkupLine($"[green]Connected! GRBL status: {message}[/]");
                        }
                        // Save connection settings and remember this as the last successful connection type
                        AppState.Session.LastSuccessfulConnectionType = AppState.Settings.ConnectionType;
                        Persistence.SaveSettings();
                        Persistence.SaveSession();
                        break;
                    case ConnectionResult.Success:
                        AnsiConsole.MarkupLine("[yellow]Warning: Port opened but no GRBL response received.[/]");
                        AnsiConsole.MarkupLine("[yellow]Check that the correct port is selected and GRBL is running.[/]");
                        if (MenuHelpers.Confirm("Disconnect?"))
                        {
                            machine.Disconnect();
                        }
                        break;
                    case ConnectionResult.Timeout:
                        AnsiConsole.MarkupLine("[red]Connection timed out.[/]");
                        if (machine.Connected)
                        {
                            machine.Disconnect();
                        }
                        break;
                    case ConnectionResult.PortNotOpened:
                        AnsiConsole.MarkupLine("[red]Could not open serial port. Is the machine powered on?[/]");
                        break;
                    case ConnectionResult.Error:
                        AnsiConsole.MarkupLine($"[red]Connection failed: {Markup.Escape(message ?? "Unknown error")}[/]");
                        break;
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Connection failed: {Markup.Escape(ex.Message)}[/]");
            }
            finally
            {
                AppState.SuppressErrors = false;
            }
        }

        private static bool AutoDetectSerial(string[] ports)
        {
            var machine = AppState.Machine;
            var settings = AppState.Settings;

            AnsiConsole.MarkupLine($"[blue]Scanning {ports.Length} port(s) at {CommonBaudRates.Length} baud rates...[/]");

            foreach (var baud in CommonBaudRates)
            {
                foreach (var port in ports)
                {
                    AnsiConsole.Markup($"  Trying [cyan]{port}[/] @ [cyan]{baud}[/]... ");

                    settings.SerialPortName = port;
                    settings.SerialPortBaud = baud;

                    try
                    {
                        var (result, message) = TryConnect(AutoDetectTimeoutMs);

                        if (result == ConnectionResult.Success && message != null)
                        {
                            AnsiConsole.MarkupLine($"[green]Found! Status: {message}[/]");
                            return true;
                        }

                        if (machine.Connected)
                        {
                            machine.Disconnect();
                        }

                        var status = result switch
                        {
                            ConnectionResult.Timeout => "timeout",
                            ConnectionResult.PortNotOpened => "not available",
                            ConnectionResult.Error => "failed",
                            _ => "no response"
                        };
                        AnsiConsole.MarkupLine($"[dim]{status}[/]");
                    }
                    catch (Exception)
                    {
                        if (machine.Connected)
                        {
                            machine.Disconnect();
                        }
                        AnsiConsole.MarkupLine("[dim]failed[/]");
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Scans the local network for devices with the proxy port open.
        /// </summary>
        private static bool AutoDetectEthernet(int port, int mask)
        {
            var settings = AppState.Settings;
            var localIPs = GetLocalIPAddresses();

            if (localIPs.Count == 0)
            {
                AnsiConsole.MarkupLine("[red]Could not determine local network address.[/]");
                return false;
            }

            int totalHosts = (1 << (32 - mask)) - 2; // Exclude network and broadcast
            AnsiConsole.MarkupLine($"[blue]Scanning {localIPs.Count} network(s), /{mask} ({totalHosts} hosts each), port {port}...[/]");

            foreach (var localIP in localIPs)
            {
                var parts = localIP.Split('.').Select(int.Parse).ToArray();
                string rangeDesc = mask == 24
                    ? $"{parts[0]}.{parts[1]}.{parts[2]}.x"
                    : $"{parts[0]}.{parts[1]}.x.x/{mask}";
                AnsiConsole.MarkupLine($"  Scanning [cyan]{rangeDesc}[/]...");

                var found = ScanNetwork(localIP, mask, port);

                if (found.Count > 0)
                {
                    AnsiConsole.MarkupLine($"  [green]Found {found.Count} device(s)[/]");

                    // Build menu with devices and Back option
                    var options = found.Select((ip, i) => $"{i + 1}. {ip}:{port}").ToList();
                    options.Add($"{options.Count + 1}. Back");
                    int choice = MenuHelpers.ShowMenu("Connect to:", options.ToArray());

                    // Back selected
                    if (choice == found.Count)
                    {
                        return false;
                    }

                    string selectedIp = found[choice];
                    settings.EthernetIP = selectedIp;
                    settings.EthernetPort = port;
                    ConnectWithCurrentSettings();

                    // Only return true if connection succeeded
                    if (AppState.Machine.Connected)
                    {
                        return true;
                    }

                    // Connection failed - wait for keypress so user can see error
                    Console.ReadKey(true);
                    return false;
                }

                AnsiConsole.MarkupLine("  [dim]No devices found[/]");
            }

            return false;
        }

        /// <summary>
        /// Gets all local IPv4 addresses.
        /// </summary>
        private static List<string> GetLocalIPAddresses()
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
                        if (!addresses.Contains(ip))
                        {
                            addresses.Add(ip);
                        }
                    }
                }
            }
            catch
            {
                // Ignore errors enumerating interfaces
            }

            return addresses;
        }

        /// <summary>
        /// Scans a network range for hosts with the specified port open.
        /// </summary>
        private static List<string> ScanNetwork(string localIP, int mask, int port)
        {
            var found = new List<string>();
            var lockObj = new object();

            // Convert IP to 32-bit integer
            var parts = localIP.Split('.').Select(int.Parse).ToArray();
            uint ipInt = ((uint)parts[0] << 24) | ((uint)parts[1] << 16) | ((uint)parts[2] << 8) | (uint)parts[3];

            // Calculate network address and host count
            uint maskBits = 0xFFFFFFFF << (32 - mask);
            uint networkAddr = ipInt & maskBits;
            int hostCount = (1 << (32 - mask)) - 2; // Exclude network and broadcast

            Parallel.For(1, hostCount + 1, new ParallelOptions { MaxDegreeOfParallelism = NetworkScanParallelism }, i =>
            {
                uint hostAddr = networkAddr + (uint)i;
                string ip = $"{(hostAddr >> 24) & 0xFF}.{(hostAddr >> 16) & 0xFF}.{(hostAddr >> 8) & 0xFF}.{hostAddr & 0xFF}";

                if (IsPortOpen(ip, port, NetworkScanTimeoutMs))
                {
                    lock (lockObj)
                    {
                        found.Add(ip);
                    }
                }
            });

            // Sort by IP address numerically
            found.Sort((a, b) =>
            {
                var aParts = a.Split('.').Select(int.Parse).ToArray();
                var bParts = b.Split('.').Select(int.Parse).ToArray();
                for (int i = 0; i < 4; i++)
                {
                    if (aParts[i] != bParts[i])
                    {
                        return aParts[i].CompareTo(bParts[i]);
                    }
                }
                return 0;
            });

            return found;
        }

        /// <summary>
        /// Checks if a TCP port is open on the specified host.
        /// </summary>
        private static bool IsPortOpen(string host, int port, int timeoutMs)
        {
            try
            {
                using var client = new TcpClient();
                // Set send/receive timeouts
                client.SendTimeout = timeoutMs;
                client.ReceiveTimeout = timeoutMs;

                // Use ConnectAsync with cancellation for reliable timeout
                using var cts = new CancellationTokenSource(timeoutMs);
                var task = client.ConnectAsync(host, port);
                task.Wait(cts.Token);

                return client.Connected;
            }
            catch (OperationCanceledException)
            {
                // Timeout
            }
            catch (AggregateException)
            {
                // Connection refused or other error
            }
            catch
            {
                // Other errors
            }
            return false;
        }

        public static string[] GetAvailablePorts()
        {
            var ports = new List<string>();

            if (OperatingSystem.IsWindows())
            {
                ports.AddRange(System.IO.Ports.SerialPort.GetPortNames());
            }
            else
            {
                if (Directory.Exists("/dev"))
                {
                    foreach (var pattern in UnixSerialPortPatterns)
                    {
                        try
                        {
                            ports.AddRange(Directory.GetFiles("/dev", pattern));
                        }
                        catch
                        {
                            // Ignore directory access errors - continue with other patterns
                        }
                    }
                }
            }

            return ports.ToArray();
        }

        public static (ConnectionResult Result, string? Message) TryConnect(int timeoutMs)
        {
            var machine = AppState.Machine;
            string? grblStatus = null;
            bool timedOut = false;
            Exception? error = null;

            var connectTask = Task.Run(() =>
            {
                machine.Connect();
                if (!machine.Connected)
                {
                    return null;
                }
                machine.SoftReset();
                return StatusHelpers.WaitForGrblResponse(machine, GrblResponseTimeoutMs);
            });

            if (connectTask.Wait(timeoutMs))
            {
                try
                {
                    grblStatus = connectTask.Result;
                }
                catch (AggregateException ae)
                {
                    error = ae.InnerException;
                }
            }
            else
            {
                timedOut = true;
                connectTask.ContinueWith(t =>
                {
                    var _ = t.Exception;
                }, TaskContinuationOptions.OnlyOnFaulted);
            }

            if (error != null)
            {
                return (ConnectionResult.Error, error.Message);
            }
            if (timedOut)
            {
                return (ConnectionResult.Timeout, null);
            }
            if (grblStatus != null)
            {
                return (ConnectionResult.Success, grblStatus);
            }
            if (machine.Connected)
            {
                return (ConnectionResult.Success, null);
            }
            return (ConnectionResult.PortNotOpened, null);
        }

        /// <summary>
        /// Offers to home the machine after connecting. Called from Program.cs.
        /// </summary>
        public static void OfferToHome()
        {
            var machine = AppState.Machine;
            if (!machine.Connected)
            {
                return;
            }

            // Offer to home
            var result = MenuHelpers.ConfirmOrQuit("Home machine?", false);
            if (result == null)
            {
                Environment.Exit(0);
            }
            if (result != true)
            {
                return;
            }

            // Clear any alarm/door state before homing
            while (StatusHelpers.IsProblematicState(machine))
            {
                if (StatusHelpers.IsDoor(machine))
                {
                    AnsiConsole.MarkupLine("[yellow]Door is open. Close the door and press Enter.[/]");
                    Console.ReadLine();
                    MachineCommands.ClearDoorState(machine);
                }
                else if (StatusHelpers.IsAlarm(machine))
                {
                    // Silently clear alarm state
                    MachineCommands.Unlock(machine);
                }
                Thread.Sleep(CommandDelayMs);
            }

            MachineCommands.Home(machine);

            AnsiConsole.Status()
                .Start("Homing...", ctx =>
                {
                    StatusHelpers.WaitForIdle(machine, HomingTimeoutMs);
                });
        }
    }
}
