using coppercli.Core.Communication;
using coppercli.Helpers;
using coppercli.WebServer;
using Spectre.Console;
using static coppercli.CliConstants;
using static coppercli.Helpers.DisplayHelpers;
using static coppercli.WebServer.WebConstants;

namespace coppercli.Menus
{
    /// <summary>
    /// Starts the serial proxy and the web server on one serial port, so `Machine` must
    /// disconnect before either can open it.
    /// </summary>
    internal static class ServerMenu
    {
        private enum PortOption { UseSaved, Port, Manual, Back }

        private const int MaxMessages = 5;

        public static void Show()
        {
            var settings = AppState.Settings;
            string selectedPort;
            int selectedBaud;

            if (AppState.Machine.Connected)
            {
                var currentPort = settings.SerialPortName;
                var currentBaud = settings.SerialPortBaud;

                AnsiConsole.MarkupLine($"[{ColorWarning}]Currently connected to {currentPort} @ {currentBaud}[/]");
                AnsiConsole.MarkupLine($"[{ColorDim}]Server will disconnect and take over the serial port.[/]");
                AnsiConsole.WriteLine();

                if (!MenuHelpers.Confirm("Disconnect and start server with current settings?"))
                {
                    return;
                }

                // The server reconnects to the same machine, which keeps its work offset, so
                // a zero that was known before the disconnect is still good after it.
                var preserveWorkZero = AppState.IsWorkZeroSet;
                AppState.Machine.Disconnect();
                AppState.SetWorkZeroTrusted(preserveWorkZero);
                Logger.Log($"ServerMenu: Preserved IsWorkZeroSet={preserveWorkZero} across server transition");

                selectedPort = currentPort;
                selectedBaud = currentBaud;
            }
            else
            {
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

                var portMenu = new MenuDef<PortOption>();

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
                    // The baud menu's last entry keeps what is saved, so that is the start.
                    selectedBaud = settings.SerialPortBaud;

                    if (portChoice.Option == PortOption.Manual)
                    {
                        selectedPort = MenuHelpers.Ask<string>("Enter port name:");
                    }
                    else
                    {
                        selectedPort = ports[portChoice.Data];
                    }

                    selectedBaud = MenuHelpers.AskBaudRate(selectedBaud);
                }
            }

            int proxyPort = MenuHelpers.Ask("Proxy port (for TUI clients):", ProxyDefaultPort);
            int webPort = MenuHelpers.Ask("Web port (for browser):", WebDefaultPort);

            RunServer(selectedPort, selectedBaud, proxyPort, webPort, exitToMenu: true);
        }

        /// <summary>
        /// The `--server` flag and this menu both call this, so the proxy and the web server
        /// start the same way from either route.
        /// </summary>
        /// <param name="exitToMenu">True returns to the menu on exit; false exits the process.</param>
        public static void RunServer(string serialPort, int baudRate, int proxyPort, int webPort, bool exitToMenu)
        {
            Logger.Log("ServerMenu.RunServer: starting proxy={0}, web={1}", proxyPort, webPort);
            var messages = new List<string>();

            Logger.Log("ServerMenu.RunServer: creating SerialProxy");
            var proxy = new SerialProxy();
            SubscribeProxyEvents(proxy, messages);

            // The proxy must not open the serial port while `Machine` or a web client holds it.
            proxy.IsSerialPortInUse = () => AppState.Machine.Connected || CncWebServer.HasWebClient;

            CncWebServer.ForceDisconnectProxyClient = () => proxy.ForceDisconnectClient();

            CncWebServer.HasProxyClient = () => proxy.HasClient;

            try
            {
                Logger.Log("ServerMenu.RunServer: starting proxy on port {0}", proxyPort);
                proxy.Start(serialPort, baudRate, proxyPort);
                Logger.Log("ServerMenu.RunServer: proxy started");
            }
            catch (Exception ex)
            {
                // Only the menu waits for a keypress. With --server nobody is at the terminal,
                // so waiting would hang the service instead of exiting 1.
                if (exitToMenu)
                {
                    MenuHelpers.ShowFailureAndWait(CliConstants.FailedStartingTheProxy, ex);
                    return;
                }

                MenuHelpers.ShowFailure(CliConstants.FailedStartingTheProxy, ex);
                Environment.Exit(1);
            }

            Logger.Log("ServerMenu.RunServer: starting web server thread");
            Exception? webServerError = null;
            var webServerStarted = new ManualResetEvent(false);

            var webServerThread = new Thread(() =>
            {
                try
                {
                    CncWebServer.Run(webPort, serialPort, baudRate, webServerStarted);
                }
                catch (Exception ex)
                {
                    webServerError = ex;
                    webServerStarted.Set();
                }
            })
            {
                Name = "WebServer",
                IsBackground = true
            };
            webServerThread.Start();

            Logger.Log("ServerMenu.RunServer: waiting for web server to signal ready");
            webServerStarted.WaitOne(WebServerStartTimeoutMs);
            Logger.Log("ServerMenu.RunServer: web server signaled (error={0})", webServerError != null);
            if (webServerError != null)
            {
                proxy.Stop();

                if (exitToMenu)
                {
                    MenuHelpers.ShowFailureAndWait(
                        CliConstants.FailedStartingTheWebServer, webServerError);
                    return;
                }

                MenuHelpers.ShowFailure(CliConstants.FailedStartingTheWebServer, webServerError);
                Environment.Exit(1);
            }

            var exitRequested = false;
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                exitRequested = true;
            };

