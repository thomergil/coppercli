using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using coppercli.Core.Communication;
using coppercli.Core.Controllers;
using coppercli.Core.GCode;
using coppercli.Core.Settings;
using coppercli.Helpers;
using coppercli.WebServer;
using Spectre.Console;
using static coppercli.CliConstants;
using static coppercli.Core.Util.Constants;
using static coppercli.Core.Util.GrblProtocol;

namespace coppercli.Menus
{
    internal static class ConnectionMenu
    {
        public enum ConnectionResult
        {
            Success,
            Timeout,
            PortNotOpened,
            ClientAlreadyConnected,
            SerialPortBusy,
            Error
        }

        private enum ConnType { Serial, Ethernet, Back }
        private enum PortOption { Reconnect, AutoDetect, Port, Manual }
        private enum EthernetOption { Reconnect, AutoDetect, Manual }

        private static readonly MenuDef<ConnType> ConnTypeMenu = new(
            new MenuItem<ConnType>("Serial", 's', ConnType.Serial),
            new MenuItem<ConnType>("Network", 'n', ConnType.Ethernet),
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
                    machine.Disconnect();  // AppState resets IsWorkZeroSet centrally on the disconnect event
                    AnsiConsole.MarkupLine($"[{ColorWarning}]{StatusDisconnected}[/]");
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

                    string[] ports = Array.Empty<string>();
                    AnsiConsole.Status()
                        .Start("Enumerating serial ports...", ctx =>
                        {
                            ports = GetAvailablePorts();
                        });

                    if (ports.Length == 0)
                    {
                        AnsiConsole.MarkupLine($"[{ColorError}]No serial ports found![/]");
                        return;
                    }

                    var portMenu = new MenuDef<PortOption>();

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
                            AnsiConsole.MarkupLine($"[{ColorError}]No GRBL device found on any port.[/]");
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

                    settings.SerialPortBaud = MenuHelpers.AskBaudRate(settings.SerialPortBaud);
                }
                else if (connChoice.Option == ConnType.Ethernet)
                {
                    settings.ConnectionType = ConnectionType.Ethernet;

                    var ethMenu = new MenuDef<EthernetOption>();

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
                        var localIPs = NetworkHelpers.GetLocalIPAddresses();
                        if (localIPs.Count > 0)
                        {
                            var sampleIP = localIPs[0].Split('.');
                            AnsiConsole.MarkupLine($"[{ColorDim}]Your IP: {localIPs[0]}[/]");
                            AnsiConsole.MarkupLine($"[{ColorDim}]  /24 = {sampleIP[0]}.{sampleIP[1]}.{sampleIP[2]}.* (254 hosts)[/]");
                            AnsiConsole.MarkupLine($"[{ColorDim}]  /16 = {sampleIP[0]}.{sampleIP[1]}.*.* (65534 hosts)[/]");
                        }

                        int mask = MenuHelpers.Ask("Subnet mask:", NetworkScanDefaultMask);
                        mask = Math.Clamp(mask, NetworkScanMinMask, NetworkScanDefaultMask);
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

                    settings.EthernetIP = MenuHelpers.Ask("IP address:", settings.EthernetIP);
                    settings.EthernetPort = MenuHelpers.Ask("Port:", settings.EthernetPort);
                }