            MonitorServer(proxy, proxyPort, webPort, messages, () => exitRequested);

            CncWebServer.Stop();
            webServerThread.Join(2000);
            proxy.Stop();

            if (!exitToMenu)
            {
                Environment.Exit(0);
            }

            if (!AppState.Machine.Connected)
            {
                try
                {
                    AppState.Machine.Connect();
                }
                catch
                {
                    // A failed reconnect is not worth a message; the operator can reconnect
                    // from the menu.
                }
            }
        }

        private static void SubscribeProxyEvents(SerialProxy proxy, List<string> messages)
        {
            proxy.Info += msg =>
            {
                Logger.Log($"Proxy: {msg}");
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
                Logger.Log($"Proxy error: {msg}");
                lock (messages)
                {
                    messages.Add($"{AnsiDim}{DateTime.Now:HH:mm:ss}{AnsiReset} {AnsiError}{msg}{AnsiReset}");
                    while (messages.Count > MaxMessages)
                    {
                        messages.RemoveAt(0);
                    }
                }
            };
        }

        private static void MonitorServer(SerialProxy proxy, int proxyPort, int webPort, List<string> messages, Func<bool> shouldExit)
        {
            Console.Clear();
            Console.CursorVisible = false;

            try
            {
                while (!shouldExit())
                {
                    DrawServerStatus(proxy, proxyPort, webPort, messages);

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
                Console.CursorVisible = true;
            }
        }

        private static void DrawServerStatus(SerialProxy proxy, int proxyPort, int webPort, List<string> messages)
        {
            var (winWidth, _) = GetSafeWindowSize();

            Console.SetCursorPosition(0, 0);

            string header = $"{AnsiPrompt}Server{AnsiReset}";
            int headerPad = Math.Max(0, (winWidth - CalculateDisplayLength(header)) / 2);
            WriteLineTruncated(new string(' ', headerPad) + header, winWidth);
            WriteLineTruncated("", winWidth);

            WriteLineTruncated($"  Serial Port:    {AnsiInfo}{proxy.SerialPortName}{AnsiReset}", winWidth);
            WriteLineTruncated($"  Baud Rate:      {AnsiInfo}{proxy.BaudRate}{AnsiReset}", winWidth);
            WriteLineTruncated("", winWidth);

            var localIps = NetworkHelpers.GetLocalIPAddresses();
            if (localIps.Count > 0)
            {
                var ip = localIps[0];
                WriteLineTruncated($"  TUI  (:{proxyPort}):  {AnsiSuccessBold}{ip}:{proxyPort}{AnsiReset}", winWidth);
                WriteLineTruncated($"  Web  (:{webPort}):  {AnsiSuccessBold}http://{ip}:{webPort}{AnsiReset}", winWidth);
            }
            else
            {
                WriteLineTruncated($"  TUI Port:       {AnsiInfo}{proxyPort}{AnsiReset}", winWidth);
                WriteLineTruncated($"  Web Port:       {AnsiInfo}{webPort}{AnsiReset}", winWidth);
            }
            WriteLineTruncated("", winWidth);

            // One client at a time: the proxy and the web server share the one serial port.
            if (proxy.HasClient)
            {
                var clientAddr = proxy.ClientAddress ?? "";
                WriteLineTruncated($"  Client:         {AnsiSuccess}TUI @ {clientAddr}{AnsiReset}", winWidth);
                if (proxy.ClientConnectedFor is TimeSpan connectedFor)
                {
                    WriteLineTruncated($"  Connected:      {AnsiSuccess}{FormatDuration(connectedFor)}{AnsiReset}", winWidth);
                }
            }
            else if (CncWebServer.HasWebClient)
            {
                var webAddr = CncWebServer.WebClientAddress ?? "";
                WriteLineTruncated($"  Client:         {AnsiSuccess}Web @ {webAddr}{AnsiReset}", winWidth);
                WriteLineTruncated("", winWidth);
            }
            else
            {
                WriteLineTruncated($"  Client:         {AnsiDim}{StatusNone}{AnsiReset}", winWidth);
                WriteLineTruncated("", winWidth);
            }

            WriteLineTruncated($"  Bytes to client:   {AnsiDim}{proxy.BytesToClient,10:N0}{AnsiReset}", winWidth);
            WriteLineTruncated($"  Bytes from client: {AnsiDim}{proxy.BytesFromClient,10:N0}{AnsiReset}", winWidth);
            WriteLineTruncated("", winWidth);

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

            WriteLineTruncated($"  {AnsiDim}Press {AnsiReset}{AnsiInfo}q{AnsiReset}{AnsiDim} or {AnsiReset}{AnsiInfo}Escape{AnsiReset}{AnsiDim} to stop server{AnsiReset}", winWidth);
        }
    }
}