                ConnectWithCurrentSettings();
            }
        }

        public static void QuickConnect()
        {
            var settings = AppState.Settings;
            if (settings.ConnectionType == ConnectionType.Serial)
            {
                AnsiConsole.MarkupLine($"[{ColorPrompt}]Connecting to {settings.SerialPortName} @ {settings.SerialPortBaud}...[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[{ColorPrompt}]Connecting to {settings.EthernetIP}:{settings.EthernetPort}...[/]");
            }
            ConnectWithCurrentSettings();
        }

        private static void ConnectWithCurrentSettings()
        {
            var machine = AppState.Machine;
            var settings = AppState.Settings;

            try
            {
                var (result, message) = TryConnectWithStatus("Connecting...");

                switch (result)
                {
                    case ConnectionResult.Success when message != null:
                        HandleSuccessfulConnection(message);
                        break;
                    case ConnectionResult.Success:
                        // Success with no message: the port opened but GRBL never answered.
                        AnsiConsole.MarkupLine($"[{ColorWarning}]Warning: Port opened but no GRBL response received.[/]");
                        AnsiConsole.MarkupLine($"[{ColorWarning}]Check that the correct port is selected and GRBL is running.[/]");
                        machine.Disconnect();
                        break;
                    case ConnectionResult.Timeout:
                        AnsiConsole.MarkupLine($"[{ColorError}]Connection timed out.[/]");
                        if (machine.Connected)
                        {
                            machine.Disconnect();
                        }
                        break;
                    case ConnectionResult.ClientAlreadyConnected:
                        AnsiConsole.MarkupLine($"[{ColorWarning}]Another TUI client is already connected to the proxy.[/]");
                        AnsiConsole.MarkupLine($"[{ColorDim}]Close the other TUI client first, then try again.[/]");
                        break;
                    case ConnectionResult.SerialPortBusy:
                        AnsiConsole.MarkupLine($"[{ColorWarning}]{ServerHasTheMachine}[/]");
                        if (!MenuHelpers.Confirm(TakeOverFromServerQuestion))
                        {
                            Environment.Exit(0);
                        }
                        string? notTakenOver = TryTakeOverFromServer(settings);
                        if (notTakenOver == null)
                        {
                            Thread.Sleep(TakeoverDelayMs);
                            (result, message) = TryConnectWithStatus("Reconnecting...");
                            if (result == ConnectionResult.Success && message != null)
                            {
                                HandleSuccessfulConnection(message);
                            }
                            else
                            {
                                AnsiConsole.MarkupLine($"[{ColorError}]Reconnection failed.[/]");
                                Environment.Exit(1);
                            }
                        }
                        else
                        {
                            MenuHelpers.ShowError(notTakenOver);
                            Environment.Exit(1);
                        }
                        break;
                    case ConnectionResult.PortNotOpened:
                        if (settings.ConnectionType == ConnectionType.Serial)
                        {
                            AnsiConsole.MarkupLine($"[{ColorError}]Could not open serial port. Is the machine powered on?[/]");
                        }
                        else
                        {
                            AnsiConsole.MarkupLine($"[{ColorError}]Could not connect. Is the machine powered on and network available?[/]");
                        }
                        break;
                    case ConnectionResult.Error:
                        AnsiConsole.MarkupLine(
                $"[{ColorError}]{Markup.Escape(message ?? string.Format(ErrorSomethingFailed, FailedConnecting))}[/]");
                        break;
                }
            }
            catch (Exception ex)
            {
                MenuHelpers.ShowFailureAndWait(CliConstants.FailedConnecting, ex);
            }
            finally
            {
                AppState.SuppressErrors = false;
            }
        }

        private static (ConnectionResult Result, string? Message) TryConnectWithStatus(string statusMessage)
        {
            ConnectionResult result = ConnectionResult.Error;
            string? message = null;

            AppState.SuppressErrors = true;
            AnsiConsole.Status()
                .Start(statusMessage, ctx =>
                {
                    (result, message) = TryConnect(ConnectionTimeoutMs + GrblResponseTimeoutMs);
                });
            AppState.SuppressErrors = false;

            return (result, message);
        }

        private static void HandleSuccessfulConnection(string grblStatus)
        {
            // An alarm is not announced: `EnableAutoStateClear` clears it moments later.
            if (grblStatus != StatusIdle && !grblStatus.StartsWith(StatusAlarm))
            {
                AnsiConsole.MarkupLine($"[{ColorSuccess}]Connected. Machine is {grblStatus}.[/]");
            }
            AppState.Session.LastSuccessfulConnectionType = AppState.Settings.ConnectionType;
            Persistence.SaveSettings();
            Persistence.SaveSession();
        }

        private static bool AutoDetectSerial(string[] ports)
        {
            var machine = AppState.Machine;
            var settings = AppState.Settings;

            AnsiConsole.MarkupLine($"[{ColorPrompt}]Scanning {ports.Length} port(s) at {CommonBaudRates.Length} baud rates...[/]");

            foreach (var baud in CommonBaudRates)
            {
                foreach (var port in ports)
                {
                    AnsiConsole.Markup($"  Trying [{ColorInfo}]{port}[/] @ [{ColorInfo}]{baud}[/]... ");

                    settings.SerialPortName = port;
                    settings.SerialPortBaud = baud;

                    try
                    {
                        var (result, message) = TryConnect(AutoDetectTimeoutMs);

                        if (result == ConnectionResult.Success && message != null)
                        {
                            AnsiConsole.MarkupLine($"[{ColorSuccess}]Found! Status: {message}[/]");
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
                        AnsiConsole.MarkupLine($"[{ColorDim}]{status}[/]");
                    }
                    catch (Exception)
                    {
                        if (machine.Connected)
                        {
                            machine.Disconnect();
                        }
                        AnsiConsole.MarkupLine($"[{ColorDim}]failed[/]");
                    }
                }
            }

            return false;
        }

        private static bool AutoDetectEthernet(int port, int mask)
        {
            var settings = AppState.Settings;
            var localIPs = NetworkHelpers.GetLocalIPAddresses();

            if (localIPs.Count == 0)
            {
                AnsiConsole.MarkupLine($"[{ColorError}]Could not determine local network address.[/]");
                return false;
            }

            int totalHosts = (1 << (32 - mask)) - 2; // Exclude network and broadcast
            AnsiConsole.MarkupLine($"[{ColorPrompt}]Scanning {localIPs.Count} network(s), /{mask} ({totalHosts} hosts each), port {port}...[/]");

            foreach (var localIP in localIPs)
            {
                var parts = localIP.Split('.').Select(int.Parse).ToArray();
                string rangeDesc = mask == 24
                    ? $"{parts[0]}.{parts[1]}.{parts[2]}.x"
                    : $"{parts[0]}.{parts[1]}.x.x/{mask}";
                AnsiConsole.MarkupLine($"  Scanning [{ColorInfo}]{rangeDesc}[/]...");

                var found = ScanNetwork(localIP, mask, port);

                if (found.Count > 0)
                {
                    AnsiConsole.MarkupLine($"  [{ColorSuccess}]Found {found.Count} device(s)[/]");

                    var options = found.Select((ip, i) => $"{i + 1}. {ip}:{port}").ToList();
                    options.Add($"{options.Count + 1}. Back");
                    int choice = MenuHelpers.ShowMenu("Connect to:", options.ToArray());

                    // The Back entry sits one past the last device.
                    if (choice == found.Count)
                    {
                        return false;
                    }

                    string selectedIp = found[choice];
                    settings.EthernetIP = selectedIp;
                    settings.EthernetPort = port;
                    ConnectWithCurrentSettings();

                    if (AppState.Machine.Connected)
                    {
                        return true;
                    }

                    MenuHelpers.WaitEnter();
                    return false;
                }

                AnsiConsole.MarkupLine($"  [{ColorDim}]No devices found[/]");
            }

            return false;
        }

        private static List<string> ScanNetwork(string localIP, int mask, int port)
        {
            var found = new List<string>();
            var lockObj = new object();

            var parts = localIP.Split('.').Select(int.Parse).ToArray();
            uint ipInt = ((uint)parts[0] << 24) | ((uint)parts[1] << 16) | ((uint)parts[2] << 8) | (uint)parts[3];

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

            // Numeric, not lexicographic: 10.0.0.9 sorts before 10.0.0.10.
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

        private static bool IsPortOpen(string host, int port, int timeoutMs)
        {
            try
            {
                using var client = new TcpClient();
                client.SendTimeout = timeoutMs;
                client.ReceiveTimeout = timeoutMs;

                // TcpClient.Connect ignores SendTimeout, so the deadline comes from the token.
                using var cts = new CancellationTokenSource(timeoutMs);
                var task = client.ConnectAsync(host, port);
                task.Wait(cts.Token);

                return client.Connected;
            }
            catch (OperationCanceledException)
            {
            }
            catch (AggregateException)
            {
            }
            catch
            {
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
                            // An unreadable /dev entry is skipped; the other patterns still run.
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
            bool connectionRejected = false;
            bool serialPortBusy = false;
            Exception? error = null;

            void OnLineReceived(string line)
            {
                if (line.StartsWith(ProxyConnectionRejectedPrefix))
                {
                    connectionRejected = true;
                }
                // Only this one means the server has the machine. A proxy that could not open the
                // port says so with ProxySerialPortBusyPrefix, and that is a failed connect.
                else if (line.StartsWith(ProxySerialPortInUsePrefix))
                {
                    serialPortBusy = true;
                }
            }

            machine.LineReceived += OnLineReceived;
            try
            {
                var connectTask = Task.Run(() =>
                {
                    machine.Connect();
                    if (!machine.Connected)
                    {
                        return null;
                    }
                    machine.SoftReset();
                    return MachineWait.WaitForStatusChangeAsync(machine, StatusDisconnected, GrblResponseTimeoutMs).GetAwaiter().GetResult();
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
            }
            finally
            {
                machine.LineReceived -= OnLineReceived;
            }

            if (error != null)
            {
                // The exception's own text names sockets and ports the operator cannot act
                // on, so it goes to the log and the caller shows a sentence.
                Logger.Log("{0} failed: {1}", CliConstants.FailedConnecting, error);
                return (ConnectionResult.Error, null);
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
            if (connectionRejected)
            {
                return (ConnectionResult.ClientAlreadyConnected, null);
            }
            if (serialPortBusy)
            {
                return (ConnectionResult.SerialPortBusy, null);
            }
            return (ConnectionResult.PortNotOpened, null);
        }

        /// <returns>Why the server did not let go of the machine, or null once it did.</returns>
        private static string? TryTakeOverFromServer(MachineSettings settings)
        {
            if (settings.ConnectionType != ConnectionType.Ethernet || string.IsNullOrEmpty(settings.EthernetIP))
            {
                return ServerNotReached;
            }

            // The server publishes the web port one above the proxy port.
            int webPort = settings.EthernetPort + 1;
            var url = $"http://{settings.EthernetIP}:{webPort}{WebConstants.ApiTerminalTakeover}";

            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromMilliseconds(TakeoverApiTimeoutMs);
                var response = client.PostAsync(url, null).Result;
                Logger.Log($"TryTakeOverFromServer: {url} returned {response.StatusCode}");

                if (response.IsSuccessStatusCode)
                {
                    return null;
                }

                // The server's own reason, so the wording is defined once, in WebConstants;
                // ServerNotReached covers a reply without one.
                using var body = JsonDocument.Parse(response.Content.ReadAsStringAsync().Result);
                return body.RootElement.TryGetProperty(WebConstants.JsonFieldError, out var error)
                    ? error.GetString() ?? ServerNotReached
                    : ServerNotReached;
            }
            catch (Exception ex)
            {
                Logger.Log($"TryTakeOverFromServer failed: {ex.Message}");
                return ServerNotReached;
            }
        }

        public static void OfferToHome()
        {
            var machine = AppState.Machine;
            if (!MachineWait.IsResponding(machine))
            {
                return;
            }

            if (!MenuHelpers.ConfirmOrExit(PromptHomeMachine))
            {
                return;
            }

            // GRBL takes $H in Alarm, so a door hold is the only state to clear first.
            if (MachineWait.IsDoor(machine) && !MenuHelpers.WaitForDoorClear(machine, DoorPrompts.ScrollingConsole))
            {
                return;
            }

            // Asleep, GRBL answers nothing but a reset, so $H would wait out its whole timeout.
            if (MachineWait.IsAsleep(machine))
            {
                MenuHelpers.ShowError(ErrorMachineAsleep);
                return;
            }

            // A line rather than a spinner, because homing can stop to ask about the door.
            AnsiConsole.MarkupLine(ControllerConstants.MessageHoming);

            var homing = MachineCommands.HomeAndWait(machine, retryAfterDoorCloses: () =>
            {
                // Keys typed while homing ran are still buffered, and one of them would answer
                // this before it has been read.
                InputHelpers.FlushKeyboard();
                return MenuHelpers.ConfirmOrExit(PromptHomeDoorOpen);
            });

            // A door refusal is returned only once the operator has declined to retry.
            if (!homing.DoorOpen && homing.FailureMessage is { } failure)
            {
                MenuHelpers.ShowError(failure);
            }
        }
    }
}
